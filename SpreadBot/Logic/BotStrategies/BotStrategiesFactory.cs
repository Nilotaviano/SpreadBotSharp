using System;
using System.Collections.Generic;
using System.Text;
using SpreadBot.Logic.BotStrategies.Spread;

namespace SpreadBot.Logic.BotStrategies
{
    public class BotStrategiesFactory
    {
        public Dictionary<BotState, IBotStateStrategy> GetStrategiesDictionary(/*For future reference: this should receive a strategy id to generate the correct strategies*/)
        {
            return new Dictionary<BotState, IBotStateStrategy>() {
                { BotState.Buying, new SpreadBuyStateStrategy() },
                { BotState.Bought, new SpreadSellStateStrategy() },
                { BotState.BuyOrderActive, new SpreadBuyOrderActiveStateStrategy() },
                { BotState.SellOrderActive, new SpreadSellOrderActiveStateStrategy() },
            };
        }
    }
}
