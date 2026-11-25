using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    /// <summary>
    /// Trades like the strategy it wraps, but bids only while the market is calm: its mid price has moved less than
    /// <see cref="SpreadConfiguration.MinimumSpreadPercentage"/> over the last <see cref="Window"/>, from a mid it saw at or before the
    /// window's start.
    /// A market moving faster than the spread is wide fills bids on the way down and leaves the bot holding below its sell floor.
    /// Not calm, it holds off on buying (cancelling a resting buy, a restored bot's too until it has watched a window) instead of
    /// finishing, so it can bid once the market settles. Selling is never held back.
    /// </summary>
    public class CalmSpreadStrategy : IBotStrategy
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        private readonly IBotStrategy strategy;
        private readonly Func<DateTime> utcNow;

        //This bot's own observations: a restart starts over, and the bot waits a window before bidding again
        private readonly Queue<(DateTime at, decimal mid)> mids = new Queue<(DateTime at, decimal mid)>();

        public CalmSpreadStrategy(IBotStrategy strategy) : this(strategy, () => DateTime.UtcNow) { }

        public CalmSpreadStrategy(IBotStrategy strategy, Func<DateTime> utcNow)
        {
            this.strategy = strategy;
            this.utcNow = utcNow;
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            var market = botContext.LatestMarketData;
            var now = utcNow();
            Watch(market, now);

            //Below the minimum spread the wrapped strategy finishes or cancels the bot's buy anyway
            var wouldBid = market.SpreadPercentage >= botContext.spreadConfiguration.MinimumSpreadPercentage;

            if (wouldBid && !IsCalm(botContext.spreadConfiguration, now))
            {
                switch (botContext.BotState)
                {
                    case BotState.Buying:
                        return;
                    case BotState.BuyOrderActive:
                        await executeOrderFunctionCallback(async () => await dataRepository.Exchange.CancelOrder(botContext.CurrentOrderData.Id, market.Symbol));
                        return;
                }
            }

            await strategy.ProcessMarketData(dataRepository, botContext, executeOrderFunctionCallback, finishWorkCallBack);
        }

        private void Watch(MarketData market, DateTime now)
        {
            if (market.BidRate > 0 && market.AskRate > 0)
                mids.Enqueue((now, (market.BidRate.Value + market.AskRate.Value) / 2));

            //Keeps the last mid at or before the window's start: the price the window started at, however long ago it last changed
            var windowStart = now - Window;
            while (mids.Count > 1 && mids.ElementAt(1).at <= windowStart)
                mids.Dequeue();
        }

        private bool IsCalm(SpreadConfiguration configuration, DateTime now)
        {
            //Samples that start inside the window don't show where the price came from
            if (mids.Count == 0 || mids.Peek().at > now - Window)
                return false;

            var highest = mids.Max(sample => sample.mid);
            var lowest = mids.Min(sample => sample.mid);

            return (highest - lowest) / highest * 100 < configuration.MinimumSpreadPercentage;
        }
    }
}
