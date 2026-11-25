using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadBuyStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            //Without the market's tick there's no valid price to improve the bid by
            if (!botContext.LatestMarketData.BidRate.HasValue || !(botContext.LatestMarketData.PriceTick is decimal tick))
                return;

            if (botContext.LatestMarketData.SpreadPercentage >= botContext.spreadConfiguration.MinimumSpreadPercentage)
            {
                decimal bidPrice = (botContext.LatestMarketData.BidRate.Value + tick).CeilToTick(tick);

                //With a one-tick spread, improving the bid would cross the ask and the post-only order would be rejected; join the bid instead
                if (botContext.LatestMarketData.AskRate.HasValue && bidPrice >= botContext.LatestMarketData.AskRate.Value)
                    bidPrice = botContext.LatestMarketData.BidRate.Value;
                //The exchange rounds the amount down to its step size
                decimal amount = botContext.Balance * (1 - dataRepository.Exchange.FeeRate) / bidPrice;

                await executeOrderFunctionCallback(async () => await dataRepository.Exchange.BuyLimit(botContext.LatestMarketData.Symbol, amount, bidPrice));
            }
            else
                await finishWorkCallBack();
        }

    }
}
