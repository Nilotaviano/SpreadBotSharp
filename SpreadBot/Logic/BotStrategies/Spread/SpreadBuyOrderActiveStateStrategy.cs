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
        public async Task ProcessMarketData(BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack)
        {
            if (botContext.latestMarketData.SpreadPercentage < botContext.spreadConfiguration.MinimumSpreadPercentage)
            {
                //Cancel order and exit
                await executeOrderFunctionCallback(async () => await botContext.exchange.CancelOrder(botContext.currentOrderData.Id));
            }
            else if (botContext.latestMarketData.BidRate - botContext.currentOrderData.Limit >= botContext.spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //Cancel order and switch to BotState.Buy
                //TODO: I think we should rate limit how often we cancel orders here
                await executeOrderFunctionCallback(async () => await botContext.exchange.CancelOrder(botContext.currentOrderData.Id));
            }
        }
    }
}
