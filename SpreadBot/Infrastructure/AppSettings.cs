using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public class AppSettings
    {
        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }
        public int MaxNumberOfBots { get; set; }
        public string BaseMarket { get; set; }
        public int MinutesForLoss { get; set; }
        public decimal MinimumPrice { get; set; }
        public decimal MinimumNegotiatedAmount { get; set; } //Dust limit

        public IEnumerable<SpreadConfiguration> SpreadConfigurations { get; set; }
    }

    public class SpreadConfiguration
    {
        public int Id { get; set; }
        public decimal MaxPercentChangeFromPreviousDay { get; set; }
        public decimal MinimumSpread { get; set; }
        public decimal MinimumQuoteVolume { get; set; }
        public decimal AllocatedAmountOfBaseCurrency { get; set; }
    }
}
