using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadSellStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (!botContext.latestMarketData.AskRate.HasValue)
                return;

            decimal askPrice = botContext.latestMarketData.AskRate.Value - 1.Satoshi().CeilToPrecision(botContext.latestMarketData.Precision);

            bool canSellAtLoss = botContext.buyStopwatch.Elapsed.TotalMinutes > botContext.spreadConfiguration.MinutesForLoss;
            if (!canSellAtLoss)
                askPrice = Math.Max(botContext.boughtPrice * (1m + botContext.spreadConfiguration.MinimumProfitPercentage / 100), askPrice).CeilToPrecision(botContext.latestMarketData.Precision);

            await executeOrderFunctionCallback(async () => await dataRepository.Exchange.SellLimit(botContext.latestMarketData.Symbol, botContext.HeldAmount, askPrice));
        }
    }
}
