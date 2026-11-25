using System;
using System.Collections.Generic;

namespace SpreadBot.Dashboard
{
    //What the dashboard's API returns (System.Text.Json, camelCase). Prices and amounts are decimals: JSON numbers

    public record StateView(
        DateTime Now,
        DateTime StartedAt,
        string Exchange,
        bool Sandbox,
        decimal FeeRate,
        bool Halted,
        string HaltReason,
        bool AllocationPaused,
        int MaxBots,
        IReadOnlyList<BaseMarketView> BaseMarkets,
        IReadOnlyList<BotView> Bots,
        StreamsView Streams,
        IReadOnlyList<ConfigurationView> Configurations,
        IReadOnlyList<DustView> Dust,
        bool PushAvailable);

    public record BaseMarketView(string Currency, decimal TotalValue, decimal Unallocated, decimal Available, decimal Total, int Bots, decimal Allocated);

    public record BotView(
        Guid Id,
        string Market,
        string BaseMarket,
        string Coin,
        string Strategy,
        string State,
        decimal Allocated,
        decimal Balance,
        decimal Held,
        decimal BoughtPrice,
        decimal BreakEven,
        DateTime? BuyFilledAt,
        DateTime? SellAtLossFrom,
        decimal? Bid,
        decimal? Ask,
        decimal? Last,
        decimal SpreadPercentage,
        decimal? Tick,
        decimal Value,
        decimal UnrealizedProfit,
        bool OrderRequestInFlight,
        OrderView Order);

    public record OrderView(
        string Id,
        string ClientOrderId,
        string Market,
        string Side,
        string Type,
        string Status,
        decimal Quantity,
        decimal Limit,
        decimal Filled,
        decimal Proceeds,
        decimal Commission,
        decimal CoinCommission,
        DateTime CreatedAt,
        DateTime? RecordedAt,
        Guid? BotId);

    public record StreamsView(DateTime? LastMarketUpdate, DateTime? LastBalanceUpdate, DateTime? LastOrderUpdate, DateTime? LastResync, int Resyncs, int Markets);

    public record ConfigurationView(
        string Strategy,
        string BaseMarket,
        decimal AllocatedAmountOfBaseCurrency,
        decimal MinimumSpreadPercentage,
        decimal MinimumQuoteVolume,
        decimal MinimumPrice,
        decimal MinimumNegotiatedAmount,
        decimal MaxPercentChangeFromPreviousDay,
        int MinutesForLoss,
        int Bots);

    public record DustView(string Market, decimal Amount);

    public record MarketView(
        string Symbol,
        string BaseMarket,
        string Status,
        decimal? Bid,
        decimal? Ask,
        decimal? Last,
        decimal SpreadPercentage,
        decimal? QuoteVolume,
        decimal? PercentChange,
        decimal? Tick,
        decimal Score,
        int Bots,
        IReadOnlyList<VerdictView> Verdicts);

    public record VerdictView(string Strategy, decimal AllocatedAmountOfBaseCurrency, string Rejection);

    public record HistoryView(
        IReadOnlyList<ProfitTotalView> ProfitByBaseMarket,
        IReadOnlyList<ProfitGroupView> ProfitByMarket,
        IReadOnlyList<ProfitGroupView> ProfitByConfiguration,
        IReadOnlyList<DailyProfitView> DailyProfit,
        IReadOnlyList<EquityPointView> Equity,
        IReadOnlyList<FinishedBotView> FinishedBots);

    public record ProfitTotalView(string BaseMarket, decimal Profit, int Bots, int Winners, double AverageMinutes);

    public record ProfitGroupView(string Name, string BaseMarket, decimal Profit, int Bots, int Winners, bool Active);

    public record DailyProfitView(DateTime Day, string BaseMarket, decimal Profit, int Bots);

    public record EquityPointView(DateTime At, string BaseMarket, decimal TotalValue, decimal Unallocated, int Bots);

    public record FinishedBotView(Guid Id, string Market, string BaseMarket, string Strategy, DateTime AllocatedAt, DateTime? FinishedAt, decimal? Profit, decimal Allocated, string State);

    public record ActivityView(string Kind, DateTime At, string Title, string Detail, string Market, Guid? BotId, decimal? Amount);

    public record LogView(DateTime At, string Level, string Content);

    public record PushSubscriptionRequest(string Endpoint, PushKeys Keys, IReadOnlyList<string> Categories);

    public record PushKeys(string P256dh, string Auth);

    public record ConfirmRequest(string Confirm);

    public record AllocationRequest(bool Paused);
}
