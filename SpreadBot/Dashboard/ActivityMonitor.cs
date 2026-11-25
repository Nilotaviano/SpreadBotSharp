using Microsoft.EntityFrameworkCore;
using SpreadBot.Infrastructure;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Dashboard
{
    /// <summary>
    /// Turns what the bot does into the dashboard's live events and push notifications: state snapshots for open dashboards, fills and
    /// finished bots (read from the database's append-only records, so nothing in the bots changes for it), logs, streams going quiet,
    /// and an equity sample every few minutes.
    /// </summary>
    internal sealed class ActivityMonitor : IDisposable
    {
        internal static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan EquityInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan QuietStream = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ErrorPushWindow = TimeSpan.FromMinutes(10);
        //A bot's FinishedAt is stamped before its row is queued, and two bots can be queued out of stamp order: look back this far
        private static readonly TimeSpan FinishedLookback = TimeSpan.FromMinutes(1);

        private readonly DashboardService service;
        private readonly LiveFeed feed;
        private readonly PushNotifier push;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private Task loop;

        private long lastOrderRecordId;
        private DateTime lastFinishedAt;
        private readonly Dictionary<Guid, DateTime> reportedFinished = new Dictionary<Guid, DateTime>();
        private DateTime nextEquitySample;
        private bool streamQuiet;

        private readonly object errorsLock = new object();
        private DateTime errorWindowEnds;
        private int errorsInWindow;

        public ActivityMonitor(DashboardService service, LiveFeed feed, PushNotifier push)
        {
            this.service = service;
            this.feed = feed;
            this.push = push;
        }

        public void Start()
        {
            //Only what happens from now on is news
            using (var db = service.Database())
                lastOrderRecordId = db.Orders.Select(o => EF.Property<long>(o, "RecordId")).OrderByDescending(id => id).FirstOrDefault();
            lastFinishedAt = DateTime.UtcNow;
            nextEquitySample = DateTime.UtcNow;

            Logger.Instance.Logged += OnLogged;
            loop = Task.Run(Run);
        }

        private async Task Run()
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    Poll();
                }
                catch (Exception e)
                {
                    Logger.Instance.LogError($"The dashboard's activity check failed: {e.Message}");
                }

                try
                {
                    await Task.Delay(Tick, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        internal void Poll()
        {
            //Each on its own: one failing must not silence the others
            Step("state", () =>
            {
                if (feed.Connections > 0)
                    feed.Publish("state", service.State());
            });
            Step("orders", PollOrders);
            Step("finished bots", PollFinishedBots);
            Step("streams", CheckStreams);
            Step("errors", FlushErrorSummary);
            Step("equity", SampleEquity);
        }

        private static void Step(string name, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Logger.Instance.LogError($"The dashboard's {name} check failed: {e.Message}");
            }
        }

        private void SampleEquity()
        {
            if (DateTime.UtcNow < nextEquitySample)
                return;

            nextEquitySample = DateTime.UtcNow + EquityInterval;
            using var db = service.Database();
            db.EquitySnapshots.AddRange(service.EquityNow());
            db.SaveChanges();
            service.HistoryChanged();
        }

        private void PollOrders()
        {
            using var db = service.Database();
            var states = db.Orders.AsNoTracking()
                .Where(o => EF.Property<long>(o, "RecordId") > lastOrderRecordId)
                .OrderBy(o => EF.Property<long>(o, "RecordId"))
                .Take(500)
                .Select(o => new { Order = o, RecordId = EF.Property<long>(o, "RecordId"), BotId = EF.Property<Guid>(o, "BotId") })
                .ToList();

            foreach (var state in states)
            {
                lastOrderRecordId = state.RecordId;
                var order = state.Order;
                var (coin, quote) = Split(order.MarketSymbol);
                var side = order.Direction == OrderDirection.BUY ? "Buy" : "Sell";

                if (order.Status == OrderStatus.CLOSED && order.FillQuantity > 0)
                {
                    var price = order.FillQuantity > 0 && order.Proceeds > 0 ? order.Proceeds / order.FillQuantity : order.Limit;
                    var title = $"{(order.Direction == OrderDirection.BUY ? "Bought" : "Sold")} {Amount(order.FillQuantity)} {coin}";
                    var detail = $"at {Amount(price)} {quote} · {Amount(order.Proceeds)} {quote}";
                    feed.Activity(new ActivityView("fill", DateTime.UtcNow, title, detail, order.MarketSymbol, state.BotId, order.Proceeds));
                    _ = push.Notify("fills", title, detail, tag: $"fill-{order.ClientOrderId}", path: "/#bots");
                }
                else if (order.Status == OrderStatus.CLOSED)
                    feed.Activity(new ActivityView("cancelled", DateTime.UtcNow, $"{side} cancelled", $"{order.MarketSymbol} at {Amount(order.Limit)}", order.MarketSymbol, state.BotId, null));
                else if (order.Status == OrderStatus.UNKNOWN)
                    feed.Activity(new ActivityView("unknown", DateTime.UtcNow, $"{side} placement unknown", $"{order.MarketSymbol}: looking it up", order.MarketSymbol, state.BotId, null));
                else if (order.FillQuantity == 0)
                    feed.Activity(new ActivityView("placed", DateTime.UtcNow, $"{side} placed", $"{Amount(order.Quantity)} {coin} at {Amount(order.Limit)} {quote}", order.MarketSymbol, state.BotId, null));
                else
                    feed.Activity(new ActivityView("partial", DateTime.UtcNow, $"{side} partly filled", $"{Amount(order.FillQuantity)} of {Amount(order.Quantity)} {coin}", order.MarketSymbol, state.BotId, null));
            }
        }

        private void PollFinishedBots()
        {
            using var db = service.Database();
            var since = lastFinishedAt - FinishedLookback;
            var finished = db.Bots.AsNoTracking().Include(b => b.Configuration)
                .Where(b => b.FinishedAt > since && b.Profit != null)
                .ToList()
                .Where(b => !reportedFinished.ContainsKey(b.Id))
                .OrderBy(b => b.FinishedAt)
                .ToList();

            foreach (var stale in reportedFinished.Where(r => r.Value < since).Select(r => r.Key).ToList())
                reportedFinished.Remove(stale);

            if (finished.Count > 0)
                service.HistoryChanged();

            foreach (var bot in finished)
            {
                reportedFinished[bot.Id] = bot.FinishedAt.Value;
                if (bot.FinishedAt.Value > lastFinishedAt)
                    lastFinishedAt = bot.FinishedAt.Value;
                var profit = bot.Profit.Value;
                var title = $"{bot.MarketSymbol} finished {(profit >= 0 ? "+" : "")}{Amount(profit)} {bot.Configuration.BaseMarket}";
                var detail = $"{bot.Configuration.Strategy} · held {Minutes(bot.FinishedAt.Value - bot.AllocatedAt)}";
                feed.Activity(new ActivityView("finished", DateTime.UtcNow, title, detail, bot.MarketSymbol, bot.Id, profit));
                _ = push.Notify("finished", title, detail, tag: $"finished-{bot.Id}", path: "/#history");
            }
        }

        private void CheckStreams()
        {
            var last = service.DataRepository.LastMarketUpdate;
            var quiet = DateTime.UtcNow - (last ?? service.StartedAt) > QuietStream;

            if (quiet == streamQuiet)
                return;

            streamQuiet = quiet;
            var title = quiet ? "Market data stopped" : "Market data is back";
            var detail = quiet ? $"No update for {QuietStream.TotalMinutes:0} minutes; the bots aren't seeing prices" : "Prices are flowing again";
            feed.Activity(new ActivityView("streams", DateTime.UtcNow, title, detail, null, null, null));
            _ = push.Notify("streams", title, detail, tag: "streams");
        }

        private void OnLogged(Logger.LogEntry entry)
        {
            //Only hands off: this runs on the logger's thread
            feed.Log(new LogView(entry.At, entry.Level, entry.Content));

            if (entry.Level != "UnexpectedError")
                return;

            bool pushNow;
            lock (errorsLock)
            {
                pushNow = DateTime.UtcNow >= errorWindowEnds;
                if (pushNow)
                    errorWindowEnds = DateTime.UtcNow + ErrorPushWindow;
                else
                    errorsInWindow++;
            }

            if (pushNow)
            {
                var text = entry.Content.Length > 180 ? entry.Content.Substring(0, 180) + "…" : entry.Content;
                _ = Task.Run(() => push.Notify("errors", "Unexpected error", text, tag: "errors", path: "/#logs"));
            }
        }

        //One notification per window: the errors that followed the first are summed up once it ends
        private void FlushErrorSummary()
        {
            int count;
            lock (errorsLock)
            {
                if (errorsInWindow == 0 || DateTime.UtcNow < errorWindowEnds)
                    return;
                count = errorsInWindow;
                errorsInWindow = 0;
                errorWindowEnds = DateTime.UtcNow + ErrorPushWindow;
            }

            _ = push.Notify("errors", $"{count} more unexpected error{(count == 1 ? "" : "s")}", "See the dashboard's log", tag: "errors", path: "/#logs");
        }

        /// <summary>
        /// The halt is the last thing the bot reports before it exits, so this waits (a little) for it to be sent.
        /// </summary>
        public async Task AnnounceHalt(string reason)
        {
            feed.Activity(new ActivityView("halt", DateTime.UtcNow, "Trading halted", reason, null, null, null));
            await Task.WhenAny(push.Notify("halt", "Trading halted", reason ?? "The bot stopped trading and is exiting", tag: "halt"), Task.Delay(TimeSpan.FromSeconds(10)));
        }

        private static (string coin, string quote) Split(string symbol)
        {
            var parts = symbol?.Split('/') ?? Array.Empty<string>();
            return parts.Length == 2 ? (parts[0], parts[1]) : (symbol, "");
        }

        private static string Amount(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);

        private static string Minutes(TimeSpan span) =>
            span.TotalMinutes < 90 ? $"{span.TotalMinutes:0} min" : span.TotalHours < 48 ? $"{span.TotalHours:0.#} h" : $"{span.TotalDays:0.#} days";

        public void Dispose()
        {
            Logger.Instance.Logged -= OnLogged;
            stopping.Cancel();
            try
            {
                loop?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
            stopping.Dispose();
        }
    }
}
