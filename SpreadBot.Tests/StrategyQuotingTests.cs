using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies.Spread;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class StrategyQuotingTests
    {
        private const string Symbol = "NMR/BTC";
        private const decimal Bid = 0.00000100m;

        private static (DataRepository repository, MockExchange exchange) Repository(decimal feeRate = 0)
        {
            var exchange = new MockExchange();
            exchange.Mock.Setup(e => e.FeeRate).Returns(feeRate);
            return (new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 }), exchange);
        }

        private static MarketData Market(decimal ask) =>
            new MarketData { Symbol = Symbol, BidRate = Bid, AskRate = ask, PriceTick = MockExchange.Tick, Status = EMarketStatus.Online };

        private static Task PlaceOrder(Func<Task<OrderData>> placeOrder) => placeOrder();

        [TestMethod]
        public async Task Buy_ImprovesTheBidByOneTick_WhenTheSpreadAllowsIt()
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1 };
            var context = new BotContext(configuration, Market(ask: Bid + 3 * MockExchange.Tick), BotState.Buying, 0) { Balance = 0.001m };

            await new SpreadBuyStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), Bid + MockExchange.Tick), Times.Once);
        }

        [TestMethod]
        public async Task Buy_JoinsTheBid_WhenTheSpreadIsOneTick()
        {
            // One tick on a 100-tick price is a 1% spread, which passes MinimumSpreadPercentage
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 0.99m };
            var context = new BotContext(configuration, Market(ask: Bid + MockExchange.Tick), BotState.Buying, 0) { Balance = 0.001m };

            await new SpreadBuyStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), Bid), Times.Once);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)] // before its first quote too
        public async Task Buy_LeavesAMarketThatStartedPumping(bool quoted)
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1, MaxPercentChangeFromPreviousDay = 40 };
            var market = Market(ask: Bid + 3 * MockExchange.Tick);
            market.PercentChange = 80;
            if (!quoted)
                (market.BidRate, market.AskRate) = (null, null);
            var context = new BotContext(configuration, market, BotState.Buying, 0) { Balance = 0.001m };
            var finished = false;

            await new SpreadBuyStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => { finished = true; return Task.CompletedTask; });

            Assert.IsTrue(finished);
            exchange.Mock.Verify(e => e.BuyLimit(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
        }

        [TestMethod]
        public async Task Buy_LeavesAMarketBlacklistedAfterItsAllocation()
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1 };
            var settings = new AppSettings { SpreadConfigurations = new[] { configuration } };
            var strategy = new SpreadBuyStateStrategy(new MarketFilters(settings));
            var context = new BotContext(configuration, Market(ask: Bid + 3 * MockExchange.Tick), BotState.Buying, 0) { Balance = 0.001m };
            var finished = false;

            settings.Reload(new AppSettings { BlacklistedMarkets = new[] { "NMR" }, SpreadConfigurations = settings.SpreadConfigurations });
            await strategy.ProcessMarketData(repository, context, PlaceOrder, () => { finished = true; return Task.CompletedTask; });

            Assert.IsTrue(finished);
            exchange.Mock.Verify(e => e.BuyLimit(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
        }

        [TestMethod]
        public async Task Buy_LeavesAFailingMarket_EvenWithoutABidToQuote()
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC" };
            var market = new MarketData { Symbol = Symbol, AskRate = Bid, PriceTick = MockExchange.Tick, Status = EMarketStatus.Offline };
            var context = new BotContext(configuration, market, BotState.Buying, 0) { Balance = 0.001m };
            var finished = false;

            await new SpreadBuyStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => { finished = true; return Task.CompletedTask; });

            Assert.IsTrue(finished);
        }

        [TestMethod]
        [DataRow(true, false, 1, true)] // a bid but no ask
        [DataRow(false, true, 1, true)]
        [DataRow(true, false, 0, true)] // even when any spread will do
        [DataRow(false, false, 1, false)] // no quote yet: waits for one
        public async Task Buy_OnAMarketWithoutABidOrAnAsk(bool hasBid, bool hasAsk, int minimumSpreadPercentage, bool expectLeaves)
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = minimumSpreadPercentage };
            var market = new MarketData { Symbol = Symbol, BidRate = hasBid ? Bid : null, AskRate = hasAsk ? Bid + 3 * MockExchange.Tick : null, PriceTick = MockExchange.Tick, Status = EMarketStatus.Online };
            var context = new BotContext(configuration, market, BotState.Buying, 0) { Balance = 0.001m };
            var finished = false;

            await new SpreadBuyStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => { finished = true; return Task.CompletedTask; });

            Assert.AreEqual(expectLeaves, finished);
            exchange.Mock.Verify(e => e.BuyLimit(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
        }

        [TestMethod]
        public async Task BuyOrderActive_CancelsItsBuy_WhenTheMarketStopsPassingTheFilters()
        {
            // The spread is fine and the buy is still the best bid, but the market started pumping
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1, MaxPercentChangeFromPreviousDay = 40 };
            var market = Market(ask: Bid + 3 * MockExchange.Tick);
            market.PercentChange = 80;
            var context = new BotContext(configuration, market, BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid }
            };

            await new SpreadBuyOrderActiveStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), Times.Once);
        }

        /// <summary>
        /// The bot's buy improved a 99-tick bid to 100 ticks and is now the best bid. With the ask at 103 ticks the ticker's spread is
        /// 2.91%, below the 3% minimum, but the market the bot bid into had 3.88%: cancelling would only place the same buy again.
        /// </summary>
        [TestMethod]
        [DataRow(103, false)]
        [DataRow(102, true)] // 3 ticks over 102 is 2.94% even without the bot's improvement
        public async Task BuyOrderActive_JudgesTheSpreadWithoutItsOwnImprovement_WhileItIsTheBestBid(int askTicks, bool expectCancel)
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 3 };
            var context = new BotContext(configuration, Market(ask: askTicks * MockExchange.Tick), BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid }
            };

            await new SpreadBuyOrderActiveStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), expectCancel ? Times.Once() : Times.Never());
        }

        [TestMethod]
        public async Task BuyOrderActive_SendsNoCancel_WhileItsMarketIsOffline()
        {
            // Outbid too, on a market the exchange refuses requests for: every cancel would fail
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1 };
            var market = Market(ask: Bid + 6 * MockExchange.Tick);
            market.BidRate = Bid + 2 * MockExchange.Tick;
            market.Status = EMarketStatus.Offline;
            var context = new BotContext(configuration, market, BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid }
            };

            await new SpreadBuyOrderActiveStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.CancelOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task BuyOrderActive_LogsEachBuyItHoldsOnAnOfflineMarket_Once()
        {
            // buy-1 filled and the market came back online while the bot was in another state, so this state never saw it online
            var (repository, _) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1 };
            var market = Market(ask: Bid + 6 * MockExchange.Tick);
            market.Status = EMarketStatus.Offline;
            var context = new BotContext(configuration, market, BotState.BuyOrderActive, 0);
            var strategy = new SpreadBuyOrderActiveStateStrategy(TestStrategies.Filters());
            var marker = $"held-buys-test {context.Guid}";
            var seen = new List<string>();
            void listen(Logger.LogEntry entry)
            {
                if (entry.Content.Contains(context.Guid.ToString()))
                    lock (seen) seen.Add(entry.Content);
            }

            Logger.Instance.Logged += listen;
            try
            {
                foreach (var orderId in new[] { "buy-1", "buy-1", "buy-2", "buy-2" })
                {
                    context.CurrentOrderData = new OrderData { Id = orderId, Status = OrderStatus.OPEN, Limit = Bid };
                    await strategy.ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);
                }
                Logger.Instance.LogMessage(marker);

                WaitUntil(() => { lock (seen) return seen.Contains(marker); }, "the logs before the marker are reported");
                lock (seen)
                    CollectionAssert.AreEqual(new[] { "buy-1", "buy-2" }, seen.Where(log => log != marker).Select(log => log.Contains("buy-1") ? "buy-1" : "buy-2").ToArray());
            }
            finally
            {
                Logger.Instance.Logged -= listen;
            }
        }

        private static void WaitUntil(Func<bool> condition, string description)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds > 2000)
                    Assert.Fail($"Timed out waiting until {description}");
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// Price and volume only decide allocation: a market drifting around them would make the bot leave and come back on every trade.
        /// </summary>
        [TestMethod]
        [DataRow(BotState.Buying)]
        [DataRow(BotState.BuyOrderActive)]
        public async Task BuyingBot_StaysOnAMarketBelowItsEntryMinimums(BotState state)
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1, MinimumQuoteVolume = 150, MinimumPrice = Bid * 2 };
            var market = Market(ask: Bid + 3 * MockExchange.Tick);
            market.QuoteVolume = 10;
            market.LastTradeRate = Bid;
            var context = new BotContext(configuration, market, state, 0) { Balance = 0.001m };
            if (state == BotState.BuyOrderActive)
                context.CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid };
            var finished = false;
            var strategy = new SpreadStrategy(new BreakEvenSellFloor(), TestStrategies.Filters());

            await strategy.ProcessMarketData(repository, context, PlaceOrder, () => { finished = true; return Task.CompletedTask; });

            Assert.IsFalse(finished);
            exchange.Mock.Verify(e => e.CancelOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), state == BotState.Buying ? Times.Once() : Times.Never());
        }

        [TestMethod]
        [DataRow(true, false, 0, true)] // a bid but no ask, even when any spread will do
        [DataRow(false, false, 0, false)] // no quote yet: the buy keeps resting
        [DataRow(false, false, 1, false)]
        public async Task BuyOrderActive_OnAMarketWithoutABidOrAnAsk(bool hasBid, bool hasAsk, int minimumSpreadPercentage, bool expectCancel)
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = minimumSpreadPercentage };
            var market = new MarketData { Symbol = Symbol, BidRate = hasBid ? Bid : null, AskRate = hasAsk ? Bid + 3 * MockExchange.Tick : null, PriceTick = MockExchange.Tick, Status = EMarketStatus.Online };
            var context = new BotContext(configuration, market, BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = "buy-1", Status = OrderStatus.OPEN, Limit = Bid }
            };

            await new SpreadBuyOrderActiveStateStrategy(TestStrategies.Filters()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), expectCancel ? Times.Once() : Times.Never());
        }

        [TestMethod]
        public async Task Sell_JoinsTheAsk_WhenTheSpreadIsOneTick()
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 0 };
            var ask = Bid + MockExchange.Tick;
            var context = new BotContext(configuration, Market(ask), BotState.Bought, 10) { BoughtPrice = Bid, BuyFilledAt = DateTime.UtcNow.AddMinutes(-1) };

            await new SpreadSellStateStrategy(new BreakEvenSellFloor()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.SellLimit(Symbol, 10, ask), Times.Once);
        }

        [TestMethod]
        public async Task Sell_KeepsTheBreakEvenFloor_WhenItIsAboveAJoinedAsk()
        {
            var (repository, exchange) = Repository(feeRate: 0.01m);
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 30 };
            var ask = Bid + MockExchange.Tick;
            var context = new BotContext(configuration, Market(ask), BotState.Bought, 10) { BoughtPrice = Bid, BuyFilledAt = DateTime.UtcNow };

            await new SpreadSellStateStrategy(new BreakEvenSellFloor()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            // 100 ticks / 0.99² = 102.03 ticks, rounded up to the tick
            exchange.Mock.Verify(e => e.SellLimit(Symbol, 10, 103 * MockExchange.Tick), Times.Once);
        }

        /// <summary>
        /// The ask fell below the bot's sell order, still above what the coins were bought at.
        /// </summary>
        [TestMethod]
        [DataRow(0.01, 0, false)] // 101 ticks is below the 102.03-tick break-even: re-quoting would sell at a loss
        [DataRow(0.0, 0, true)]
        [DataRow(0.01, 31, true)] // past MinutesForLoss: any price
        public async Task SellOrderActive_RequotesBelowItsOrder_OnlyAboveBreakEven(double feeRate, int heldMinutes, bool expectCancel)
        {
            var (repository, exchange) = Repository((decimal)feeRate);
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 30 };
            var ask = Bid + MockExchange.Tick;
            var context = new BotContext(configuration, Market(ask), BotState.SellOrderActive, 10)
            {
                BoughtPrice = Bid,
                BuyFilledAt = DateTime.UtcNow.AddMinutes(-heldMinutes),
                CurrentOrderData = new OrderData { Id = "sell-1", Status = OrderStatus.OPEN, Limit = Bid + 5 * MockExchange.Tick }
            };

            await new SpreadSellOrderActiveStateStrategy(new BreakEvenSellFloor()).ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.CancelOrder("sell-1", Symbol), expectCancel ? Times.Once() : Times.Never());
        }
    }
}
