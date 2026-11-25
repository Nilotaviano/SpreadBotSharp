using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadSellOrderActiveStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (botContext.CurrentOrderData.Limit - botContext.LatestMarketData.AskRate >= botContext.spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //cancel order and switch to BotState.Sell
                //TODO: I think we should rate limit how often we cancel orders here
                bool canSellAtLoss = botContext.CanSellAtLoss(DateTime.UtcNow);
                bool currentAskAboveBreakEven = botContext.LatestMarketData.AskRate > botContext.BreakEvenPrice(dataRepository.Exchange.FeeRate);

                if (canSellAtLoss || currentAskAboveBreakEven)
                    await executeOrderFunctionCallback(async () => await dataRepository.Exchange.CancelOrder(botContext.CurrentOrderData.Id, botContext.LatestMarketData.Symbol));
            }
        }
    }
}
