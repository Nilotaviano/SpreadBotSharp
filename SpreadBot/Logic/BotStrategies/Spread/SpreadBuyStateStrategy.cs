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
        public async Task ProcessMarketData(IExchange exchange, SpreadConfiguration spreadConfiguration, Stopwatch buyStopwatch, decimal balance, decimal heldAmount, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack, OrderData currentOrderData, MarketData marketData, decimal boughtPrice)
        {
            if (!marketData.BidRate.HasValue)
                return;

            if (marketData.SpreadPercentage >= spreadConfiguration.MinimumSpreadPercentage)
            {

                decimal bidPrice = marketData.BidRate.Value + 1.Satoshi();
                decimal amount = (balance * (1 - exchange.FeeRate) / bidPrice).RoundOrderLimitPrice(marketData.Precision);

                await executeOrderFunctionCallback(async () => await exchange.BuyLimit(marketData.Symbol, amount, bidPrice));
            }
            else
                finishWorkCallBack();
        }

    }
}
