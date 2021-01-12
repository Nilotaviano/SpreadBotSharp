using System;
using System.Collections.Generic;
using System.Text;
using SpreadBot.Logic.BotStrategies.Spread;

namespace SpreadBot.Logic.BotStrategies
{
    public class BotStrategiesFactory
    {
        public IBotStrategy GetStrategy(/*For future reference: this should receive a strategy id to generate the correct strategy*/)
        {
            return new SpreadStrategy();
        }
    }
}
