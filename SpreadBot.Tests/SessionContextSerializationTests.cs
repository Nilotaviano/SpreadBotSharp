using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System.Collections.Generic;
using System.Linq;

namespace SpreadBot.Tests
{
    [TestClass]
    public class SessionContextSerializationTests
    {
        [TestMethod]
        public void PreviousSessionContext_RoundTripsBotStateThroughNewtonsoft()
        {
            var configuration = new SpreadConfiguration
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 0.001m,
                MinimumNegotiatedAmount = 0.0005m,
                MinutesForLoss = 30,
                MinimumProfitPercentage = 0.5m
            };
            var market = new MarketData { Symbol = "NMR-BTC", BidRate = 0.00009999m, AskRate = 0.00010101m, Precision = 8 };
            var context = new BotContext(configuration, market, BotState.Buying, 0.25m)
            {
                Balance = 0.0004m,
                BotState = BotState.SellOrderActive,
                BoughtPrice = 0.0001m,
                BuyFilledAt = new System.DateTime(2026, 11, 24, 12, 0, 0, System.DateTimeKind.Utc),
                CurrentOrderData = new OrderData
                {
                    Id = "order-1",
                    ClientOrderId = "client-1",
                    Direction = OrderDirection.SELL,
                    Status = OrderStatus.OPEN,
                    Limit = 0.000101m
                }
            };
            var session = new PreviousSessionContext
            {
                BotContexts = new List<BotContext> { context },
                DustPerMarket = new Dictionary<string, decimal> { ["XYZ-BTC"] = 0.5m }
            };

            var json = JsonConvert.SerializeObject(session, Formatting.Indented);
            var restored = JsonConvert.DeserializeObject<PreviousSessionContext>(json);

            var bot = restored.BotContexts.Single();
            Assert.AreEqual(configuration, bot.spreadConfiguration);
            Assert.AreEqual(0.0004m, bot.Balance);
            Assert.AreEqual(0.25m, bot.HeldAmount);
            Assert.AreEqual(BotState.SellOrderActive, bot.BotState);
            Assert.AreEqual(0.0001m, bot.BoughtPrice);
            Assert.AreEqual(new System.DateTime(2026, 11, 24, 12, 0, 0, System.DateTimeKind.Utc), bot.BuyFilledAt.Value.ToUniversalTime());
            Assert.AreEqual("NMR-BTC", bot.LatestMarketData.Symbol);
            Assert.AreEqual(0.00010101m, bot.LatestMarketData.AskRate.Value);
            Assert.AreEqual(8, bot.LatestMarketData.Precision.Value);
            Assert.AreEqual("client-1", bot.CurrentOrderData.ClientOrderId);
            Assert.AreEqual(OrderStatus.OPEN, bot.CurrentOrderData.Status);
            Assert.AreEqual(0.000101m, bot.CurrentOrderData.Limit);
            Assert.AreEqual(0.5m, restored.DustPerMarket["XYZ-BTC"]);
        }

        [TestMethod]
        public void PreviousSessionContext_RestoresFileThatStillContainsSerializedSemaphore()
        {
            const string savedByOlderBuild = @"{
  ""BotContexts"": [
    {
      ""spreadConfiguration"": { ""BaseMarket"": ""BTC"", ""AllocatedAmountOfBaseCurrency"": 0.001, ""MinimumNegotiatedAmount"": 0.0005 },
      ""buyStopwatch"": { ""IsRunning"": false, ""Elapsed"": ""00:00:00"", ""ElapsedMilliseconds"": 0, ""ElapsedTicks"": 0 },
      ""Semaphore"": { ""CurrentCount"": 1 },
      ""Guid"": ""0639511b-8edb-4063-81e3-5dbec8dbf69f"",
      ""SemaphoreCurrentCount"": 1,
      ""Balance"": 0.0004,
      ""HeldAmount"": 0.25,
      ""BotState"": 3,
      ""CurrentOrderData"": null,
      ""LatestMarketData"": { ""Symbol"": ""NMR-BTC"", ""BidRate"": 0.00009999, ""AskRate"": 0.00010101, ""Precision"": 8 },
      ""BoughtPrice"": 0.0001
    }
  ],
  ""DustPerMarket"": { ""XYZ-BTC"": 0.5 }
}";

            var restored = JsonConvert.DeserializeObject<PreviousSessionContext>(savedByOlderBuild);

            var bot = restored.BotContexts.Single();
            Assert.AreEqual("BTC", bot.spreadConfiguration.BaseMarket);
            Assert.AreEqual(0.001m, bot.spreadConfiguration.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual(0.0004m, bot.Balance);
            Assert.AreEqual(0.25m, bot.HeldAmount);
            Assert.AreEqual(BotState.SellOrderActive, bot.BotState);
            Assert.AreEqual("NMR-BTC", bot.LatestMarketData.Symbol);
            Assert.IsNull(bot.CurrentOrderData);
            Assert.IsNull(bot.BuyFilledAt);
            Assert.AreEqual(0.5m, restored.DustPerMarket["XYZ-BTC"]);
        }
    }
}
