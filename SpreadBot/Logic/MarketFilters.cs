using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Linq;

namespace SpreadBot.Logic
{
    /// <summary>
    /// The filters a market must pass for a configuration's bot: the Coordinator allocates only on markets that pass, and a bot
    /// still buying leaves a market that stops passing. Reads the settings live, so a reload applies on the next market update.
    /// </summary>
    public class MarketFilters
    {
        private readonly AppSettings appSettings;

        public MarketFilters(AppSettings appSettings)
        {
            this.appSettings = appSettings;
        }

        /// <summary>
        /// The first filter <paramref name="market"/> fails for <paramref name="configuration"/>, or null if it passes them all
        /// (balance and bot slots aside).
        /// </summary>
        public string Rejection(MarketData market, SpreadConfiguration configuration) =>
            ViabilityRejection(market) ?? ConfigurationRejection(market, configuration);

        /// <summary>
        /// Why no configuration's bot can trade <paramref name="market"/>, or null.
        /// </summary>
        public string ViabilityRejection(MarketData market)
        {
            if (IsBlacklisted(market))
                return "blacklisted";
            if (!string.IsNullOrWhiteSpace(market.Notice))
                return $"notice: {market.Notice}";
            if (market.Status != EMarketStatus.Online)
                return "offline";
            //The strategies can't quote a market without a tick, so its bot would hold a slot and its balance forever
            if (!(market.PriceTick > 0))
                return "no price tick";
            return null;
        }

        private bool IsBlacklisted(MarketData market) =>
            appSettings.BlacklistedMarkets?.Any(entry =>
                string.Equals(entry, market.Target, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry, market.Symbol, StringComparison.OrdinalIgnoreCase)) == true;

        private static string ConfigurationRejection(MarketData market, SpreadConfiguration configuration)
        {
            if (market.LastTradeRate < configuration.MinimumPrice)
                return $"price {market.LastTradeRate} below the minimum {configuration.MinimumPrice}";
            if (market.SpreadPercentage < configuration.MinimumSpreadPercentage)
                return $"spread {market.SpreadPercentage:0.###}% below the minimum {configuration.MinimumSpreadPercentage}%";
            if (market.QuoteVolume < configuration.MinimumQuoteVolume)
                return $"volume {market.QuoteVolume:0.####} below the minimum {configuration.MinimumQuoteVolume}";
            if (market.PercentChange > configuration.MaxPercentChangeFromPreviousDay)
                return $"up {market.PercentChange:0.##}% in 24 h, above the maximum {configuration.MaxPercentChangeFromPreviousDay}%";
            if (configuration.AvoidTokenizedSecurities && market.IsTokenizedSecurity.GetValueOrDefault())
                return "a tokenized security";
            return null;
        }
    }
}
