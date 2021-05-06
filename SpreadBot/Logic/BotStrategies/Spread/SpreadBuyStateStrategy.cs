using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadBuyStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<Order>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (!botContext.LatestMarketData.BidRate.HasValue)
                return;

            if (botContext.LatestMarketData.SpreadPercentage >= botContext.spreadConfiguration.MinimumSpreadPercentage)
            {

                decimal bidPrice = (botContext.LatestMarketData.BidRate.Value + 1.Satoshi()).CeilToPrecision(botContext.LatestMarketData.Precision);
                decimal amount = (botContext.Balance * (1 - dataRepository.Exchange.FeeRate) / bidPrice).CeilToPrecision(botContext.LatestMarketData.Precision);

                await executeOrderFunctionCallback(async () => await dataRepository.Exchange.BuyLimit(botContext.LatestMarketData.Symbol, amount, bidPrice));
            }
            else
                await finishWorkCallBack();
        }

    }
}
