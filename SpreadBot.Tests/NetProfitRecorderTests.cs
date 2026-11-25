using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SpreadBot.Infrastructure;
using SpreadBot.Logic.BotStrategies;
using System.Linq;

namespace SpreadBot.Tests
{
    [TestClass]
    public class NetProfitRecorderTests
    {
        [TestMethod]
        public void ParseConfigurationProfits_SumsRecordsOfWhatIsNowOneConfiguration()
        {
            // Written when MinimumProfitPercentage was part of a configuration's equality
            const string json = @"[
  { ""BaseMarket"": ""BTC"", ""AllocatedAmountOfBaseCurrency"": 0.001, ""MinimumProfitPercentage"": 0.6, ""IsActive"": false, ""Profit"": 0.00001 },
  { ""BaseMarket"": ""BTC"", ""AllocatedAmountOfBaseCurrency"": 0.001, ""MinimumProfitPercentage"": 1.0, ""IsActive"": true, ""Profit"": 0.00002 },
  { ""BaseMarket"": ""ETH"", ""AllocatedAmountOfBaseCurrency"": 0.1, ""IsActive"": true, ""Profit"": 0.005 }
]";

            var records = NetProfitRecorder.ParseConfigurationProfits(json);

            Assert.AreEqual(2, records.Count);
            Assert.AreEqual(0.00003m, records.Single(r => r.BaseMarket == "BTC").Profit);
            Assert.IsTrue(records.Single(r => r.BaseMarket == "BTC").IsActive, "active if any of them was");
            Assert.AreEqual(0.005m, records.Single(r => r.BaseMarket == "ETH").Profit);
            Assert.IsTrue(records.Single(r => r.BaseMarket == "BTC").Equals(new SpreadConfiguration { BaseMarket = "BTC", AllocatedAmountOfBaseCurrency = 0.001m }), "matches the configuration it records");
        }

        [TestMethod]
        public void Record_IsSavedAsOneFlatObject()
        {
            var record = NetProfitRecorder.SpreadConfigurationNetProfit.For(new SpreadConfiguration { BaseMarket = "BTC", Strategy = BotStrategyType.CalmSpread, MinutesForLoss = 90 });
            record.Profit = 0.1m;

            var saved = JObject.FromObject(record);

            Assert.AreEqual("BTC", (string)saved["BaseMarket"]);
            Assert.AreEqual("CalmSpread", (string)saved["Strategy"]);
            Assert.AreEqual(90, (int)saved["MinutesForLoss"]);
            Assert.AreEqual(0.1m, (decimal)saved["Profit"]);
            Assert.IsNotNull(saved["IsActive"]);
        }
    }
}
