using SpreadBot.Infrastructure;
using System;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    /// <summary>
    /// The lowest price a bot holding coins may ask for them.
    /// </summary>
    public interface ISellFloor
    {
        /// <returns>The floor, or null when the bot may sell at any price</returns>
        decimal? Price(BotContext botContext, decimal feeRate, DateTime utcNow);
    }
}
