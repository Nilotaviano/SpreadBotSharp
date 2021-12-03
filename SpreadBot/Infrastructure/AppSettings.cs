using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public class AppSettings
    {
        public event EventHandler Reloaded;

        public bool TryToRestoreSession { get; set; }

        public string ApiKey { get; set; }
        public string ApiSecret { get; set; }
        public string AccountId { get; set; }
        public int MaxNumberOfBots { get; set; }
        public int ResyncIntervalMs { get; set; }

        public string CoinMarketCapApiKey { get; set; }

        [JsonConverter(typeof(CustomHashSetConverter))]
        public HashSet<string> BlacklistedMarkets { get; set; } = new HashSet<string>();

        public IEnumerable<SpreadConfiguration> SpreadConfigurations { get; set; }

        public void Reload(AppSettings newSettings)
        {
            //No reason to reload ApiKey and ApiSecret atm
            MaxNumberOfBots = newSettings.MaxNumberOfBots;
            SpreadConfigurations = newSettings.SpreadConfigurations;
            BlacklistedMarkets = newSettings.BlacklistedMarkets;

            Reloaded?.Invoke(this, EventArgs.Empty);
        }
    }

    public class SpreadConfiguration
    {
        public string Id { get; set; }
        public string BaseMarket { get; set; }
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
            return Convert.ToInt64(this.Id, 16).GetHashCode();
        }
        public override bool Equals(object obj)
        {
            return Equals(obj as SpreadConfiguration);
        }

        public bool Equals(SpreadConfiguration obj)
        {
            return obj != null && obj.Id == this.Id;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    public class CustomHashSetConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(HashSet<string>);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            JObject jo = JObject.Load(reader);
            return new HashSet<string>(jo.Properties().Select(p => p.Name));
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            HashSet<string> hashSet = (HashSet<string>)value;
            JObject jo = new JObject(hashSet.Select(s => new JProperty(s, s)));
            jo.WriteTo(writer);
        }
    }
}
