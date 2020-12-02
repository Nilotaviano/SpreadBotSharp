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
    public class SpreadSellOrderActiveStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(IExchange exchange, SpreadConfiguration spreadConfiguration, Stopwatch buyStopwatch, decimal balance, decimal heldAmount, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack, OrderData currentOrderData, MarketData marketData, decimal boughtPrice)
        {
            if (currentOrderData.Limit - marketData.AskRate > spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //cancel order and switch to BotState.Sell
                //TODO: I think we should rate limit how often we cancel orders here
                bool canSellAtLoss = buyStopwatch.Elapsed.TotalMinutes > spreadConfiguration.MinutesForLoss;
                if (canSellAtLoss)
                    await executeOrderFunctionCallback(async () => await exchange.CancelOrder(currentOrderData.Id));
            }
        }
    }
}
