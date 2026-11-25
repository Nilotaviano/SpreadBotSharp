using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SpreadBot.Logic.BotStrategies;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public class AppSettings
    {
        public event EventHandler Reloaded;

        public bool TryToRestoreSession { get; set; }

        public string ExchangeId { get; set; } = "binance"; //A ccxt.pro exchange id
        public bool UseSandbox { get; set; } //The exchange's testnet, when CCXT supports one
        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }
        public int MaxNumberOfBots { get; set; }
        public int ResyncIntervalMs { get; set; }

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
        [JsonConverter(typeof(StringEnumConverter))]
        public BotStrategyType Strategy { get; set; }
        public string BaseMarket { get; set; }
        public decimal MaxPercentChangeFromPreviousDay { get; set; }
        public decimal MinimumSpreadPercentage { get; set; }
        public decimal MinimumQuoteVolume { get; set; }
        public decimal MinimumPrice { get; set; }
        public decimal MinimumNegotiatedAmount { get; set; } //Dust limit
        public decimal AllocatedAmountOfBaseCurrency { get; set; }
        public decimal SpreadThresholdBeforeCancelingCurrentOrder { get; set; } = 0.00000001m; //Any price move of at least one tick, but should be set higher, I think
        public int MinutesForLoss { get; set; } //Until then the bot won't sell below the price that recovers its buy and both fees
        public bool AvoidTokenizedSecurities { get; set; }

        public override int GetHashCode()
        {
            return (
                Strategy,
                BaseMarket,
                MaxPercentChangeFromPreviousDay,
                MinimumSpreadPercentage,
                MinimumQuoteVolume,
                AllocatedAmountOfBaseCurrency,
                SpreadThresholdBeforeCancelingCurrentOrder,
                MinutesForLoss,
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
                && obj.Strategy == this.Strategy
                && obj.BaseMarket == this.BaseMarket
                && obj.MaxPercentChangeFromPreviousDay == this.MaxPercentChangeFromPreviousDay
                && obj.MinimumSpreadPercentage == this.MinimumSpreadPercentage
                && obj.MinimumQuoteVolume == this.MinimumQuoteVolume
                && obj.AllocatedAmountOfBaseCurrency == this.AllocatedAmountOfBaseCurrency
                && obj.SpreadThresholdBeforeCancelingCurrentOrder == this.SpreadThresholdBeforeCancelingCurrentOrder
                && obj.MinutesForLoss == this.MinutesForLoss
                && obj.MinimumPrice == this.MinimumPrice
                && obj.MinimumNegotiatedAmount == this.MinimumNegotiatedAmount;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
