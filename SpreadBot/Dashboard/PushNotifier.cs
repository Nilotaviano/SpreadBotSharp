using Microsoft.EntityFrameworkCore;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using WebPush;

namespace SpreadBot.Dashboard
{
    /// <summary>
    /// Web Push to the browsers that subscribed, each for the categories it chose. Needs <see cref="DashboardSettings.PublicUrl"/>:
    /// push services want the sender's address, and the notifications link back to it. Sends go out one at a time from a queue, so a
    /// slow push service delays the others instead of piling requests up.
    /// </summary>
    internal sealed class PushNotifier : IDisposable
    {
        public static readonly IReadOnlyList<string> Categories = new[] { "halt", "finished", "fills", "errors", "streams" };
        public const int MaxSubscriptions = 20;

        //Chrome, Firefox, Edge and Safari's push services
        private static readonly string[] PushHosts = { "fcm.googleapis.com", "updates.push.services.mozilla.com", "web.push.apple.com" };
        private static readonly string[] PushHostSuffixes = { ".push.services.mozilla.com", ".notify.windows.com", ".push.apple.com" };

        private readonly Func<SpreadBotDbContext> database;
        private readonly VapidDetails vapid;
        private readonly string publicUrl;
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly WebPushClient client;
        private readonly JsonSerializerOptions json;
        private readonly Channel<PushMessage> queue = Channel.CreateBounded<PushMessage>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Task sender;

        private record PushMessage(string Category, string Payload, TaskCompletionSource Sent);

        public PushNotifier(Func<SpreadBotDbContext> database, DashboardSecrets secrets, string publicUrl, JsonSerializerOptions json)
        {
            this.database = database;
            this.publicUrl = publicUrl?.TrimEnd('/');
            this.json = json;
            client = new WebPushClient(http);
            if (!string.IsNullOrWhiteSpace(this.publicUrl))
                vapid = new VapidDetails(this.publicUrl, secrets.VapidPublicKey, secrets.VapidPrivateKey);
            sender = Task.Run(Send);
        }

        public static bool IsPushService(Uri endpoint) =>
            endpoint.Scheme == Uri.UriSchemeHttps
            && (PushHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase) || PushHostSuffixes.Any(suffix => endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

        public bool Available => vapid != null;

        public string PublicKey => vapid?.PublicKey;

        /// <returns>False when the device would be one too many</returns>
        public bool Subscribe(PushSubscriptionRequest request)
        {
            var categories = string.Join(",", (request.Categories ?? Categories).Where(Categories.Contains).Distinct());

            using var db = database();
            var stored = db.PushSubscriptions.Find(request.Endpoint);
            if (stored == null && db.PushSubscriptions.Count() >= MaxSubscriptions)
                return false;
            if (stored == null)
                db.PushSubscriptions.Add(new PushSubscriptionRow
                {
                    Endpoint = request.Endpoint,
                    P256dh = request.Keys.P256dh,
                    Auth = request.Keys.Auth,
                    Categories = categories,
                    CreatedAt = DateTime.UtcNow
                });
            else
            {
                stored.P256dh = request.Keys.P256dh;
                stored.Auth = request.Keys.Auth;
                stored.Categories = categories;
            }
            db.SaveChanges();
            return true;
        }

        public bool Unsubscribe(string endpoint)
        {
            using var db = database();
            var stored = db.PushSubscriptions.Find(endpoint);
            if (stored == null)
                return false;
            db.PushSubscriptions.Remove(stored);
            db.SaveChanges();
            return true;
        }

        public IReadOnlyList<string> CategoriesOf(string endpoint)
        {
            using var db = database();
            return db.PushSubscriptions.AsNoTracking().Where(s => s.Endpoint == endpoint).Select(s => s.Categories).FirstOrDefault()?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Queues a notification for every subscription that chose <paramref name="category"/> (a test goes to all). Never throws: a
        /// notification is never worth failing the caller for. The task completes once it's been sent, or dropped from a full queue.
        /// </summary>
        public Task Notify(string category, string title, string body, string tag = null, string path = "/")
        {
            if (!Available)
                return Task.CompletedTask;

            var payload = JsonSerializer.Serialize(new { title, body, tag = tag ?? category, category, url = publicUrl + path }, json);
            var message = new PushMessage(category, payload, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!queue.Writer.TryWrite(message))
                message.Sent.TrySetResult();
            return message.Sent.Task;
        }

        private async Task Send()
        {
            await foreach (var message in queue.Reader.ReadAllAsync())
            {
                try
                {
                    await SendNow(message);
                }
                finally
                {
                    message.Sent.TrySetResult();
                }
            }
        }

        private async Task SendNow(PushMessage message)
        {
            var category = message.Category;
            try
            {
                List<PushSubscriptionRow> subscriptions;
                using (var db = database())
                    subscriptions = db.PushSubscriptions.AsNoTracking().ToList()
                        .Where(s => category == "test" || s.Categories.Split(',').Contains(category))
                        .ToList();

                var gone = new List<string>();

                foreach (var subscription in subscriptions)
                {
                    try
                    {
                        await client.SendNotificationAsync(new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth), message.Payload, vapid);
                    }
                    //The browser dropped the subscription (uninstalled, permission revoked), or it was made with other keys (a new secrets file)
                    catch (WebPushException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    {
                        gone.Add(subscription.Endpoint);
                    }
                    catch (Exception e)
                    {
                        //Not an unexpected error: those are pushed, and this one would push itself
                        Logger.Instance.LogError($"Push notification to {new Uri(subscription.Endpoint).Host} failed: {e.Message}");
                    }
                }

                foreach (var endpoint in gone)
                    Unsubscribe(endpoint);
            }
            catch (Exception e)
            {
                Logger.Instance.LogError($"Sending {category} push notifications failed: {e.Message}");
            }
        }

        public void Dispose()
        {
            queue.Writer.TryComplete();
            sender.Wait(TimeSpan.FromSeconds(10));
            client.Dispose();
            http.Dispose();
        }
    }
}
