using System;
using System.Collections.Generic;
using System.Text;
using SpreadBot.Logic.BotStrategies.Spread;

namespace SpreadBot.Logic.BotStrategies
{
    public class BotStrategiesFactory
    {
        private readonly MarketFilters marketFilters;

        public BotStrategiesFactory(MarketFilters marketFilters)
        {
            this.marketFilters = marketFilters;
        }

        public IBotStrategy GetStrategy(BotStrategyType strategyType)
        {
            return strategyType switch
            {
                BotStrategyType.Spread => new SpreadStrategy(new BreakEvenSellFloor(), marketFilters),
                BotStrategyType.PatientSpread => new SpreadStrategy(new DecayingSellFloor(), marketFilters),
                BotStrategyType.CalmSpread => new CalmSpreadStrategy(new SpreadStrategy(new BreakEvenSellFloor(), marketFilters), marketFilters),
                _ => throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Unknown bot strategy")
            };
        }
    }
}
