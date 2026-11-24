using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CoordinatorAllocationTests
    {
        private static SpreadConfiguration Configuration(decimal allocated) => new SpreadConfiguration
        {
            BaseMarket = "BTC",
            AllocatedAmountOfBaseCurrency = allocated,
            MinimumNegotiatedAmount = 0.001m,
            MinimumSpreadPercentage = 1,
            MinimumQuoteVolume = 150,
            MaxPercentChangeFromPreviousDay = 40,
            MinimumPrice = 0.000008m
        };

        private static BalanceData Balance(string currency, decimal total, decimal available) =>
            new BalanceData { CurrencyAbbreviation = currency, Total = total, Amount = available };

        /// <summary>
        /// A started DataRepository whose REST snapshot contains the given BTC markets, all online with a ~1% spread.
        /// </summary>
        private static (MockExchange exchange, DataRepository repository) StartedRepository(decimal quoteVolume, BalanceData[] balances, params string[] symbols)
        {
            var exchange = new MockExchange(balances);
            exchange.Mock.Setup(e => e.GetMarketsData()).ReturnsAsync(symbols
                .Select(s => new BittrexApiMarketData { Symbol = s, Status = "ONLINE", Precision = 8 })
                .ToArray());
            exchange.Mock.Setup(e => e.GetMarketSummariesData()).ReturnsAsync(new BittrexApiMarketSummariesData
            {
                Deltas = symbols.Select(s => new BittrexApiMarketSummariesData.MarketSummary { Symbol = s, QuoteVolume = quoteVolume, PercentChange = 5 }).ToArray()
            });
            exchange.Mock.Setup(e => e.GetTickersData()).ReturnsAsync(new BittrexApiTickersData
            {
                Deltas = symbols.Select(s => new BittrexApiTickersData.Ticker { Symbol = s, AskRate = 0.00010101m, BidRate = 0.00009999m, LastTradeRate = 0.0001m }).ToArray()
            });

            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            return (exchange, repository);
        }

        private static Mock<ICoordinatorContext> InMemoryContext(List<Bot> bots, decimal dust = 0, PreviousSessionContext previousSession = null)
        {
            var context = new Mock<ICoordinatorContext>();
            context.Setup(c => c.GetBotCount()).Returns(() => bots.Count);
            context.Setup(c => c.GetBots()).Returns(() => bots.ToList());
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bots.Add);
            context.Setup(c => c.RemoveDustForMarket(It.IsAny<string>())).Returns(dust);
            context.Setup(c => c.GetPreviousSessionContext()).Returns(previousSession);
            return context;
        }

        private static AppSettings Settings(SpreadConfiguration configuration, bool restore = false) => new AppSettings
        {
            MaxNumberOfBots = 3,
            ResyncIntervalMs = 600000,
            TryToRestoreSession = restore,
            SpreadConfigurations = new[] { configuration }
        };

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

        [TestMethod]
        public void Allocates_WhenExchangeHasTheFunds()
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "NMR-BTC");
            var bots = new List<Bot>();

            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();

            Assert.AreEqual(1, bots.Count);
            Assert.AreEqual("NMR-BTC", bots[0].MarketSymbol);
        }

        [TestMethod]
        public void AllocationFailingPartway_StillStartsTheBotsAlreadyAllocated()
        {
            var (exchange, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "AAA-BTC", "BBB-BTC");
            var bots = new List<Bot>();
            var context = InMemoryContext(bots);
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bot =>
            {
                if (bots.Count == 1)
                    throw new InvalidOperationException("the second allocation fails");
                bots.Add(bot);
            });

            Assert.ThrowsExactly<InvalidOperationException>(() => new Coordinator(Settings(Configuration(0.3m)), repository, context.Object).Start());

            Assert.AreEqual(1, bots.Count);
            var symbol = bots[0].MarketSymbol;
            WaitUntil(() => exchange.Mock.Invocations.Any(i => i.Method.Name == nameof(exchange.Object.BuyLimit) && (string)i.Arguments[0] == symbol),
                "the allocated bot starts and places its buy");
        }

        [TestMethod]
        public void DoesNotAllocate_WhenExchangeBalanceDroppedBelowTheTrackedBalance()
        {
            var (exchange, repository) = StartedRepository(0, new[] { Balance("BTC", 1, 1) }, "NMR-BTC");
            var bots = new List<Bot>();
            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();
            Assert.AreEqual(0, bots.Count, "market should not qualify before its volume update");

            exchange.OnBalance(new BittrexApiBalanceData
            {
                Sequence = 2,
                Delta = new BittrexApiBalanceData.Balance { CurrencySymbol = "BTC", Total = 0.1m, Available = 0.1m }
            });
            WaitUntil(() => repository.BalancesData["BTC"].Amount == 0.1m, "the withdrawal is applied");

            exchange.OnSummaries(new BittrexApiMarketSummariesData
            {
                Sequence = 1,
                Deltas = new[] { new BittrexApiMarketSummariesData.MarketSummary { Symbol = "NMR-BTC", QuoteVolume = 200, PercentChange = 5 } }
            });
            // Summaries are consumed in order, so once this marker lands the NMR-BTC evaluation has finished
            exchange.OnSummaries(new BittrexApiMarketSummariesData
            {
                Sequence = 2,
                Deltas = new[] { new BittrexApiMarketSummariesData.MarketSummary { Symbol = "MARKER-ETH", QuoteVolume = 1 } }
            });
            WaitUntil(() => repository.MarketsData.ContainsKey("MARKER-ETH"), "the market update is evaluated");

            Assert.AreEqual(0, bots.Count);
        }

        [TestMethod]
        public void Allocates_AfterADepositArrives()
        {
            var (exchange, repository) = StartedRepository(200, new[] { Balance("BTC", 0.1m, 0.1m) }, "NMR-BTC");
            var bots = new List<Bot>();
            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();
            Assert.AreEqual(0, bots.Count, "0.1 BTC can't cover a 0.3 BTC allocation");

            exchange.OnBalance(new BittrexApiBalanceData
            {
                Sequence = 2,
                Delta = new BittrexApiBalanceData.Balance { CurrencySymbol = "BTC", Total = 1m, Available = 1m }
            });
            WaitUntil(() => repository.BalancesData["BTC"].Amount == 1m, "the deposit is applied");

            exchange.OnTickers(new BittrexApiTickersData
            {
                Sequence = 1,
                Deltas = new[] { new BittrexApiTickersData.Ticker { Symbol = "NMR-BTC", AskRate = 0.00010101m, BidRate = 0.00009999m, LastTradeRate = 0.0001m } }
            });
            WaitUntil(() => bots.Count == 1, "the market is re-evaluated with the new balance");
        }

        [TestMethod]
        public void InheritedDust_IsCappedAtTheCoinsAvailableOnTheExchange()
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1), Balance("NMR", 2, 2) }, "NMR-BTC");
            var bots = new List<Bot>();

            var context = InMemoryContext(bots, dust: 5);

            new Coordinator(Settings(Configuration(0.3m)), repository, context.Object).Start();

            Assert.AreEqual(1, bots.Count);
            Assert.AreEqual(2m, bots[0].HeldAmount);
            context.Verify(c => c.AddDustForMarket("NMR-BTC", 3m), Times.Once, "the excess stays recorded");
        }

        [TestMethod]
        public void RestoredBotWithOpenBuyOrder_IsNotSubtractedTwiceFromTheBalance()
        {
            const decimal allocated = 0.35m;
            var configuration = Configuration(allocated);
            // 0.35 BTC is locked in the restored bot's open buy order: it counts in Total but not in Available
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 0.65m) }, "NMR-BTC", "XYZ-BTC");

            var restoredContext = new BotContext(configuration, new MarketData { Symbol = "NMR-BTC", Precision = 8 }, BotState.BuyOrderActive, 0)
            {
                Balance = allocated,
                CurrentOrderData = new OrderData
                {
                    Id = "buy-1",
                    ClientOrderId = "buy-1",
                    Direction = OrderDirection.BUY,
                    Status = OrderStatus.OPEN,
                    Limit = 0.00010000m
                }
            };
            var previousSession = new PreviousSessionContext { BotContexts = new List<BotContext> { restoredContext } };
            var bots = new List<Bot>();

            new Coordinator(Settings(configuration, restore: true), repository, InMemoryContext(bots, previousSession: previousSession).Object).Start();

            CollectionAssert.AreEquivalent(new[] { "NMR-BTC", "XYZ-BTC" }, bots.Select(b => b.MarketSymbol).ToArray());
        }
    }
}
