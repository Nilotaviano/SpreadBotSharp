using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadBuyStateStrategy : IBotStateStrategy
    {
        private readonly MarketFilters marketFilters;

        public SpreadBuyStateStrategy(MarketFilters marketFilters)
        {
            this.marketFilters = marketFilters;
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            var market = botContext.LatestMarketData;

            //The market no longer passes the filters it was allocated on (e.g. its spread narrowed, or it started pumping). Before its
            //first quote (a restored bot's market) only its own state can rule it out: a missing quote would read as a zero spread
            var hasQuote = market.BidRate > 0 && market.AskRate > 0;
            var rejection = hasQuote ? marketFilters.Rejection(market, botContext.spreadConfiguration) : marketFilters.ViabilityRejection(market);
            if (rejection != null)
            {
                Logger.Instance.LogMessage($"Bot {botContext.Guid}: leaving {market.Symbol}: {rejection}");
                await finishWorkCallBack();
                return;
            }

            //Without the market's tick there's no valid price to improve the bid by
            if (!hasQuote || !(market.PriceTick is decimal tick))
                return;

            decimal bidPrice = (market.BidRate.Value + tick).CeilToTick(tick);

            //With a one-tick spread, improving the bid would cross the ask and the post-only order would be rejected; join the bid instead
            if (bidPrice >= market.AskRate.Value)
                bidPrice = market.BidRate.Value;
            //The exchange rounds the amount down to its step size
            decimal amount = botContext.Balance * (1 - dataRepository.Exchange.FeeRate) / bidPrice;

            await executeOrderFunctionCallback(async () => await dataRepository.Exchange.BuyLimit(market.Symbol, amount, bidPrice));
        }

    }
}
