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
            if (!botContext.LatestMarketData.AskRate.HasValue || !(botContext.LatestMarketData.PriceTick is decimal tick))
                return;

            decimal askPrice = botContext.LatestMarketData.AskRate.Value - tick;

            bool canSellAtLoss = botContext.CanSellAtLoss(DateTime.UtcNow);
            if (!canSellAtLoss)
                askPrice = Math.Max(botContext.BoughtPrice * (1m + botContext.spreadConfiguration.MinimumProfitPercentage / 100), askPrice).CeilToTick(tick);

            await executeOrderFunctionCallback(async () => await dataRepository.Exchange.SellLimit(botContext.LatestMarketData.Symbol, botContext.HeldAmount, askPrice));
        }
    }
}
