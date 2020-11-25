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
        public decimal MinimumPrice { get; set; }
        public decimal MinimumNegotiatedAmount { get; set; } //Dust limit

        public IEnumerable<SpreadConfiguration> SpreadConfigurations { get; set; }
    }

    public class SpreadConfiguration
    {
        public Guid Guid { get; } = Guid.NewGuid();
        public decimal MaxPercentChangeFromPreviousDay { get; set; }
        public decimal MinimumSpreadPercentage { get; set; }
        public decimal MinimumQuoteVolume { get; set; }
        public decimal AllocatedAmountOfBaseCurrency { get; set; }
        public decimal MaxBidAskDifferenceFromOrder { get; set; } = 1.Satoshi(); //default 1 satoshi, but should be set higher, I think
        public int MinutesForLoss { get; set; }
    }
}
