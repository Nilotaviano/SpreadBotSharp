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
    public class SpreadSellStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(IExchange exchange, SpreadConfiguration spreadConfiguration, Stopwatch buyStopwatch, decimal balance, decimal heldAmount, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack, OrderData currentOrderData, MarketData marketData)
        {
            if (!marketData.AskRate.HasValue)
                return;

            decimal askPrice = (marketData.AskRate.Value - 1.Satoshi()).RoundOrderLimitPrice(marketData.Precision);

            await executeOrderFunctionCallback(async () => await exchange.SellLimit(marketData.Symbol, heldAmount, askPrice));
        }
    }
}
