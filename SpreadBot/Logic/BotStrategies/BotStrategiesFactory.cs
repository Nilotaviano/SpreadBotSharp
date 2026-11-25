using System;
using System.Collections.Generic;
using System.Text;
using SpreadBot.Logic.BotStrategies.Spread;

namespace SpreadBot.Logic.BotStrategies
{
    public class BotStrategiesFactory
    {
        public IBotStrategy GetStrategy(BotStrategyType strategyType)
        {
            return strategyType switch
            {
                BotStrategyType.Spread => new SpreadStrategy(new BreakEvenSellFloor()),
                BotStrategyType.PatientSpread => new SpreadStrategy(new DecayingSellFloor()),
                BotStrategyType.CalmSpread => new CalmSpreadStrategy(new SpreadStrategy(new BreakEvenSellFloor())),
                _ => throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Unknown bot strategy")
            };
        }
    }
}
