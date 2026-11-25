using Microsoft.EntityFrameworkCore;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
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
        //Full, a new message is refused (and completed) rather than an old one dropped unseen
        private readonly Channel<PushMessage> queue = Channel.CreateBounded<PushMessage>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly Task sender;
        //Sends outside the queue (the halt), under their own lock: Dispose waits for them before cancelling anything
        private readonly List<Task> urgent = new List<Task>();
        //Under the urgent lock: once Dispose has taken its snapshot, no send outside the queue starts
        private bool closing;
        private readonly object subscribing = new object();
        //Endpoint -> refusals in a row (401/403): one may be the bot's own key or clock, several mean the subscription is dead
        private readonly Dictionary<string, int> refusals = new Dictionary<string, int>();
        private const int RefusalsBeforeDropping = 3;

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
            endpoint.Scheme == Uri.UriSchemeHttps && endpoint.IsDefaultPort && string.IsNullOrEmpty(endpoint.UserInfo)
            && (PushHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase) || PushHostSuffixes.Any(suffix => endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

        public bool Available => vapid != null;

        public string PublicKey => vapid?.PublicKey;

        /// <returns>False when the device would be one too many</returns>
        public bool Subscribe(PushSubscriptionRequest request)
        {
            var categories = string.Join(",", (request.Categories ?? Categories).Where(Categories.Contains).Distinct());

            //The cap is a count then an insert
            lock (subscribing)
                return Store(request, categories);
        }

        private bool Store(PushSubscriptionRequest request, string categories)
        {
            //Subscribing again starts over
            lock (refusals)
                refusals.Remove(request.Endpoint);

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
            lock (refusals)
                refusals.Remove(endpoint);

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

            var message = Message(category, title, body, tag, path);
            if (!queue.Writer.TryWrite(message))
                message.Sent.TrySetResult();
            return message.Sent.Task;
        }

        /// <summary>
        /// Sends now, ahead of the queue: for the halt, the last thing the bot reports before it exits.
        /// </summary>
        public Task NotifyNow(string category, string title, string body, string tag = null)
        {
            if (!Available)
                return Task.CompletedTask;

            var message = Message(category, title, body, tag, "/");
            //Started and tracked as one step: Dispose either waits for it or it never starts
            lock (urgent)
            {
                if (closing)
                {
                    Logger.Instance.LogError($"The {category} push notification wasn't sent: the dashboard is stopping");
                    return Task.CompletedTask;
                }

                urgent.RemoveAll(task => task.IsCompleted);
                var send = Task.Run(() => SendNow(message));
                urgent.Add(send);
                return send;
            }
        }

        private PushMessage Message(string category, string title, string body, string tag, string path) =>
            new PushMessage(category, JsonSerializer.Serialize(new { title, body, tag = tag ?? category, category, url = publicUrl + path }, json),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        private async Task Send()
        {
            try
            {
                await foreach (var message in queue.Reader.ReadAllAsync(stopping.Token))
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
            catch (OperationCanceledException)
            {
            }

            //Stopped with messages left: they're not sent, but nobody waits on them forever
            while (queue.Reader.TryRead(out var left))
                left.Sent.TrySetResult();
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
                    if (stopping.IsCancellationRequested)
                        break;

                    try
                    {
                        await client.SendNotificationAsync(new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth), message.Payload, vapid, stopping.Token);
                        lock (refusals)
                            refusals.Remove(subscription.Endpoint);
                    }
                    //The browser dropped the subscription (uninstalled, permission revoked)
                    catch (WebPushException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
                    {
                        gone.Add(subscription.Endpoint);
                    }
                    //Made with other keys (a new secrets file), or the bot's own key or clock is off: only repeated refusals drop it
                    catch (WebPushException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    {
                        int count;
                        lock (refusals)
                            refusals[subscription.Endpoint] = count = refusals.GetValueOrDefault(subscription.Endpoint) + 1;

                        Logger.Instance.LogError($"Push service {new Uri(subscription.Endpoint).Host} refused a notification ({(int)e.StatusCode}), {count} in a row{(count >= RefusalsBeforeDropping ? "; dropping that device" : "")}");
                        if (count >= RefusalsBeforeDropping)
                            gone.Add(subscription.Endpoint);
                    }
                    //Cut short by shutdown: not a failure
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        break;
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
            if (stopping.IsCancellationRequested)
                return;

            //The halt gets its chance first; then what's queued isn't sent once the bot exits, and a send under way is cancelled
            Task urgentSends;
            lock (urgent)
            {
                closing = true;
                urgentSends = Task.WhenAll(urgent);
            }
            urgentSends.Wait(TimeSpan.FromSeconds(10));
            queue.Writer.TryComplete();
            stopping.Cancel();
            if (!sender.Wait(TimeSpan.FromSeconds(15)) || !urgentSends.Wait(TimeSpan.FromSeconds(5)))
            {
                //Still using the client: leave it to the process exit rather than dispose it under a send
                Logger.Instance.LogError("Push notifications were still sending when the dashboard stopped");
                return;
            }
            client.Dispose();
            http.Dispose();
        }
    }
}
