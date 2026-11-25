using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using SpreadBot.Logic;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SpreadBot.Dashboard
{
    /// <summary>
    /// The dashboard: a PWA and its JSON API, served by the bot itself on <see cref="DashboardSettings.Url"/> (this machine only by
    /// default). Every /api call needs the access token from <see cref="DashboardSecrets"/>; actions also need a typed confirmation.
    /// </summary>
    internal sealed class DashboardHost : IAsyncDisposable
    {
        public static readonly JsonSerializerOptions Json = CreateJsonOptions();

        private readonly WebApplication app;
        private readonly ActivityMonitor monitor;
        private readonly PushNotifier push;

        private DashboardHost(WebApplication app, ActivityMonitor monitor, PushNotifier push)
        {
            this.app = app;
            this.monitor = monitor;
            this.push = push;
        }

        internal WebApplication App => app;
        internal ActivityMonitor Monitor => monitor;

        private static JsonSerializerOptions CreateJsonOptions()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        public static DashboardHost Create(DashboardSettings settings, DashboardSecrets secrets, AppSettings appSettings, DataRepository dataRepository,
            Coordinator coordinator, ICoordinatorContext context, Func<SpreadBotDbContext> database, Action<IWebHostBuilder> configureHost = null)
        {
            var publicUrl = settings.PublicUrl;
            if (!string.IsNullOrWhiteSpace(publicUrl) && !(Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
            {
                Logger.Instance.LogError($"Dashboard.PublicUrl must be the https address the dashboard is opened at; push notifications are off");
                publicUrl = null;
            }

            var push = new PushNotifier(database, secrets, publicUrl, Json);
            var service = new DashboardService(appSettings, dataRepository, coordinator, context, database, push.Available);
            var feed = new LiveFeed(Json);
            var monitor = new ActivityMonitor(service, feed, push);

            //The bin directory: no web content or settings to pick up there
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(settings.Url);
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
            configureHost?.Invoke(builder.WebHost);

            var app = builder.Build();
            var token = Encoding.UTF8.GetBytes(secrets.AccessToken);

            app.Use(async (http, next) =>
            {
                var headers = http.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Frame-Options"] = "DENY";
                headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

                if (http.Request.Path.StartsWithSegments("/api") && !Authorized(http.Request, token))
                {
                    //Slows guessing down; the token is 256 random bits anyway
                    await Task.Delay(300);
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            });

            var files = new ManifestEmbeddedFileProvider(typeof(DashboardHost).Assembly, "Dashboard/wwwroot");
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                //Small files, and the service worker decides what's cached offline
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
            });

            app.MapGet("/healthz", () => Results.Text("ok"));

            var api = app.MapGroup("/api");
            api.MapGet("/state", () => service.State());
            api.MapGet("/markets", (int? take) => service.Markets(Math.Clamp(take ?? 150, 1, 2000)));
            api.MapGet("/history", (int? days) => service.History(Math.Clamp(days ?? 30, 1, 3650)));
            api.MapGet("/orders", (int? take, Guid? botId, string market) => service.Orders(Math.Clamp(take ?? 200, 1, 2000), botId, market));
            api.MapGet("/activity", () => feed.RecentActivity());
            api.MapGet("/logs", () => feed.RecentLogs());
            api.MapGet("/stream", (HttpContext http, CancellationToken aborted) => Stream(http, feed, service, aborted));

            api.MapPost("/controls/allocation", (AllocationRequest request) =>
            {
                coordinator.SetAllocationPaused(request.Paused);
                return Results.Ok(new { paused = coordinator.IsAllocationPaused });
            });
            api.MapPost("/controls/resync", () =>
            {
                Logger.Instance.LogMessage("Resync requested from the dashboard");
                dataRepository.RequestResync();
                return Results.Accepted();
            });
            api.MapPost("/controls/halt", (ConfirmRequest request) =>
            {
                if (request?.Confirm != "HALT")
                    return Results.BadRequest(new { error = "Type HALT to confirm" });

                coordinator.Halt("Halted from the dashboard");
                return Results.Accepted();
            });
            api.MapPost("/bots/{id:guid}/finish", (Guid id, ConfirmRequest request) =>
            {
                var bot = context.GetBots().FirstOrDefault(b => b.Guid == id);
                if (bot == null)
                    return Results.NotFound(new { error = "No such bot is running" });
                if (!string.Equals(request?.Confirm, bot.MarketSymbol, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = $"Type {bot.MarketSymbol} to confirm" });

                return coordinator.FinishBot(id) ? Results.Accepted() : Results.NotFound(new { error = "No such bot is running" });
            });

            api.MapGet("/push", () => new { available = push.Available, publicKey = push.PublicKey, categories = PushNotifier.Categories });
            api.MapPost("/push/subscriptions", (PushSubscriptionRequest request) =>
            {
                if (!push.Available)
                    return Results.BadRequest(new { error = "Push notifications need Dashboard.PublicUrl" });
                if (!Uri.TryCreate(request?.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
                    || string.IsNullOrWhiteSpace(request.Keys?.P256dh) || string.IsNullOrWhiteSpace(request.Keys?.Auth))
                    return Results.BadRequest(new { error = "Not a push subscription" });

                push.Subscribe(request);
                return Results.Ok(new { categories = push.CategoriesOf(request.Endpoint) });
            });
            api.MapPost("/push/subscriptions/lookup", (PushSubscriptionRequest request) =>
                Results.Ok(new { categories = push.CategoriesOf(request?.Endpoint) }));
            api.MapPost("/push/subscriptions/remove", (PushSubscriptionRequest request) =>
                push.Unsubscribe(request?.Endpoint) ? Results.Ok() : Results.NotFound());
            api.MapPost("/push/test", async () =>
            {
                await push.Notify("test", "SpreadBot", "Push notifications work", tag: "test");
                return Results.Ok();
            });

            return new DashboardHost(app, monitor, push);
        }

        private static bool Authorized(HttpRequest request, byte[] token)
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
                return false;

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header.Substring("Bearer ".Length).Trim()), token);
        }

        /// <summary>
        /// Server-sent events: the state right away, then whatever the feed publishes, with a comment every 15 s so proxies keep it open.
        /// Read with fetch (EventSource can't send the token).
        /// </summary>
        private static async Task Stream(HttpContext http, LiveFeed feed, DashboardService service, CancellationToken aborted)
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var (id, reader) = feed.Connect();
            try
            {
                await http.Response.WriteAsync($"event: state\ndata: {JsonSerializer.Serialize(service.State(), Json)}\n\n", aborted);
                await http.Response.Body.FlushAsync(aborted);

                while (!aborted.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                    wait.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        var message = await reader.ReadAsync(wait.Token);
                        await http.Response.WriteAsync(message, aborted);
                    }
                    catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", aborted);
                    }
                    await http.Response.Body.FlushAsync(aborted);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
            finally
            {
                feed.Disconnect(id);
            }
        }

        public async Task StartAsync()
        {
            await app.StartAsync();
            monitor.Start();
        }

        public Task AnnounceHalt(string reason) => monitor.AnnounceHalt(reason);

        public async ValueTask DisposeAsync()
        {
            monitor.Dispose();
            try
            {
                await app.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch (Exception e)
            {
                Logger.Instance.LogError($"Stopping the dashboard failed: {e.Message}");
            }
            await app.DisposeAsync();
            push.Dispose();
        }
    }
}
