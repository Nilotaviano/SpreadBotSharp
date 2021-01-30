using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadStrategy : IBotStrategy
    {
        private readonly Dictionary<BotState, IBotStateStrategy> botStateStrategyDictionary;

        public SpreadStrategy()
        {
            this.botStateStrategyDictionary = new Dictionary<BotState, IBotStateStrategy>() {
                { BotState.Buying, new SpreadBuyStateStrategy() },
                { BotState.Bought, new SpreadSellStateStrategy() },
                { BotState.BuyOrderActive, new SpreadBuyOrderActiveStateStrategy() },
                { BotState.SellOrderActive, new SpreadSellOrderActiveStateStrategy() },
            };
        }

        public async Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack)
        {
            await botStateStrategyDictionary[botContext.BotState].ProcessMarketData(dataRepository, botContext, executeOrderFunctionCallback, finishWorkCallBack);
        }
    }
}
