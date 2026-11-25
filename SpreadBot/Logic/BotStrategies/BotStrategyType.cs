namespace SpreadBot.Logic.BotStrategies
{
    /// <summary>
    /// The strategy a <see cref="Infrastructure.SpreadConfiguration"/>'s bots trade with. Stored by name, so values can be added in any order.
    /// </summary>
    public enum BotStrategyType
    {
        /// <summary>
        /// Improves the bid and the ask by one tick; sells no lower than break-even after fees until <c>MinutesForLoss</c>.
        /// </summary>
        Spread = 0,

        /// <summary>
        /// Spread, but the sell floor starts <c>MinimumSpreadPercentage</c> above break-even and falls to it over <c>MinutesForLoss</c>:
        /// more margin per trade for fewer, later sells.
        /// </summary>
        PatientSpread,

        /// <summary>
        /// Spread, but it bids only while the market's mid price has moved less than <c>MinimumSpreadPercentage</c> over the last
        /// few minutes, since a faster market moves more than the spread pays for.
        /// </summary>
        CalmSpread
    }
}
