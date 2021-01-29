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
            if (!botContext.latestMarketData.BidRate.HasValue)
                return;

            if (botContext.latestMarketData.SpreadPercentage >= botContext.spreadConfiguration.MinimumSpreadPercentage)
            {

                decimal bidPrice = (botContext.latestMarketData.BidRate.Value + 1.Satoshi()).CeilToPrecision(botContext.latestMarketData.Precision);
                decimal amount = (botContext.Balance * (1 - dataRepository.Exchange.FeeRate) / bidPrice).CeilToPrecision(botContext.latestMarketData.Precision);

                await executeOrderFunctionCallback(async () => await dataRepository.Exchange.BuyLimit(botContext.latestMarketData.Symbol, amount, bidPrice));
            }
            else
                await finishWorkCallBack();
        }

    }
}
