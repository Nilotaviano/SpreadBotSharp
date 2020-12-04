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
    public class SpreadBuyOrderActiveStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(IExchange exchange, SpreadConfiguration spreadConfiguration, Stopwatch buyStopwatch, decimal balance, decimal heldAmount, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack, OrderData currentOrderData, MarketData marketData, decimal boughtPrice)
        {
            if (marketData.SpreadPercentage < spreadConfiguration.MinimumSpreadPercentage)
            {
                //Cancel order and exit
                await executeOrderFunctionCallback(async () => await exchange.CancelOrder(currentOrderData.Id));
            }
            else if (marketData.BidRate - currentOrderData.Limit > spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //Cancel order and switch to BotState.Buy
                //TODO: I think we should rate limit how often we cancel orders here
                await executeOrderFunctionCallback(async () => await exchange.CancelOrder(currentOrderData.Id));
            }
        }
    }
}
