using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Logic
{
    class MarketEvaluator
    {
        public static bool IsMarketViable(Market market, AppSettings appSettings)
        {
            return string.IsNullOrWhiteSpace(market.Notice) && market.Status == EMarketStatus.Online && !appSettings.BlacklistedMarkets.Contains(market.Target);
        }

        public static bool EvaluateMarketBasedOnSpreadConfiguration(Market marketData, SpreadConfiguration spreadConfiguration)
        {
            if (marketData.LastTradeRate < spreadConfiguration.MinimumPrice)
                return false;

            if (marketData.SpreadPercentage < spreadConfiguration.MinimumSpreadPercentage || marketData.QuoteVolume < spreadConfiguration.MinimumQuoteVolume)
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has not enough volume ({marketData.QuoteVolume}) or spread ({marketData.SpreadPercentage})");
                return false;
            }

            if (marketData.PercentChange > spreadConfiguration.MaxPercentChangeFromPreviousDay)
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is pumping {marketData.PercentChange}%");
                return false;
            }

            if (spreadConfiguration.AvoidTokenizedSecurities && marketData.IsTokenizedSecurity.GetValueOrDefault())
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is a tokenized security");
                return false;
            }

            return true;
        }
    }
}
