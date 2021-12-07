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

            if (botContext.LatestMarketData.BidRate.HasValue 
                && MarketEvaluator.EvaluateMarketBasedOnSpreadConfiguration(botContext.LatestMarketData, botContext.spreadConfiguration)
                && MarketEvaluator.IsMarketViable(botContext.LatestMarketData, botContext.appSettings))
            {

                decimal bidPrice = (botContext.LatestMarketData.BidRate.Value + 1.Satoshi()).CeilToPrecision(botContext.LatestMarketData.LimitPrecision);
                decimal amount = (botContext.Balance * (1 - dataRepository.Exchange.FeeRate) / bidPrice).CeilToPrecision(botContext.LatestMarketData.AmountPrecision);

                await executeOrderFunctionCallback(async () => await dataRepository.Exchange.BuyLimit(botContext.LatestMarketData.Symbol, amount, bidPrice));
            }
            else
                await finishWorkCallBack();
        }

    }
}
