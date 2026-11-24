using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies.Spread;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class SellAtLossTests
    {
        private const string Symbol = "NMR-BTC";
        private const decimal BoughtPrice = 0.00020000m;
        private const decimal AskRate = 0.00010101m;

        private static BotContext BoughtContext(DateTime? buyFilledAt)
        {
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 30, MinimumProfitPercentage = 1 };
            var market = new MarketData { Symbol = Symbol, BidRate = 0.00009999m, AskRate = AskRate, Precision = 8 };

            return new BotContext(configuration, market, BotState.Bought, 10) { BoughtPrice = BoughtPrice, BuyFilledAt = buyFilledAt };
        }

        private static async Task<decimal> SellPriceAfterRestore(BotContext context)
        {
            var restored = JsonConvert.DeserializeObject<BotContext>(JsonConvert.SerializeObject(context));

            var exchange = new MockExchange();
            decimal? sellPrice = null;
            exchange.Mock
                .Setup(e => e.SellLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>(), null))
                .Callback((string _, decimal _, decimal limit, string _) => sellPrice = limit)
                .ReturnsAsync(new OrderData());

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 60000 });

            await new SpreadSellStateStrategy().ProcessMarketData(dataRepository, restored, async placeOrder => await placeOrder(), () => Task.CompletedTask);

            return sellPrice.Value;
        }

        [TestMethod]
        public async Task RestoredBot_PastMinutesForLoss_UndercutsAskBelowBoughtPrice()
        {
            var price = await SellPriceAfterRestore(BoughtContext(DateTime.UtcNow.AddMinutes(-31)));

            Assert.AreEqual(AskRate - 1.Satoshi(), price);
        }

        [TestMethod]
        public async Task RestoredBot_WithinMinutesForLoss_KeepsMinimumProfitFloor()
        {
            var price = await SellPriceAfterRestore(BoughtContext(DateTime.UtcNow.AddMinutes(-5)));

            Assert.AreEqual(BoughtPrice * 1.01m, price);
        }

        [TestMethod]
        public void CanSellAtLoss_IsFalseWithoutFillTime()
        {
            Assert.IsFalse(BoughtContext(null).CanSellAtLoss(DateTime.UtcNow));
        }
    }
}
