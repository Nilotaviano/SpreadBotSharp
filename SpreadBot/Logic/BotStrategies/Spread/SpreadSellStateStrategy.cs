using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadSellStateStrategy : IBotStateStrategy
    {
        private readonly ISellFloor sellFloor;

        public SpreadSellStateStrategy(ISellFloor sellFloor)
        {
            this.sellFloor = sellFloor;
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            if (!botContext.LatestMarketData.AskRate.HasValue || !(botContext.LatestMarketData.PriceTick is decimal tick))
                return;

            decimal askPrice = botContext.LatestMarketData.AskRate.Value - tick;

            //With a one-tick spread, improving the ask would cross the bid and the post-only order would be rejected; join the ask instead
            if (botContext.LatestMarketData.BidRate.HasValue && askPrice <= botContext.LatestMarketData.BidRate.Value)
                askPrice = botContext.LatestMarketData.AskRate.Value;

            if (sellFloor.Price(botContext, dataRepository.Exchange.FeeRate, DateTime.UtcNow) is decimal floor)
                askPrice = Math.Max(floor, askPrice).CeilToTick(tick);

            await executeOrderFunctionCallback(async () => await dataRepository.Exchange.SellLimit(botContext.LatestMarketData.Symbol, botContext.HeldAmount, askPrice));
        }
    }
}
