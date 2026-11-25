using SpreadBot.Infrastructure;
using System;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    /// <summary>
    /// Break-even after fees, until <see cref="SpreadConfiguration.MinutesForLoss"/> has elapsed since the buy filled.
    /// </summary>
    public class BreakEvenSellFloor : ISellFloor
    {
        public decimal? Price(BotContext botContext, decimal feeRate, DateTime utcNow)
        {
            if (botContext.CanSellAtLoss(utcNow))
                return null;

            return botContext.BreakEvenPrice(feeRate);
        }
    }
}
