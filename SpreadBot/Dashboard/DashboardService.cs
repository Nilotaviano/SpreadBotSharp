using Microsoft.EntityFrameworkCore;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SpreadBot.Dashboard
{
    /// <summary>
    /// Reads what the dashboard shows: live state from the running objects, history from the database (read-only queries on
    /// short-lived contexts; the bot's writer keeps writing meanwhile).
    /// </summary>
    internal class DashboardService
    {
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly Coordinator coordinator;
        private readonly ICoordinatorContext context;
        private readonly BalanceLedger ledger;
        private readonly Func<SpreadBotDbContext> database;
        private readonly bool pushAvailable;
        //History reads every finished bot; it changes only when one finishes (or equity is sampled), and the app asks often
        private static readonly TimeSpan HistoryLifetime = TimeSpan.FromSeconds(60);
        private readonly ConcurrentDictionary<int, (DateTime at, HistoryView view)> historyCache = new ConcurrentDictionary<int, (DateTime, HistoryView)>();

        public DateTime StartedAt { get; } = DateTime.UtcNow;

        public DashboardService(AppSettings appSettings, DataRepository dataRepository, Coordinator coordinator, ICoordinatorContext context, Func<SpreadBotDbContext> database, bool pushAvailable)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.coordinator = coordinator;
            this.context = context;
            this.database = database;
            this.pushAvailable = pushAvailable;
            ledger = new BalanceLedger(dataRepository, context);
        }

        public Coordinator Coordinator => coordinator;
        public DataRepository DataRepository => dataRepository;
        public Func<SpreadBotDbContext> Database => database;

        public IReadOnlyList<string> BaseMarkets =>
            coordinator.Configurations.Select(c => c.BaseMarket).Distinct().OrderBy(b => b).ToList();

        public StateView State()
        {
            var feeRate = dataRepository.Exchange.FeeRate;
            var bots = context.GetBots().ToList();

            var baseMarkets = BaseMarkets.Select(baseMarket =>
            {
                dataRepository.BalancesData.TryGetValue(baseMarket, out var balance);
                var onBase = bots.Where(b => b.BaseMarket == baseMarket).ToList();
                return new BaseMarketView(
                    baseMarket,
                    ledger.TotalValue(baseMarket),
                    ledger.UnallocatedBaseBalance(baseMarket),
                    balance?.Amount ?? 0,
                    balance?.Total ?? 0,
                    onBase.Count,
                    onBase.Sum(b => b.botContext.spreadConfiguration.AllocatedAmountOfBaseCurrency));
            }).ToList();

            var configurations = coordinator.Configurations.Select(c => new ConfigurationView(
                c.Strategy.ToString(), c.BaseMarket, c.AllocatedAmountOfBaseCurrency, c.MinimumSpreadPercentage, c.MinimumQuoteVolume,
                c.MinimumPrice, c.MinimumNegotiatedAmount, c.MaxPercentChangeFromPreviousDay, c.MinutesForLoss,
                bots.Count(b => b.botContext.spreadConfiguration.Equals(c)))).ToList();

            return new StateView(
                DateTime.UtcNow,
                StartedAt,
                appSettings.ExchangeId,
                appSettings.UseSandbox,
                feeRate,
                coordinator.IsHalted,
                coordinator.HaltReason,
                coordinator.IsAllocationPaused,
                appSettings.MaxNumberOfBots,
                baseMarkets,
                bots.Select(b => ToView(b, feeRate)).OrderBy(b => b.Market).ToList(),
                new StreamsView(dataRepository.LastMarketUpdate, dataRepository.LastBalanceUpdate, dataRepository.LastOrderUpdate, dataRepository.LastResync, dataRepository.ResyncCount, dataRepository.MarketsData.Count),
                configurations,
                context.GetDustPerMarket().Where(d => d.Value > 0).Select(d => new DustView(d.Key, d.Value)).OrderBy(d => d.Market).ToList(),
                pushAvailable);
        }

        private static BotView ToView(Bot bot, decimal feeRate)
        {
            //Each read once: the bot changes them on its own thread meanwhile
            var c = bot.botContext;
            var market = c.LatestMarketData;
            var order = c.CurrentOrderData;
            var configuration = c.spreadConfiguration;
            //What the bot would have if it sold what it holds at the bid now, after the fee
            var value = c.Balance + c.HeldAmount * (market.BidRate ?? 0) * (1 - feeRate);

            return new BotView(
                c.Guid,
                market.Symbol,
                bot.BaseMarket,
                market.Target,
                configuration.Strategy.ToString(),
                c.BotState.ToString(),
                configuration.AllocatedAmountOfBaseCurrency,
                c.Balance,
                c.HeldAmount,
                c.BoughtPrice,
                c.BreakEvenPrice(feeRate),
                c.BuyFilledAt,
                c.BuyFilledAt?.AddMinutes(configuration.MinutesForLoss),
                market.BidRate,
                market.AskRate,
                market.LastTradeRate,
                market.SpreadPercentage,
                market.PriceTick,
                value,
                value - configuration.AllocatedAmountOfBaseCurrency,
                bot.OrderRequestInFlight,
                order == null ? null : ToView(order, null, c.Guid));
        }

        private static OrderView ToView(OrderData o, DateTime? recordedAt, Guid? botId) => new OrderView(
            o.Id, o.ClientOrderId, o.MarketSymbol, o.Direction.ToString(), o.Type.ToString(), o.Status.ToString(),
            o.Quantity, o.Limit, o.FillQuantity, o.Proceeds, o.Commission, o.CoinCommission, o.CreatedAt, recordedAt, botId);

        /// <summary>
        /// The configured base markets' markets, best first (quote volume × spread, as allocation ranks them), with each configuration's verdict.
        /// </summary>
        public IReadOnlyList<MarketView> Markets(int take)
        {
            var configurations = coordinator.Configurations.ToList();
            var baseMarkets = configurations.Select(c => c.BaseMarket).ToHashSet();
            var botsPerMarket = context.GetBots().GroupBy(b => b.MarketSymbol).ToDictionary(g => g.Key, g => g.Count());

            return dataRepository.MarketsData.Values
                .Where(m => m.Symbol != null && m.Symbol.Contains('/') && baseMarkets.Contains(m.BaseMarket))
                .Select(m => (market: m, score: m.QuoteVolume.GetValueOrDefault() * m.SpreadPercentage))
                .OrderByDescending(m => m.score)
                .Take(take)
                .Select(m => new MarketView(
                    m.market.Symbol,
                    m.market.BaseMarket,
                    m.market.Status?.ToString(),
                    m.market.BidRate,
                    m.market.AskRate,
                    m.market.LastTradeRate,
                    m.market.SpreadPercentage,
                    m.market.QuoteVolume,
                    m.market.PercentChange,
                    m.market.PriceTick,
                    m.score,
                    botsPerMarket.GetValueOrDefault(m.market.Symbol),
                    configurations.Where(c => c.BaseMarket == m.market.BaseMarket)
                        .Select(c => new VerdictView(c.Strategy.ToString(), c.AllocatedAmountOfBaseCurrency, Coordinator.WhyNotAllocatable(m.market, c)))
                        .ToList()))
                .ToList();
        }

        /// <summary>
        /// Realized profit (finished bots) grouped every useful way, the equity curve, and the latest finished bots. SQLite can't sum
        /// decimals, so finished bots are aggregated here.
        /// </summary>
        public HistoryView History(int days)
        {
            if (historyCache.TryGetValue(days, out var cached) && DateTime.UtcNow - cached.at < HistoryLifetime)
                return cached.view;

            var view = ReadHistory(days);
            historyCache[days] = (DateTime.UtcNow, view);
            return view;
        }

        public void HistoryChanged() => historyCache.Clear();

        private HistoryView ReadHistory(int days)
        {
            var since = DateTime.UtcNow.AddDays(-days);
            using var db = database();

            var finished = db.Bots.AsNoTracking().Include(b => b.Configuration)
                .Where(b => b.FinishedAt != null && b.Profit != null)
                .ToList();
            var active = coordinator.Configurations.ToList();

            var byBase = finished.GroupBy(b => b.Configuration.BaseMarket)
                .Select(g => new ProfitTotalView(g.Key, g.Sum(b => b.Profit.Value), g.Count(), g.Count(b => b.Profit > 0),
                    g.Average(b => (b.FinishedAt.Value - b.AllocatedAt).TotalMinutes)))
                .OrderBy(p => p.BaseMarket).ToList();

            var byMarket = finished.GroupBy(b => (b.MarketSymbol, b.Configuration.BaseMarket))
                .Select(g => new ProfitGroupView(g.Key.MarketSymbol, g.Key.BaseMarket, g.Sum(b => b.Profit.Value), g.Count(), g.Count(b => b.Profit > 0), true))
                .OrderByDescending(p => p.Profit).ToList();

            var byConfiguration = finished.GroupBy(b => b.ConfigurationId)
                .Select(g =>
                {
                    var c = g.First().Configuration;
                    var name = $"{c.Strategy} · {c.AllocatedAmountOfBaseCurrency} {c.BaseMarket} · ≥{c.MinimumSpreadPercentage}% · {c.MinutesForLoss} min";
                    return new ProfitGroupView(name, c.BaseMarket, g.Sum(b => b.Profit.Value), g.Count(), g.Count(b => b.Profit > 0), active.Any(a => a.Equals(c)));
                })
                .OrderByDescending(p => p.Profit).ToList();

            var daily = finished.Where(b => b.FinishedAt >= since)
                .GroupBy(b => (Day: b.FinishedAt.Value.Date, b.Configuration.BaseMarket))
                .Select(g => new DailyProfitView(DateTime.SpecifyKind(g.Key.Day, DateTimeKind.Utc), g.Key.BaseMarket, g.Sum(b => b.Profit.Value), g.Count()))
                .OrderBy(d => d.Day).ToList();

            var equity = db.EquitySnapshots.AsNoTracking().Where(e => e.At >= since).OrderBy(e => e.At)
                .Select(e => new EquityPointView(e.At, e.BaseMarket, e.TotalValue, e.Unallocated, e.Bots))
                .ToList();

            var recent = finished.OrderByDescending(b => b.FinishedAt).Take(200)
                .Select(b => new FinishedBotView(b.Id, b.MarketSymbol, b.Configuration.BaseMarket, b.Configuration.Strategy.ToString(), b.AllocatedAt, b.FinishedAt, b.Profit, b.Configuration.AllocatedAmountOfBaseCurrency, b.State.ToString()))
                .ToList();

            return new HistoryView(byBase, byMarket, byConfiguration, daily, equity, recent);
        }

        /// <summary>
        /// Stored order states, newest first.
        /// </summary>
        public IReadOnlyList<OrderView> Orders(int take, Guid? botId, string market)
        {
            using var db = database();
            var query = db.Orders.AsNoTracking();
            if (botId.HasValue)
                query = query.Where(o => EF.Property<Guid>(o, "BotId") == botId.Value);
            if (!string.IsNullOrWhiteSpace(market))
                query = query.Where(o => o.MarketSymbol == market);

            return query
                .OrderByDescending(o => EF.Property<long>(o, "RecordId"))
                .Take(take)
                .Select(o => new { Order = o, RecordedAt = EF.Property<DateTime>(o, "RecordedAt"), BotId = EF.Property<Guid>(o, "BotId") })
                .AsEnumerable()
                .Select(o => ToView(o.Order, o.RecordedAt, o.BotId))
                .ToList();
        }

        public IReadOnlyList<EquitySnapshotRow> EquityNow() =>
            BaseMarkets.Select(baseMarket => new EquitySnapshotRow
            {
                At = DateTime.UtcNow,
                BaseMarket = baseMarket,
                TotalValue = ledger.TotalValue(baseMarket),
                Unallocated = ledger.UnallocatedBaseBalance(baseMarket),
                Bots = context.GetBots().Count(b => b.BaseMarket == baseMarket)
            }).ToList();
    }
}
