using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
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
            exchange.WithMarkets(symbols);
            exchange.WithTickers(symbols
                .Select(s => MockExchange.Ticker(s, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: quoteVolume, percentChange: 5))
                .ToArray());

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
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "NMR/BTC");
            var bots = new List<Bot>();

            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();

            Assert.AreEqual(1, bots.Count);
            Assert.AreEqual("NMR/BTC", bots[0].MarketSymbol);
        }

        [TestMethod]
        public void DoesNotAllocate_ABlacklistedMarket()
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "NMR/BTC", "XYZ/BTC");
            var bots = new List<Bot>();
            var settings = Settings(Configuration(0.3m));
            settings.BlacklistedMarkets = new[] { "NMR" };

            new Coordinator(settings, repository, InMemoryContext(bots).Object).Start();

            Assert.AreEqual("XYZ/BTC", bots.Single().MarketSymbol);
        }

        [TestMethod]
        [DataRow(1, 2)]
        [DataRow(2, 4)]
        public void TwoConfigurationsOnOneBaseMarket_ShareEachMarketUpToTheCap(int maxBotsPerMarket, int expectedBots)
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "AAA/BTC", "BBB/BTC");
            var bots = new List<Bot>();
            var patient = Configuration(0.2m);
            patient.MinutesForLoss = 60;
            var settings = Settings(Configuration(0.2m));
            settings.SpreadConfigurations = new[] { Configuration(0.2m), patient };
            settings.MaxNumberOfBots = 4;
            settings.MaxNumberOfBotsPerMarket = maxBotsPerMarket;

            new Coordinator(settings, repository, InMemoryContext(bots).Object).Start();

            Assert.AreEqual(expectedBots, bots.Count);
            Assert.IsTrue(bots.GroupBy(b => b.MarketSymbol).All(market => market.Count() == maxBotsPerMarket));
        }

        [TestMethod]
        public void ABotLeavingAMarket_FreesItsSlotUnderTheCap()
        {
            var (exchange, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "NMR/BTC");
            exchange.Mock
                .Setup(e => e.BuyLimit("NMR/BTC", It.IsAny<decimal>(), It.IsAny<decimal>()))
                .ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = "NMR/BTC", Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m });
            exchange.Mock
                .Setup(e => e.CancelOrder("buy-1", "NMR/BTC"))
                .ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = "NMR/BTC", Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m });
            var bots = new List<Bot>();
            var context = InMemoryContext(bots);
            context.Setup(c => c.RemoveBot(It.IsAny<Guid>())).Returns<Guid>(id => bots.RemoveAll(b => b.Guid == id) > 0);
            var patient = Configuration(0.2m);
            patient.MinutesForLoss = 60;
            var settings = Settings(Configuration(0.2m));
            settings.SpreadConfigurations = new[] { Configuration(0.2m), patient };
            settings.MaxNumberOfBots = 4;
            var coordinator = new Coordinator(settings, repository, context.Object);
            coordinator.Start();
            Assert.AreEqual(1, bots.Count, "the cap of 1 keeps the second configuration off the market");
            var first = bots[0];
            WaitUntil(() => first.botContext.BotState == BotState.BuyOrderActive && first.botContext.SemaphoreCurrentCount == 1, "the first bot's buy rests");

            coordinator.FinishBot(first.Guid);
            WaitUntil(() => !bots.Contains(first), "the first bot leaves");
            exchange.PushTicker(MockExchange.Ticker("NMR/BTC", ask: 0.00010102m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));

            WaitUntil(() => bots.Count == 1 && bots[0] != first, "the market takes a new bot");
        }

        [TestMethod]
        [DataRow("GONE/BTC", true)] // BTC's markets are loaded, and this one isn't among them: delisted
        [DataRow("NMR/ETH", false)] // ETH isn't configured, so its markets aren't loaded at all
        public void RestoredBot_OnAMarketNotInTheMarketData(string symbol, bool expectFinished)
        {
            var (_, repository) = StartedRepository(0, new[] { Balance("BTC", 1, 1) }, "NMR/BTC");
            var bots = new List<Bot>();
            var configuration = Configuration(0.3m);
            configuration.BaseMarket = symbol.Split('/')[1];
            var restored = new BotContext(configuration, new MarketData { Symbol = symbol, PriceTick = MockExchange.Tick }, BotState.Buying, 0) { Balance = 0.3m };
            var context = InMemoryContext(bots, previousSession: new PreviousSessionContext { BotContexts = new List<BotContext> { restored } });
            context.Setup(c => c.RemoveBot(It.IsAny<Guid>())).Returns<Guid>(id => { lock (bots) return bots.RemoveAll(b => b.Guid == id) > 0; });

            new Coordinator(Settings(Configuration(0.3m), restore: true), repository, context.Object).Start();

            if (expectFinished)
                WaitUntil(() => { lock (bots) return bots.Count == 0; }, "the bot on the delisted market finishes");
            else
            {
                Thread.Sleep(200);
                lock (bots)
                    Assert.AreEqual(1, bots.Count, "kept, since nothing shows its market is gone");
            }
        }

        [TestMethod]
        public void RestoredSession_KeepsMoreBotsOnAMarketThanTheCap_AndAddsNone()
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "NMR/BTC");
            var bots = new List<Bot>();
            var patient = Configuration(0.2m);
            patient.MinutesForLoss = 60;
            var market = new MarketData { Symbol = "NMR/BTC", PriceTick = MockExchange.Tick };
            var previousSession = new PreviousSessionContext
            {
                BotContexts = new List<BotContext>
                {
                    new BotContext(Configuration(0.2m), market, BotState.Buying, 0) { Balance = 0.2m },
                    new BotContext(patient, market, BotState.Buying, 0) { Balance = 0.2m }
                }
            };
            var settings = Settings(Configuration(0.2m), restore: true);
            settings.MaxNumberOfBotsPerMarket = 1;

            new Coordinator(settings, repository, InMemoryContext(bots, previousSession: previousSession).Object).Start();

            Assert.AreEqual(2, bots.Count);
        }

        [TestMethod]
        public void DoesNotAllocate_AMarketWithoutAPriceTick()
        {
            var exchange = new MockExchange(Balance("BTC", 1, 1));
            exchange.Mock.Setup(e => e.GetMarkets()).ReturnsAsync(new List<MarketData> { new MarketData { Symbol = "NMR/BTC", Status = EMarketStatus.Online } });
            exchange.WithTickers(MockExchange.Ticker("NMR/BTC", ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            var bots = new List<Bot>();

            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();

            Assert.AreEqual(0, bots.Count);
        }

        [TestMethod]
        public void BotSavedWhileFinishing_IsNotRestoredAndItsCoinsBecomeDust()
        {
            var (_, repository) = StartedRepository(0, new[] { Balance("BTC", 1, 1) }, "NMR/BTC");
            var bots = new List<Bot>();
            var finishing = new BotContext(Configuration(0.3m), new MarketData { Symbol = "NMR/BTC", PriceTick = MockExchange.Tick }, BotState.FinishedWork, 2);
            var context = InMemoryContext(bots, previousSession: new PreviousSessionContext { BotContexts = new List<BotContext> { finishing } });

            new Coordinator(Settings(Configuration(0.3m), restore: true), repository, context.Object).Start();

            Assert.AreEqual(0, bots.Count, "a restored finished bot would ignore every message and never release its allocation");
            context.Verify(c => c.AddDustForMarket("NMR/BTC", 2m), Times.Once);
        }

        [TestMethod]
        public void AllocationFailingPartway_StillStartsTheBotsAlreadyAllocated()
        {
            // Each market inherits 1 coin of recorded dust
            var (exchange, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1), Balance("AAA", 2, 2), Balance("BBB", 2, 2) }, "AAA/BTC", "BBB/BTC");
            var bots = new List<Bot>();
            var context = InMemoryContext(bots, dust: 1);
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bot =>
            {
                if (bots.Count == 1)
                    throw new InvalidOperationException("the second allocation fails");
                bots.Add(bot);
            });

            var coordinator = new Coordinator(Settings(Configuration(0.3m)), repository, context.Object);
            Assert.ThrowsExactly<InvalidOperationException>(coordinator.Start);

            Assert.AreEqual(1, bots.Count);
            var symbol = bots[0].MarketSymbol;
            var failedSymbol = symbol == "AAA/BTC" ? "BBB/BTC" : "AAA/BTC";
            CollectionAssert.AreEqual(new[] { symbol }, coordinator.AllocatedMarketsPerSpreadConfiguration.Values.SelectMany(markets => markets.Keys).ToArray(),
                "the failed market isn't left reserved, where it would count against the per-market cap");
            context.Verify(c => c.AddDustForMarket(failedSymbol, 1m), Times.Once, "the dust the failed bot took is recorded again");
            WaitUntil(() => exchange.Mock.Invocations.Any(i => i.Method.Name == nameof(exchange.Object.BuyLimit) && (string)i.Arguments[0] == symbol),
                "the allocated bot starts and places its buy");
        }

        [TestMethod]
        public void InheritingDustFailing_LeavesTheDustRecorded()
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1), Balance("NMR", 2, 2) }, "NMR/BTC");
            var bots = new List<Bot>();
            var context = InMemoryContext(bots, dust: 1);
            context.Setup(c => c.GetDustPerMarket()).Throws(new InvalidOperationException("the dust can't be read"));

            var coordinator = new Coordinator(Settings(Configuration(0.3m)), repository, context.Object);
            Assert.ThrowsExactly<InvalidOperationException>(coordinator.Start);

            Assert.AreEqual(0, bots.Count);
            context.Verify(c => c.RemoveDustForMarket(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public void AllocationFailingAfterTheBotIsStored_KeepsItReservedAndStartsIt()
        {
            var (exchange, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1) }, "AAA/BTC", "BBB/BTC");
            var bots = new List<Bot>();
            var context = InMemoryContext(bots);
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bot =>
            {
                bots.Add(bot);
                if (bots.Count == 2)
                    throw new InvalidOperationException("the second bot is stored, then saving it fails");
            });

            var coordinator = new Coordinator(Settings(Configuration(0.3m)), repository, context.Object);
            Assert.ThrowsExactly<InvalidOperationException>(coordinator.Start);

            Assert.AreEqual(2, bots.Count);
            CollectionAssert.AreEquivalent(bots.Select(b => b.MarketSymbol).ToArray(), coordinator.AllocatedMarketsPerSpreadConfiguration.Values.SelectMany(markets => markets.Keys).ToArray(),
                "a stored bot keeps its market reserved");
            foreach (var symbol in bots.Select(b => b.MarketSymbol))
                WaitUntil(() => exchange.Mock.Invocations.Any(i => i.Method.Name == nameof(exchange.Object.BuyLimit) && (string)i.Arguments[0] == symbol),
                    $"the bot on {symbol} starts and places its buy");
        }

        [TestMethod]
        public void DoesNotAllocate_WhenExchangeBalanceDroppedBelowTheTrackedBalance()
        {
            var (exchange, repository) = StartedRepository(0, new[] { Balance("BTC", 1, 1) }, "NMR/BTC");
            var bots = new List<Bot>();
            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();
            Assert.AreEqual(0, bots.Count, "market should not qualify before its volume update");

            exchange.OnBalance(new BalanceData { CurrencyAbbreviation = "BTC", Total = 0.1m, Amount = 0.1m });
            WaitUntil(() => repository.BalancesData["BTC"].Amount == 0.1m, "the withdrawal is applied");

            exchange.PushTicker(new MarketData { Symbol = "NMR/BTC", QuoteVolume = 200, PercentChange = 5 });
            // Market updates are consumed in order, so once this marker lands the NMR/BTC evaluation has finished
            exchange.PushTicker(new MarketData { Symbol = "MARKER/ETH", QuoteVolume = 1 });
            WaitUntil(() => repository.MarketsData.ContainsKey("MARKER/ETH"), "the market update is evaluated");

            Assert.AreEqual(0, bots.Count);
        }

        [TestMethod]
        public void Allocates_AfterADepositArrives()
        {
            var (exchange, repository) = StartedRepository(200, new[] { Balance("BTC", 0.1m, 0.1m) }, "NMR/BTC");
            var bots = new List<Bot>();
            new Coordinator(Settings(Configuration(0.3m)), repository, InMemoryContext(bots).Object).Start();
            Assert.AreEqual(0, bots.Count, "0.1 BTC can't cover a 0.3 BTC allocation");

            exchange.OnBalance(new BalanceData { CurrencyAbbreviation = "BTC", Total = 1m, Amount = 1m });
            WaitUntil(() => repository.BalancesData["BTC"].Amount == 1m, "the deposit is applied");

            exchange.PushTicker(MockExchange.Ticker("NMR/BTC", ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m));
            WaitUntil(() => bots.Count == 1, "the market is re-evaluated with the new balance");
        }

        [TestMethod]
        public void InheritedDust_IsCappedAtTheCoinsAvailableOnTheExchange()
        {
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1), Balance("NMR", 2, 2) }, "NMR/BTC");
            var bots = new List<Bot>();

            var context = InMemoryContext(bots, dust: 5);

            new Coordinator(Settings(Configuration(0.3m)), repository, context.Object).Start();

            Assert.AreEqual(1, bots.Count);
            Assert.AreEqual(2m, bots[0].HeldAmount);
            context.Verify(c => c.AddDustForMarket("NMR/BTC", It.IsAny<decimal>()), Times.Never, "the excess isn't on the exchange, so it is dropped");
        }

        [TestMethod]
        public void InheritedDust_LeavesTheCoinsRecordedOnTheCoinsOtherMarkets()
        {
            // 2 NMR are on the exchange, and they are NMR/ETH's recorded dust
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 1), Balance("NMR", 2, 2) }, "NMR/BTC");
            var bots = new List<Bot>();
            var context = InMemoryContext(bots, dust: 3);
            context.Setup(c => c.GetDustPerMarket()).Returns(new Dictionary<string, decimal> { ["NMR/ETH"] = 2, ["XYZ/BTC"] = 7 });

            new Coordinator(Settings(Configuration(0.3m)), repository, context.Object).Start();

            Assert.AreEqual(1, bots.Count);
            Assert.AreEqual(0m, bots[0].HeldAmount);
        }

        [TestMethod]
        public void RestoredBotWithOpenBuyOrder_IsNotSubtractedTwiceFromTheBalance()
        {
            const decimal allocated = 0.35m;
            var configuration = Configuration(allocated);
            // 0.35 BTC is locked in the restored bot's open buy order: it counts in Total but not in Available
            var (_, repository) = StartedRepository(200, new[] { Balance("BTC", 1, 0.65m) }, "NMR/BTC", "XYZ/BTC");

            var restoredContext = new BotContext(configuration, new MarketData { Symbol = "NMR/BTC", PriceTick = MockExchange.Tick }, BotState.BuyOrderActive, 0)
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

            CollectionAssert.AreEquivalent(new[] { "NMR/BTC", "XYZ/BTC" }, bots.Select(b => b.MarketSymbol).ToArray());
        }
    }
}
