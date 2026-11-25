using Microsoft.EntityFrameworkCore;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using WebPush;

namespace SpreadBot.Dashboard
{
    /// <summary>
    /// Web Push to the browsers that subscribed, each for the categories it chose. Needs <see cref="DashboardSettings.PublicUrl"/>:
    /// push services want the sender's address, and the notifications link back to it.
    /// </summary>
    internal sealed class PushNotifier : IDisposable
    {
        public static readonly IReadOnlyList<string> Categories = new[] { "halt", "finished", "fills", "errors", "streams" };

        private readonly Func<SpreadBotDbContext> database;
        private readonly VapidDetails vapid;
        private readonly string publicUrl;
        private readonly WebPushClient client = new WebPushClient();
        private readonly JsonSerializerOptions json;

        public PushNotifier(Func<SpreadBotDbContext> database, DashboardSecrets secrets, string publicUrl, JsonSerializerOptions json)
        {
            this.database = database;
            this.publicUrl = publicUrl?.TrimEnd('/');
            this.json = json;
            if (!string.IsNullOrWhiteSpace(this.publicUrl))
                vapid = new VapidDetails(this.publicUrl, secrets.VapidPublicKey, secrets.VapidPrivateKey);
        }

        public bool Available => vapid != null;

        public string PublicKey => vapid?.PublicKey;

        public void Subscribe(PushSubscriptionRequest request)
        {
            var categories = string.Join(",", (request.Categories ?? Categories).Where(Categories.Contains).Distinct());

            using var db = database();
            var stored = db.PushSubscriptions.Find(request.Endpoint);
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
        /// Sends to every subscription that chose <paramref name="category"/>. Never throws: a notification is never worth failing the caller for.
        /// </summary>
        public async Task Notify(string category, string title, string body, string tag = null, string path = "/")
        {
            if (!Available)
                return;

            try
            {
                List<PushSubscriptionRow> subscriptions;
                using (var db = database())
                    subscriptions = db.PushSubscriptions.AsNoTracking().ToList()
                        //A test goes to everyone
                        .Where(s => category == "test" || s.Categories.Split(',').Contains(category))
                        .ToList();

                var payload = JsonSerializer.Serialize(new { title, body, tag = tag ?? category, category, url = publicUrl + path }, json);
                var gone = new List<string>();

                foreach (var subscription in subscriptions)
                {
                    try
                    {
                        await client.SendNotificationAsync(new PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth), payload, vapid);
                    }
                    //The browser dropped the subscription (uninstalled, permission revoked)
                    catch (WebPushException e) when (e.StatusCode == HttpStatusCode.Gone || e.StatusCode == HttpStatusCode.NotFound)
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

        public void Dispose() => client.Dispose();
    }
}
