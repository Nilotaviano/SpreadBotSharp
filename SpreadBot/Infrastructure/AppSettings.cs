using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public class AppSettings
    {
        public event EventHandler Reloaded;

        public bool TryToRestoreSession { get; set; }

        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }
        public int MaxNumberOfBots { get; set; }
        public int ResyncIntervalMs { get; set; }

        public string CoinMarketCapApiKey { get; set; }

        public IEnumerable<SpreadConfiguration> SpreadConfigurations { get; set; }

        public void Reload(AppSettings newSettings)
        {
            //No reason to reload ApiKey and ApiSecret atm

            MaxNumberOfBots = newSettings.MaxNumberOfBots;
            SpreadConfigurations = newSettings.SpreadConfigurations;

            Reloaded?.Invoke(this, EventArgs.Empty);
        }
    }

    public class SpreadConfiguration
    {
        public string BaseMarket { get; set; }
        public Guid Guid { get; } = Guid.NewGuid();
        public decimal MaxPercentChangeFromPreviousDay { get; set; }
        public decimal MinimumSpreadPercentage { get; set; }
        public decimal MinimumQuoteVolume { get; set; }
        public decimal MinimumPrice { get; set; }
        public decimal MinimumNegotiatedAmount { get; set; } //Dust limit
        public decimal AllocatedAmountOfBaseCurrency { get; set; }
        public decimal SpreadThresholdBeforeCancelingCurrentOrder { get; set; } = 1.Satoshi(); //default 1 satoshi, but should be set higher, I think
        public int MinutesForLoss { get; set; }
        public decimal MinimumProfitPercentage { get; set; } //Bot will try to sell with at least this amount of profit, until past the MinutesForLoss threshold
        public bool AvoidTokenizedSecurities { get; set; }

        public override int GetHashCode()
        {
            return (
                BaseMarket,
                MaxPercentChangeFromPreviousDay,
                MinimumSpreadPercentage,
                MinimumQuoteVolume,
                AllocatedAmountOfBaseCurrency,
                SpreadThresholdBeforeCancelingCurrentOrder,
                MinutesForLoss,
                MinimumProfitPercentage,
                MinimumPrice,
                MinimumNegotiatedAmount
            ).GetHashCode();
        }
        public override bool Equals(object obj)
        {
            return Equals(obj as SpreadConfiguration);
        }

        public bool Equals(SpreadConfiguration obj)
        {
            return obj != null
                && obj.BaseMarket == this.BaseMarket
                && obj.MaxPercentChangeFromPreviousDay == this.MaxPercentChangeFromPreviousDay
                && obj.MinimumSpreadPercentage == this.MinimumSpreadPercentage
                && obj.MinimumQuoteVolume == this.MinimumQuoteVolume
                && obj.AllocatedAmountOfBaseCurrency == this.AllocatedAmountOfBaseCurrency
                && obj.SpreadThresholdBeforeCancelingCurrentOrder == this.SpreadThresholdBeforeCancelingCurrentOrder
                && obj.MinutesForLoss == this.MinutesForLoss
                && obj.MinimumProfitPercentage == this.MinimumProfitPercentage
                && obj.MinimumPrice == this.MinimumPrice
                && obj.MinimumNegotiatedAmount == this.MinimumNegotiatedAmount;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
