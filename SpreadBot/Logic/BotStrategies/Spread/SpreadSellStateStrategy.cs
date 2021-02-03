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
            if (!botContext.LatestMarketData.AskRate.HasValue)
                return;

            decimal askPrice = botContext.LatestMarketData.AskRate.Value - 1.Satoshi().CeilToPrecision(botContext.LatestMarketData.Precision);

            bool canSellAtLoss = botContext.buyStopwatch.Elapsed.TotalMinutes > botContext.spreadConfiguration.MinutesForLoss;
            if (!canSellAtLoss)
                askPrice = Math.Max(botContext.BoughtPrice * (1m + botContext.spreadConfiguration.MinimumProfitPercentage / 100), askPrice).CeilToPrecision(botContext.LatestMarketData.Precision);

            await executeOrderFunctionCallback(async () => await dataRepository.Exchange.SellLimit(botContext.LatestMarketData.Symbol, botContext.HeldAmount, askPrice));
        }
    }
}
