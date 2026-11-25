using SpreadBot.Infrastructure;
using System;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    /// <summary>
    /// Starts <see cref="SpreadConfiguration.MinimumSpreadPercentage"/> (the least spread the bot entered for) above break-even after fees
    /// when the buy fills, and falls linearly to break-even over <see cref="SpreadConfiguration.MinutesForLoss"/>; after that, any price.
    /// </summary>
    public class DecayingSellFloor : ISellFloor
    {
        public decimal? Price(BotContext botContext, decimal feeRate, DateTime utcNow)
        {
            if (botContext.CanSellAtLoss(utcNow))
                return null;

            var minutesForLoss = botContext.spreadConfiguration.MinutesForLoss;
            //Without a fill time CanSellAtLoss never lets go, so neither does the target
            var heldMinutes = botContext.BuyFilledAt.HasValue ? (decimal)(utcNow - botContext.BuyFilledAt.Value).TotalMinutes : 0;
            var remaining = minutesForLoss > 0 ? Math.Clamp(1 - heldMinutes / minutesForLoss, 0, 1) : 0;

            return botContext.BreakEvenPrice(feeRate) * (1 + botContext.spreadConfiguration.MinimumSpreadPercentage / 100 * remaining);
        }
    }
}
