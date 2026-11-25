using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Logic.BotStrategies.Spread;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class StrategySelectionTests
    {
        private const string Symbol = "NMR/BTC";
        private const decimal Bid = 0.00000100m;

        [TestMethod]
        [DataRow(BotStrategyType.Spread, typeof(SpreadStrategy))]
        [DataRow(BotStrategyType.PatientSpread, typeof(SpreadStrategy))]
        [DataRow(BotStrategyType.CalmSpread, typeof(CalmSpreadStrategy))]
        public void Factory_BuildsTheConfiguredStrategy(BotStrategyType strategyType, Type expected)
        {
            Assert.IsInstanceOfType(TestStrategies.Factory().GetStrategy(strategyType), expected);
        }

        [TestMethod]
        public void Factory_RejectsAnUnknownStrategy()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TestStrategies.Factory().GetStrategy((BotStrategyType)99));
        }

        [TestMethod]
        public void Strategy_BindsByNameAndDefaultsToSpread()
        {
            const string json = @"{ ""SpreadConfigurations"": [ { ""BaseMarket"": ""BTC"", ""Strategy"": ""calmSpread"" }, { ""BaseMarket"": ""ETH"" } ] }";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

            var configurations = new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AppSettings>().SpreadConfigurations.ToList();

            Assert.AreEqual(BotStrategyType.CalmSpread, configurations[0].Strategy);
            Assert.AreEqual(BotStrategyType.Spread, configurations[1].Strategy);
        }

        [TestMethod]
        public void Strategy_IsSavedByNameAndRestored()
        {
            var json = JsonConvert.SerializeObject(new SpreadConfiguration { BaseMarket = "BTC", Strategy = BotStrategyType.PatientSpread });

            StringAssert.Contains(json, @"""Strategy"":""PatientSpread""");
            Assert.AreEqual(BotStrategyType.PatientSpread, JsonConvert.DeserializeObject<SpreadConfiguration>(json).Strategy);
            //Saved before the field existed
            Assert.AreEqual(BotStrategyType.Spread, JsonConvert.DeserializeObject<SpreadConfiguration>(@"{ ""BaseMarket"": ""BTC"" }").Strategy);
        }

        [TestMethod]
        public void Strategy_MakesADifferentConfiguration()
        {
            var spread = new SpreadConfiguration { BaseMarket = "BTC" };
            var calm = new SpreadConfiguration { BaseMarket = "BTC", Strategy = BotStrategyType.CalmSpread };

            Assert.AreNotEqual(spread, calm);
            Assert.AreEqual(calm, new SpreadConfiguration { BaseMarket = "BTC", Strategy = BotStrategyType.CalmSpread });
        }

        private static BotContext Holding(int minutesForLoss, decimal minimumSpreadPercentage, DateTime? buyFilledAt)
        {
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = minutesForLoss, MinimumSpreadPercentage = minimumSpreadPercentage };
            var market = new MarketData { Symbol = Symbol, BidRate = Bid, AskRate = Bid * 2, PriceTick = MockExchange.Tick };

            return new BotContext(configuration, market, BotState.Bought, 10) { BoughtPrice = 0.001m, BuyFilledAt = buyFilledAt };
        }

        [TestMethod]
        [DataRow(0, 1.1)] // just filled: the whole target above break-even
        [DataRow(30, 1.05)] // halfway through MinutesForLoss: half of it
        [DataRow(59, 1.0 + 0.1 / 60)]
        [DataRow(-10, 1.1)] // a fill time ahead of the clock keeps the whole target
        public void DecayingFloor_FallsFromTheTargetToBreakEvenOverMinutesForLoss(int heldMinutes, double expectedFactor)
        {
            var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var context = Holding(minutesForLoss: 60, minimumSpreadPercentage: 10, buyFilledAt: now.AddMinutes(-heldMinutes));

            var floor = new DecayingSellFloor().Price(context, feeRate: 0.01m, now);

            var breakEven = context.BreakEvenPrice(0.01m);
            Assert.AreEqual((double)(breakEven * (decimal)expectedFactor), (double)floor.Value, 1e-12);
        }

        [TestMethod]
        public void DecayingFloor_LetsGoAfterMinutesForLoss()
        {
            var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var context = Holding(minutesForLoss: 60, minimumSpreadPercentage: 10, buyFilledAt: now.AddMinutes(-61));

            Assert.IsNull(new DecayingSellFloor().Price(context, feeRate: 0.01m, now));
        }

        [TestMethod]
        public void DecayingFloor_WithoutAFillTime_KeepsTheWholeTarget()
        {
            var context = Holding(minutesForLoss: 60, minimumSpreadPercentage: 10, buyFilledAt: null);

            Assert.AreEqual((decimal?)(context.BreakEvenPrice(0) * 1.1m), new DecayingSellFloor().Price(context, feeRate: 0, DateTime.UtcNow));
        }

        [TestMethod]
        public void BreakEvenFloor_IsBreakEvenUntilMinutesForLoss()
        {
            var now = DateTime.UtcNow;

            Assert.AreEqual((decimal?)(0.001m / (0.99m * 0.99m)), new BreakEvenSellFloor().Price(Holding(60, 10, now.AddMinutes(-59)), 0.01m, now));
            Assert.IsNull(new BreakEvenSellFloor().Price(Holding(60, 10, now.AddMinutes(-61)), 0.01m, now));
        }

        private class CalmFixture
        {
            public DateTime Now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            public readonly MockExchange Exchange = new MockExchange();
            public readonly Mock<IBotStrategy> Wrapped = new Mock<IBotStrategy>();
            public readonly CalmSpreadStrategy Strategy;
            public readonly DataRepository Repository;
            public readonly BotContext Context;
            public bool Finished;

            public CalmFixture(BotState state, params string[] blacklist)
            {
                Strategy = new CalmSpreadStrategy(Wrapped.Object, new MarketFilters(new AppSettings { BlacklistedMarkets = blacklist }), () => Now);
                Repository = new DataRepository(Exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
                var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1 };
                Context = new BotContext(configuration, Market(mid: 100), state, 0);
                if (state == BotState.BuyOrderActive)
                    Context.CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid };
            }

            //A 2% spread around the mid, in ticks
            public static MarketData Market(decimal mid) =>
                new MarketData { Symbol = Symbol, BidRate = (mid - 1) * MockExchange.Tick * 100, AskRate = (mid + 1) * MockExchange.Tick * 100, PriceTick = MockExchange.Tick, Status = EMarketStatus.Online };

            public Task See(decimal mid, TimeSpan after)
            {
                Now += after;
                Context.LatestMarketData = Market(mid);
                return Strategy.ProcessMarketData(Repository, Context, placeOrder => placeOrder(), () => { Finished = true; return Task.CompletedTask; });
            }

            public void VerifyDelegated(Times times) =>
                Wrapped.Verify(s => s.ProcessMarketData(It.IsAny<DataRepository>(), It.IsAny<BotContext>(), It.IsAny<Func<Func<Task<OrderData>>, Task>>(), It.IsAny<Func<Task>>()), times);
        }

        [TestMethod]
        public async Task Calm_DoesNotBid_BeforeItHasWatchedAWholeWindow()
        {
            var fixture = new CalmFixture(BotState.Buying);

            await fixture.See(mid: 100, after: TimeSpan.Zero);
            await fixture.See(mid: 100, after: CalmSpreadStrategy.Window - TimeSpan.FromSeconds(1));

            fixture.VerifyDelegated(Times.Never());
        }

        [TestMethod]
        public async Task Calm_Bids_OnceTheMarketMovedLessThanTheMinimumSpreadForAWindow()
        {
            var fixture = new CalmFixture(BotState.Buying);

            await fixture.See(mid: 100, after: TimeSpan.Zero);
            await fixture.See(mid: 100.5m, after: TimeSpan.FromMinutes(3));
            await fixture.See(mid: 100, after: TimeSpan.FromMinutes(2));

            fixture.VerifyDelegated(Times.Once());
        }

        [TestMethod]
        public async Task Calm_HoldsOff_WhileTheMarketMovesMoreThanTheMinimumSpread()
        {
            var fixture = new CalmFixture(BotState.Buying);

            await fixture.See(mid: 100, after: TimeSpan.Zero);
            await fixture.See(mid: 98, after: TimeSpan.FromMinutes(4));
            await fixture.See(mid: 98, after: TimeSpan.FromMinutes(1));

            fixture.VerifyDelegated(Times.Never());
            Assert.IsFalse(fixture.Finished);

            // The drop leaves the window
            await fixture.See(mid: 98, after: TimeSpan.FromMinutes(4));

            fixture.VerifyDelegated(Times.Once());
        }

        [TestMethod]
        public async Task Calm_HoldsOff_AfterAGapInWhatItSaw()
        {
            // The only mid in the window would read as no movement; the one before it shows the 10% jump
            var fixture = new CalmFixture(BotState.Buying);

            await fixture.See(mid: 100, after: TimeSpan.Zero);
            await fixture.See(mid: 110, after: TimeSpan.FromMinutes(10));

            fixture.VerifyDelegated(Times.Never());
        }

        [TestMethod]
        public async Task Calm_CancelsARestingBuy_WhenTheMarketTurns()
        {
            var fixture = new CalmFixture(BotState.Buying);

            await fixture.See(mid: 100, after: TimeSpan.Zero);
            await fixture.See(mid: 100, after: CalmSpreadStrategy.Window);
            fixture.VerifyDelegated(Times.Once());

            // The wrapped strategy's bid rests on the exchange
            fixture.Context.BotState = BotState.BuyOrderActive;
            fixture.Context.CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid };
            await fixture.See(mid: 97, after: TimeSpan.FromSeconds(10));

            fixture.Exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), Times.Once);
            fixture.VerifyDelegated(Times.Once());
        }

        [TestMethod]
        public async Task Calm_CancelsARestoredRestingBuy_UntilItHasWatchedAWindow()
        {
            var fixture = new CalmFixture(BotState.BuyOrderActive);

            await fixture.See(mid: 100, after: TimeSpan.Zero);

            fixture.Exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), Times.Once);
            fixture.VerifyDelegated(Times.Never());
        }

        [TestMethod]
        [DataRow(BotState.Bought)]
        [DataRow(BotState.SellOrderActive)]
        public async Task Calm_NeverHoldsBackSelling(BotState state)
        {
            var fixture = new CalmFixture(state);

            await fixture.See(mid: 100, after: TimeSpan.Zero);
            await fixture.See(mid: 90, after: TimeSpan.FromSeconds(10));

            fixture.VerifyDelegated(Times.Exactly(2));
        }

        [TestMethod]
        public async Task Calm_LeavesASpreadBelowTheMinimumToTheWrappedStrategy()
        {
            // The wrapped Spread strategy finishes a Buying bot whose market's spread fell below the minimum
            var fixture = new CalmFixture(BotState.Buying);
            fixture.Context.spreadConfiguration.MinimumSpreadPercentage = 5;

            await fixture.See(mid: 100, after: TimeSpan.Zero);

            fixture.VerifyDelegated(Times.Once());
        }

        [TestMethod]
        public async Task Calm_OverASpreadStrategy_LeavesAPumpingMarket()
        {
            var exchange = new MockExchange();
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            var filters = TestStrategies.Filters();
            var strategy = new CalmSpreadStrategy(new SpreadStrategy(new BreakEvenSellFloor(), filters), filters);
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1, MaxPercentChangeFromPreviousDay = 40 };
            var market = CalmFixture.Market(mid: 100);
            market.PercentChange = 80;
            var context = new BotContext(configuration, market, BotState.Buying, 0) { Balance = 0.001m };
            var finished = false;

            await strategy.ProcessMarketData(repository, context, placeOrder => placeOrder(), () => { finished = true; return Task.CompletedTask; });

            Assert.IsTrue(finished, "not calm yet, but a pumping market is left, not waited out");
            exchange.Mock.Verify(e => e.BuyLimit(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
        }

        [TestMethod]
        public async Task Calm_LeavesAMarketFailingAnyFilterToTheWrappedStrategy()
        {
            // Not calm yet (no window watched), but a blacklisted market is left, not waited out
            var fixture = new CalmFixture(BotState.Buying, "NMR");

            await fixture.See(mid: 100, after: TimeSpan.Zero);

            fixture.VerifyDelegated(Times.Once());
        }
    }
}
