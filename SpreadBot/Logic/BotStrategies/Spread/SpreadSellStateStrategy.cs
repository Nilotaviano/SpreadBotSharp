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
        public async Task ProcessMarketData(BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack)
        {
            if (!botContext.latestMarketData.AskRate.HasValue)
                return;

            decimal askPrice = botContext.latestMarketData.AskRate.Value - 1.Satoshi();

            bool canSellAtLoss = botContext.buyStopwatch.Elapsed.TotalMinutes > botContext.spreadConfiguration.MinutesForLoss;
            if (!canSellAtLoss)
                askPrice = Math.Max(botContext.boughtPrice * (1 + botContext.spreadConfiguration.MinimumProfitPercentage / 100), askPrice);

            await executeOrderFunctionCallback(async () => await botContext.exchange.SellLimit(botContext.latestMarketData.Symbol, botContext.HeldAmount, askPrice.RoundOrderLimitPrice(botContext.latestMarketData.Precision)));
        }
    }
}
