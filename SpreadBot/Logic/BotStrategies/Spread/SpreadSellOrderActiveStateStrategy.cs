using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadSellOrderActiveStateStrategy : IBotStateStrategy
    {
        private readonly ISellFloor sellFloor;

        public SpreadSellOrderActiveStateStrategy(ISellFloor sellFloor)
        {
            this.sellFloor = sellFloor;
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (botContext.CurrentOrderData.Limit - botContext.LatestMarketData.AskRate >= botContext.spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //cancel order and switch to BotState.Sell
                //TODO: I think we should rate limit how often we cancel orders here
                var floor = sellFloor.Price(botContext, dataRepository.Exchange.FeeRate, DateTime.UtcNow);

                if (floor == null || botContext.LatestMarketData.AskRate > floor)
                    await executeOrderFunctionCallback(async () => await dataRepository.Exchange.CancelOrder(botContext.CurrentOrderData.Id, botContext.LatestMarketData.Symbol));
            }
        }
    }
}
