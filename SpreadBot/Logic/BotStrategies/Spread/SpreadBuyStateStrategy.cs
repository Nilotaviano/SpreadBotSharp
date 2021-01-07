using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadBuyStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (!botContext.latestMarketData.BidRate.HasValue)
                return;

            if (botContext.latestMarketData.SpreadPercentage >= botContext.spreadConfiguration.MinimumSpreadPercentage)
            {

                decimal bidPrice = (botContext.latestMarketData.BidRate.Value + 1.Satoshi()).CeilToPrecision(botContext.latestMarketData.Precision);
                decimal amount = (botContext.Balance * (1 - botContext.exchange.FeeRate) / bidPrice).CeilToPrecision(botContext.latestMarketData.Precision);

                await executeOrderFunctionCallback(async () => await botContext.exchange.BuyLimit(botContext.latestMarketData.Symbol, amount, bidPrice));
            }
            else
                await finishWorkCallBack();
        }

    }
}
