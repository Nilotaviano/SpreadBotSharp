using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SpreadBot.Tests
{
    [TestClass]
    public class AppSettingsBindingTests
    {
        private const string SettingsJson = @"{
  ""ApiKey"": ""key"",
  ""ApiSecret"": ""secret"",
  ""TryToRestoreSession"": true,
  ""MaxNumberOfBots"": 3,
  ""BlacklistedMarkets"": [ ""NMR"", ""XYZ/BTC"" ],
  ""ResyncIntervalMs"": 60000,
  ""ExchangeId"": ""kucoin"",
  ""UseSandbox"": true,
  ""SpreadConfigurations"": [
    {
      ""BaseMarket"": ""ETH"",
      ""MaxPercentChangeFromPreviousDay"": 40.0,
      ""MinimumSpreadPercentage"": 1.0,
      ""MinimumQuoteVolume"": 150.0,
      ""MinimumPrice"": 0.00000800,
      ""MinimumNegotiatedAmount"": 0.00100000,
      ""AllocatedAmountOfBaseCurrency"": 0.3,
      ""SpreadThresholdBeforeCancelingCurrentOrder"": 0.00000002,
      ""MinutesForLoss"": 90,
      ""AvoidTokenizedSecurities"": true
    },
    {
      ""BaseMarket"": ""BTC"",
      ""AllocatedAmountOfBaseCurrency"": 0.01
    }
  ]
}";

        private static AppSettings Bind(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AppSettings>();
        }

        [TestMethod]
        public void Get_BindsTopLevelSettings()
        {
            var settings = Bind(SettingsJson);

            Assert.AreEqual("key", settings.ApiKey);
            Assert.AreEqual("secret", settings.ApiSecret);
            Assert.IsTrue(settings.TryToRestoreSession);
            Assert.AreEqual(3, settings.MaxNumberOfBots);
            CollectionAssert.AreEqual(new[] { "NMR", "XYZ/BTC" }, settings.BlacklistedMarkets.ToArray());
            Assert.AreEqual(60000, settings.ResyncIntervalMs);
            Assert.AreEqual("kucoin", settings.ExchangeId);
            Assert.IsTrue(settings.UseSandbox);
        }

        [TestMethod]
        public void Get_BindsEverySpreadConfigurationField()
        {
            var eth = Bind(SettingsJson).SpreadConfigurations.First();

            Assert.AreEqual("ETH", eth.BaseMarket);
            Assert.AreEqual(40.0m, eth.MaxPercentChangeFromPreviousDay);
            Assert.AreEqual(1.0m, eth.MinimumSpreadPercentage);
            Assert.AreEqual(150.0m, eth.MinimumQuoteVolume);
            Assert.AreEqual(0.000008m, eth.MinimumPrice);
            Assert.AreEqual(0.001m, eth.MinimumNegotiatedAmount);
            Assert.AreEqual(0.3m, eth.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual(0.00000002m, eth.SpreadThresholdBeforeCancelingCurrentOrder);
            Assert.AreEqual(90, eth.MinutesForLoss);
            Assert.IsTrue(eth.AvoidTokenizedSecurities);
        }

        [TestMethod]
        public void Get_KeepsDefaultsForFieldsMissingFromJson()
        {
            var btc = Bind(SettingsJson).SpreadConfigurations.Last();

            Assert.AreEqual("BTC", btc.BaseMarket);
            Assert.AreEqual(0.01m, btc.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual(0.00000001m, btc.SpreadThresholdBeforeCancelingCurrentOrder);
            Assert.AreEqual(0m, btc.MinimumNegotiatedAmount);
            Assert.IsFalse(btc.AvoidTokenizedSecurities);
        }

        [TestMethod]
        public void Get_ParsesDecimalsWithInvariantCultureRegardlessOfCurrentCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("pt-BR");

                var eth = Bind(SettingsJson).SpreadConfigurations.First();

                Assert.AreEqual(0.000008m, eth.MinimumPrice);
                Assert.AreEqual(0.001m, eth.MinimumNegotiatedAmount);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [TestMethod]
        public void Reload_WithNewtonsoftDeserializedSettings_UpdatesBotLimitAndConfigurationsAndRaisesReloaded()
        {
            var settings = Bind(SettingsJson);
            var reloadedRaised = false;
            settings.Reloaded += (sender, e) => reloadedRaised = true;
            const string updatedJson = @"{ ""ApiKey"": ""other-key"", ""MaxNumberOfBots"": 7, ""BlacklistedMarkets"": [ ""ABC"" ], ""SpreadConfigurations"": [ { ""BaseMarket"": ""USDT"", ""AllocatedAmountOfBaseCurrency"": 25.5 } ] }";

            settings.Reload(JsonConvert.DeserializeObject<AppSettings>(updatedJson));

            Assert.IsTrue(reloadedRaised);
            Assert.AreEqual(7, settings.MaxNumberOfBots);
            CollectionAssert.AreEqual(new[] { "ABC" }, settings.BlacklistedMarkets.ToArray());
            var usdt = settings.SpreadConfigurations.Single();
            Assert.AreEqual("USDT", usdt.BaseMarket);
            Assert.AreEqual(25.5m, usdt.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual("key", settings.ApiKey);
        }
    }
}
