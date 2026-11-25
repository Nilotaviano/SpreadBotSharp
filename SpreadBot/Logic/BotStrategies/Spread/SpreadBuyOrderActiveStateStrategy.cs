using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadBuyOrderActiveStateStrategy : IBotStateStrategy
    {
        private readonly MarketFilters marketFilters;

        //Each bot has its own instance, and its market updates are processed one at a time
        private bool holdingWhileOffline;

        public SpreadBuyOrderActiveStateStrategy(MarketFilters marketFilters)
        {
            this.marketFilters = marketFilters;
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            //The exchange refuses requests on an offline market (e.g. a symbol restricted for the account), so a cancel would fail on
            //every update. The buy rests; the order stream or a resync books it if it fills or the exchange cancels it
            if (botContext.LatestMarketData.Status != EMarketStatus.Online)
            {
                if (!holdingWhileOffline)
                    Logger.Instance.LogError($"Bot {botContext.Guid}: {botContext.LatestMarketData.Symbol} is offline, so its buy {botContext.CurrentOrderData.Id} rests untouched until the market is back online, the buy fills or the exchange cancels it");
                holdingWhileOffline = true;
                return;
            }

            holdingWhileOffline = false;

            if (marketFilters.BuyingRejection(botContext.LatestMarketData, botContext.spreadConfiguration, botContext.CurrentOrderData.Limit) != null)
            {
                //Cancel the buy: back in Buying the bot leaves the market, or, if the buy filled enough to sell, it sells first and leaves after
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
