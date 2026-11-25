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
            Assert.IsNull(Coordinator.WhyNotAllocatable(Market(), Configuration()));
            Assert.AreEqual("offline", Coordinator.WhyNotAllocatable(Market(status: EMarketStatus.Offline), Configuration()));
            Assert.AreEqual("no price tick", Coordinator.WhyNotAllocatable(Market(tick: null), Configuration()));
            StringAssert.StartsWith(Coordinator.WhyNotAllocatable(Market(ask: 0.0001001m), Configuration()), "spread ");
            StringAssert.StartsWith(Coordinator.WhyNotAllocatable(Market(volume: 10), Configuration()), "volume ");
            StringAssert.StartsWith(Coordinator.WhyNotAllocatable(Market(change: 80), Configuration()), "up 80");
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

        [TestMethod]
        public void PausedAllocation_AllocatesNothingUntilResumed()
        {
            var (exchange, repository, bots, coordinator) = Started(Symbol);
            coordinator.SetAllocationPaused(true);

            coordinator.Start();
            Assert.AreEqual(0, bots.Count);
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
            var bot = new Bot(repository, context, _ => Assert.Fail("must not unallocate"), new BotStrategiesFactory(), TestLedger.Empty(repository));

            bot.RequestFinish().Wait();

            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            exchange.Mock.Verify(e => e.CancelOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
