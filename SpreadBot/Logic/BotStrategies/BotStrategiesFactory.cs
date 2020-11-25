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
                { BotState.Buy, new SpreadBuyStateStrategy() },
                { BotState.Sell, new SpreadSellStateStrategy() },
                { BotState.BuyOrderActive, new SpreadBuyOrderActiveStateStrategy() },
                { BotState.SellOrderActive, new SpreadSellOrderActiveStateStrategy() },
            };
        }
    }
}
