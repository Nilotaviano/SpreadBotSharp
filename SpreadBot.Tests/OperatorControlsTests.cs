using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SpreadBot.Tests
{
    /// <summary>
    /// What an operator (the dashboard) can see and do: allocation verdicts, pausing allocation, finishing one bot, stream activity.
    /// </summary>
    [TestClass]
    public class OperatorControlsTests
    {
        private const string Symbol = "NMR/BTC";

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

        private static SpreadConfiguration Configuration() => new SpreadConfiguration
        {
            BaseMarket = "BTC",
            AllocatedAmountOfBaseCurrency = 0.003m,
            MinimumNegotiatedAmount = 0.001m,
            MinimumSpreadPercentage = 1,
            MinimumQuoteVolume = 150,
            MaxPercentChangeFromPreviousDay = 40,
            MinimumPrice = 0.000008m
        };

        private static MarketData Market(decimal ask = 0.00010101m, decimal volume = 200, decimal change = 5, EMarketStatus status = EMarketStatus.Online, decimal? tick = MockExchange.Tick) =>
            new MarketData { Symbol = Symbol, AskRate = ask, BidRate = 0.00009999m, LastTradeRate = 0.0001m, QuoteVolume = volume, PercentChange = change, Status = status, PriceTick = tick };

        [TestMethod]
        public void WhyNotAllocatable_NamesTheFirstFilterAMarketFails()
        {
            var (_, _, _, coordinator) = Started(Symbol);

            Assert.IsNull(coordinator.WhyNotAllocatable(Market(), Configuration()));
            Assert.AreEqual("offline", coordinator.WhyNotAllocatable(Market(status: EMarketStatus.Offline), Configuration()));
        }

        private static (MockExchange exchange, DataRepository repository, List<Bot> bots, Coordinator coordinator) Started(params string[] symbols)
        {
            var exchange = new MockExchange(new BalanceData { CurrencyAbbreviation = "BTC", Total = 1, Amount = 1 });
            exchange.WithMarkets(symbols);
            exchange.WithTickers(symbols.Select(s => MockExchange.Ticker(s, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5)).ToArray());
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var bots = new List<Bot>();
            var context = new Mock<ICoordinatorContext>();
            context.Setup(c => c.GetBotCount()).Returns(() => bots.Count);
            context.Setup(c => c.GetBots()).Returns(() => bots.ToList());
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bots.Add);
            context.Setup(c => c.RemoveBot(It.IsAny<Guid>())).Returns<Guid>(id => bots.RemoveAll(b => b.Guid == id) > 0);
            var settings = new AppSettings { MaxNumberOfBots = 3, ResyncIntervalMs = 600000, SpreadConfigurations = new[] { Configuration() } };
            return (exchange, repository, bots, new Coordinator(settings, repository, context.Object));
        }

        //Updates are consumed in order, so once the second is in the cache the first one's handlers have run
        private static void PushAndWait(MockExchange exchange, DataRepository repository, decimal ask)
        {
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: ask, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: ask + MockExchange.Tick, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            WaitUntil(() => repository.MarketsData[Symbol].AskRate == ask + MockExchange.Tick, "both updates are consumed");
        }

        [TestMethod]
        public void PausedAllocation_AllocatesNothingUntilResumed()
        {
            var (exchange, repository, bots, coordinator) = Started(Symbol);
            coordinator.SetAllocationPaused(true);

            coordinator.Start();
            PushAndWait(exchange, repository, 0.00010110m);
            Assert.AreEqual(0, bots.Count, "a viable market updated while paused");
            Assert.IsTrue(coordinator.IsAllocationPaused);

            coordinator.SetAllocationPaused(false);
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010102m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));

            WaitUntil(() => bots.Count == 1, "the next market update allocates");
        }

        [TestMethod]
        public void FinishedOnRequest_CancelsItsOrderAndFinishes()
        {
            var (exchange, repository, bots, coordinator) = Started(Symbol);
            exchange.Mock
                .Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()))
                .ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m });
            exchange.Mock
                .Setup(e => e.CancelOrder("buy-1", Symbol))
                .ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m });
            coordinator.Start();
            WaitUntil(() => bots.Count == 1 && bots[0].botContext.BotState == BotState.BuyOrderActive && bots[0].botContext.SemaphoreCurrentCount == 1, "the bot's buy rests");
            var bot = bots[0];

            Assert.IsTrue(coordinator.FinishBot(bot.Guid));

            WaitUntil(() => bot.botContext.BotState == BotState.FinishedWork, "the bot finishes");
            exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), Times.Once);
            WaitUntil(() => bots.Count == 0, "it is unallocated");
            Assert.IsFalse(coordinator.FinishBot(bot.Guid), "no such bot anymore");
        }

        [TestMethod]
        public void BotWhoseOrderPlacementIsUnknown_KeepsRunningWhenAskedToFinish()
        {
            var exchange = new MockExchange();
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            var context = new BotContext(Configuration(), Market(), BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { ClientOrderId = "sb-unknown", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.UNKNOWN, CreatedAt = DateTime.UtcNow }
            };
            var unallocations = 0;
            var bot = new Bot(repository, context, _ => unallocations++, TestStrategies.Factory(), TestLedger.Empty(repository));

            bot.RequestFinish().Wait();

            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            Assert.AreEqual(0, unallocations);
            exchange.Mock.Verify(e => e.CancelOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        private static (MockExchange exchange, BotContext context, Bot bot, Func<int> unallocations, DataRepository repository) BotWithOpenBuy(Action<Mock<IExchange>> setup, TimeSpan? cancelBudget = null)
        {
            var exchange = new MockExchange();
            setup(exchange.Mock);
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            var context = new BotContext(Configuration(), Market(), BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m, Quantity = 30 }
            };
            var unallocations = 0;
            var bot = new Bot(repository, context, _ => unallocations++, TestStrategies.Factory(), TestLedger.Empty(repository))
            {
                CancelRetryDelay = TimeSpan.FromMilliseconds(10),
                FinishCancelBudget = cancelBudget ?? TimeSpan.FromSeconds(15)
            };
            return (exchange, context, bot, () => unallocations, repository);
        }

        [TestMethod]
        public void FinishWhoseCancelKeepsFailing_LeavesTheBotTrading()
        {
            var (exchange, context, bot, unallocations, _) = BotWithOpenBuy(
                mock => mock.Setup(e => e.CancelOrder("buy-1", Symbol)).ThrowsAsync(new ApiException(ApiErrorType.RetryLater, "server busy")),
                cancelBudget: TimeSpan.FromMilliseconds(100));

            bot.RequestFinish().Wait();

            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            Assert.AreEqual(OrderStatus.OPEN, context.CurrentOrderData.Status);
            Assert.AreEqual(0, unallocations());
            exchange.Mock.Verify(e => e.CancelOrder("buy-1", Symbol), Times.AtLeast(2));
        }

        [TestMethod]
        public void RefusedFinish_ActsOnTheMarketUpdateItSkipped()
        {
            // Only the replayed update can make this bot look its unknown order up: it hasn't started, so nothing else reaches it
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            WaitUntil(() => repository.MarketsData[Symbol].AskRate == 0.00010101m, "the update is cached");
            var context = new BotContext(Configuration(), Market(), BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { ClientOrderId = "sb-unknown", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.UNKNOWN, CreatedAt = DateTime.UtcNow }
            };
            var bot = new Bot(repository, context, _ => { }, TestStrategies.Factory(), TestLedger.Empty(repository));

            bot.RequestFinish().Wait();

            WaitUntil(() => exchange.Mock.Invocations.Any(i => i.Method.Name == nameof(IExchange.GetOrderByClientId)), "the replayed update looks the order up");
        }

        [TestMethod]
        public void FillBookedDuringTheFinish_PlacesNoSellAndIsSoldAtMarket()
        {
            // The cancel answers with 20 coins bought: booking them would normally place a sell at once
            var (exchange, context, bot, unallocations, _) = BotWithOpenBuy(mock =>
            {
                mock.Setup(e => e.CancelOrder("buy-1", Symbol)).ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m, Quantity = 30, FillQuantity = 20, Proceeds = 0.002m });
                mock.Setup(e => e.SellMarket(Symbol, 20)).ReturnsAsync(new OrderData { Id = "dust", ClientOrderId = "dust", MarketSymbol = Symbol, Direction = OrderDirection.SELL, Status = OrderStatus.CLOSED, FillQuantity = 20, Proceeds = 0.002m });
            });

            bot.RequestFinish().Wait();

            exchange.Mock.Verify(e => e.SellLimit(It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
            exchange.Mock.Verify(e => e.SellMarket(Symbol, 20), Times.Once);
            Assert.AreEqual(BotState.FinishedWork, context.BotState);
            Assert.AreEqual(1, unallocations());
        }

        [TestMethod]
        public void HaltedBot_IsntFinishedOnRequest()
        {
            var (exchange, context, bot, unallocations, _) = BotWithOpenBuy(mock =>
                mock.Setup(e => e.CancelOrder("buy-1", Symbol)).ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m }));
            bot.Halt(DateTime.UtcNow).Wait();

            bot.RequestFinish().Wait();

            Assert.AreNotEqual(BotState.FinishedWork, context.BotState);
            Assert.AreEqual(0, unallocations());
            exchange.Mock.Verify(e => e.SellMarket(It.IsAny<string>(), It.IsAny<decimal>()), Times.Never);
        }

        [TestMethod]
        public void Streams_RecordWhenTheyLastDelivered()
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            var before = DateTime.UtcNow;

            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m));
            exchange.OnBalance(new BalanceData { CurrencyAbbreviation = "BTC", Total = 1, Amount = 1 });

            WaitUntil(() => repository.LastMarketUpdate >= before && repository.LastBalanceUpdate >= before, "both updates are consumed");
            Assert.IsNull(repository.LastOrderUpdate);
        }

        [TestMethod]
        public void Resyncs_AreCountedAndStampedWhenTheyComplete()
        {
            var exchange = new MockExchange();
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            var before = DateTime.UtcNow;

            repository.RequestResync();

            WaitUntil(() => repository.ResyncCount == 1 && repository.LastResync >= before, "the resync completes");
        }

        [TestMethod]
        public void Logger_ReportsEachWrittenLog()
        {
            var seen = new List<Logger.LogEntry>();
            void listen(Logger.LogEntry entry)
            {
                if (entry.Content.Contains("operator-controls-test"))
                    lock (seen) seen.Add(entry);
            }

            Logger.Instance.Logged += listen;
            try
            {
                Logger.Instance.LogError("operator-controls-test");
                WaitUntil(() => { lock (seen) return seen.Count == 1; }, "the log is reported");
                Assert.AreEqual("Error", seen[0].Level);
            }
            finally
            {
                Logger.Instance.Logged -= listen;
            }
        }
    }
}
