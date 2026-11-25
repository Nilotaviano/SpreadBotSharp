using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadBuyOrderActiveStateStrategy : IBotStateStrategy
    {
        private readonly MarketFilters marketFilters;

        public SpreadBuyOrderActiveStateStrategy(MarketFilters marketFilters)
        {
            this.marketFilters = marketFilters;
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (marketFilters.BuyingRejection(botContext.LatestMarketData, botContext.spreadConfiguration) != null)
            {
                //Cancel order and exit: back in Buying, the bot leaves the market
                await executeOrderFunctionCallback(async () => await dataRepository.Exchange.CancelOrder(botContext.CurrentOrderData.Id, botContext.LatestMarketData.Symbol));
            }
            else if (botContext.LatestMarketData.BidRate - botContext.CurrentOrderData.Limit >= botContext.spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //Cancel order and switch to BotState.Buy
                //TODO: I think we should rate limit how often we cancel orders here
                await executeOrderFunctionCallback(async () => await dataRepository.Exchange.CancelOrder(botContext.CurrentOrderData.Id, botContext.LatestMarketData.Symbol));
            }
        }
    }
}
