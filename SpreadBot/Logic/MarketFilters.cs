using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Linq;

namespace SpreadBot.Logic
{
    /// <summary>
    /// The filters a market must pass for a configuration's bot: the Coordinator allocates only on markets that pass, and a bot
    /// still buying leaves a market that stops passing most of them. The thresholds are the configuration's own, so a running bot
    /// keeps those it was allocated with; the blacklist is read from the settings live, so a reload applies on the next market update.
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
            ViabilityRejection(market) ?? EntryRejection(market, configuration) ?? PumpRejection(market, configuration) ?? QuoteRejection(market, configuration);

        /// <summary>
        /// Why a bot of <paramref name="configuration"/> that is still buying should leave <paramref name="market"/>, or null. The
        /// entry filters (price, volume, tokenized security) aren't judged: a market drifting around one would make the bot leave and
        /// get allocated again on every trade. Until the market shows a bid or an ask (a restored bot's, before its first ticker) its
        /// quote isn't judged either, since <see cref="Rejection"/> rejects a market without both.
        /// </summary>
        public string BuyingRejection(MarketData market, SpreadConfiguration configuration) =>
            ViabilityRejection(market)
            ?? PumpRejection(market, configuration)
            ?? (market.BidRate > 0 || market.AskRate > 0 ? QuoteRejection(market, configuration) : null);

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

        private static string EntryRejection(MarketData market, SpreadConfiguration configuration)
        {
            if (market.LastTradeRate < configuration.MinimumPrice)
                return $"price {market.LastTradeRate} below the minimum {configuration.MinimumPrice}";
            if (market.QuoteVolume < configuration.MinimumQuoteVolume)
                return $"volume {market.QuoteVolume:0.####} below the minimum {configuration.MinimumQuoteVolume}";
            if (configuration.AvoidTokenizedSecurities && market.IsTokenizedSecurity.GetValueOrDefault())
                return "a tokenized security";
            return null;
        }

        private static string PumpRejection(MarketData market, SpreadConfiguration configuration) =>
            market.PercentChange > configuration.MaxPercentChangeFromPreviousDay
                ? $"up {market.PercentChange:0.##}% in 24 h, above the maximum {configuration.MaxPercentChangeFromPreviousDay}%"
                : null;

        private static string QuoteRejection(MarketData market, SpreadConfiguration configuration)
        {
            //Its spread reads as zero, which a minimum of zero would let through
            if (!(market.BidRate > 0) || !(market.AskRate > 0))
                return "no bid or no ask";
            if (market.SpreadPercentage < configuration.MinimumSpreadPercentage)
                return $"spread {market.SpreadPercentage:0.###}% below the minimum {configuration.MinimumSpreadPercentage}%";
            return null;
        }
    }
}
