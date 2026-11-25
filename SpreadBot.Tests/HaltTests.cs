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
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class HaltTests
    {
        private const string Symbol = "NMR/BTC";
        private const string BuyOrderId = "buy-1";

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

        private static int OrderRequests(MockExchange exchange, string method) => exchange.Mock.Invocations.Count(i => i.Method.Name == method);

        [TestMethod]
        public async Task HaltedBot_CancelsItsOrderAndBooksTheFills_WithoutPlacingAnother()
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            // Filled 20 coins before the cancel took
            exchange.Mock
                .Setup(e => e.CancelOrder(BuyOrderId, Symbol))
                .ReturnsAsync(new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m, Quantity = 30, FillQuantity = 20, Proceeds = 0.002m });
            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();
            var market = new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick, BidRate = 0.00009999m, AskRate = 0.00010101m };
            var context = new BotContext(Configuration(), market, BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m, Quantity = 30 }
            };
            var bot = new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository));

            await bot.Halt(retryUntil: DateTime.UtcNow.AddMinutes(1));

            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.Once);
            Assert.AreEqual(BotState.Bought, context.BotState, "the fills are booked");
            Assert.AreEqual(20m, context.HeldAmount);
            Assert.IsNull(context.CurrentOrderData);
            Assert.AreEqual(0, OrderRequests(exchange, nameof(IExchange.SellLimit)), "the booking would normally place a sell at once");

            // Market data no longer reaches the strategy
            bot.Start();
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00012000m, bid: 0.00009000m));
            WaitUntil(() => context.LatestMarketData.AskRate == 0.00012000m && context.SemaphoreCurrentCount == 1, "the ticker is processed");

            Assert.AreEqual(0, OrderRequests(exchange, nameof(IExchange.SellLimit)));
            Assert.AreEqual(0, OrderRequests(exchange, nameof(IExchange.BuyLimit)));
            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.Once);
        }

        private static (MockExchange exchange, DataRepository repository, BotContext context) OpenBuy(decimal balance = 0.003m)
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            var market = new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick, BidRate = 0.00009999m, AskRate = 0.00010101m, LastTradeRate = 0.0001m };
            var context = new BotContext(Configuration(), market, BotState.BuyOrderActive, 0)
            {
                Balance = balance,
                CurrentOrderData = new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m, Quantity = 30 }
            };
            return (exchange, repository, context);
        }

        private static OrderData CancelledBuy(decimal filled) =>
            new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m, Quantity = 30, FillQuantity = filled, Proceeds = filled * 0.0001m };

        [TestMethod]
        public async Task Halt_RetriesACancelThatFailed()
        {
            var (exchange, repository, context) = OpenBuy();
            exchange.Mock
                .SetupSequence(e => e.CancelOrder(BuyOrderId, Symbol))
                .ThrowsAsync(new ApiException(ApiErrorType.RetryLater, "server busy"))
                .ReturnsAsync(CancelledBuy(filled: 0));
            var bot = new Bot(repository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository)) { HaltCancelRetryDelay = TimeSpan.FromMilliseconds(10) };

            await bot.Halt(retryUntil: DateTime.UtcNow.AddMinutes(1));

            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.Exactly(2));
            Assert.IsNull(context.CurrentOrderData);
            Assert.AreEqual(BotState.Buying, context.BotState);
        }

        [TestMethod]
        public void Halt_StopsRetryingAtTheDeadline()
        {
            var (exchange, repository, context) = OpenBuy();
            exchange.Mock.Setup(e => e.CancelOrder(BuyOrderId, Symbol)).ThrowsAsync(new ApiException(ApiErrorType.RetryLater, "server busy"));
            var bot = new Bot(repository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository)) { HaltCancelRetryDelay = TimeSpan.FromMilliseconds(10) };

            var halting = bot.Halt(retryUntil: DateTime.UtcNow.AddMilliseconds(300));

            Assert.IsTrue(halting.Wait(TimeSpan.FromSeconds(2)), "the halt ends");
            Assert.IsTrue(OrderRequests(exchange, nameof(IExchange.CancelOrder)) > 1, "retried until then");
            Assert.AreEqual(OrderStatus.OPEN, context.CurrentOrderData.Status, "still open, so the next session fetches it");
        }

        [TestMethod]
        public async Task Halt_PastTheDeadline_CancelsOnce()
        {
            var (exchange, repository, context) = OpenBuy();
            exchange.Mock.Setup(e => e.CancelOrder(BuyOrderId, Symbol)).ThrowsAsync(new InvalidOperationException("not an ApiException"));
            var bot = new Bot(repository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository)) { HaltCancelRetryDelay = TimeSpan.FromMilliseconds(10) };

            await bot.Halt(retryUntil: DateTime.UtcNow);

            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.Once);
            Assert.AreEqual(OrderStatus.OPEN, context.CurrentOrderData.Status);
        }

        [TestMethod]
        public async Task Halt_DoesNotRetryACancelTheExchangeRefusesTheAccountFor()
        {
            var (exchange, repository, context) = OpenBuy();
            exchange.Mock.Setup(e => e.CancelOrder(BuyOrderId, Symbol)).ThrowsAsync(new ApiException(ApiErrorType.Unauthorized, "Invalid API-key"));
            var bot = new Bot(repository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository)) { HaltCancelRetryDelay = TimeSpan.FromMilliseconds(10) };

            await bot.Halt(retryUntil: DateTime.UtcNow.AddMinutes(1));

            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.Once);
            Assert.AreEqual(OrderStatus.OPEN, context.CurrentOrderData.Status, "still open, so the next session fetches it");
        }

        [TestMethod]
        public async Task Halt_LeavesAnOrderWhosePlacementIsUnknownToTheNextSession()
        {
            var (exchange, repository, context) = OpenBuy();
            context.CurrentOrderData = new OrderData { ClientOrderId = "sb-unknown", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.UNKNOWN };
            var bot = new Bot(repository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository));

            await bot.Halt(retryUntil: DateTime.UtcNow.AddMinutes(1));

            exchange.Mock.Verify(e => e.CancelOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.AreEqual(OrderStatus.UNKNOWN, context.CurrentOrderData.Status);
        }

        [TestMethod]
        public async Task HaltedBot_LeftWithNothingToTrade_FinishesWithoutSellingItsDust()
        {
            // 9 coins (0.000909 BTC) and 0.0006 BTC are both below the 0.001 minimum, so the booking finishes the bot
            var (exchange, repository, context) = OpenBuy(balance: 0.0015m);
            exchange.Mock.Setup(e => e.CancelOrder(BuyOrderId, Symbol)).ReturnsAsync(CancelledBuy(filled: 9));
            var unallocated = false;
            var bot = new Bot(repository, context, _ => unallocated = true, new BotStrategiesFactory(), TestLedger.Empty(repository));

            await bot.Halt(retryUntil: DateTime.UtcNow.AddMinutes(1));

            Assert.AreEqual(BotState.FinishedWork, context.BotState);
            Assert.IsTrue(unallocated);
            Assert.AreEqual(9m, context.HeldAmount, "kept, so it's recorded as dust");
            exchange.Mock.Verify(e => e.SellMarket(It.IsAny<string>(), It.IsAny<decimal>()), Times.Never);
        }

        [TestMethod]
        public void BotRestoredAfterAHalt_IsHalted()
        {
            var exchange = new MockExchange(new BalanceData { CurrencyAbbreviation = "BTC", Total = 1, Amount = 1 });
            exchange.WithMarkets(Symbol);
            exchange.WithTickers(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            exchange.Mock.Setup(e => e.CancelOrder(BuyOrderId, Symbol)).ReturnsAsync(CancelledBuy(filled: 0));
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            var saved = new BotContext(Configuration(), new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick }, BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m }
            };
            var bots = new List<Bot>();
            var context = new Mock<ICoordinatorContext>();
            context.Setup(c => c.GetBotCount()).Returns(() => bots.Count);
            context.Setup(c => c.GetBots()).Returns(() => bots.ToList());
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bots.Add);
            context.Setup(c => c.GetPreviousSessionContext()).Returns(new PreviousSessionContext { BotContexts = new List<BotContext> { saved } });
            var settings = new AppSettings { MaxNumberOfBots = 3, ResyncIntervalMs = 600000, TryToRestoreSession = true, SpreadConfigurations = new[] { Configuration() } };
            var coordinator = new Coordinator(settings, repository, context.Object);

            coordinator.Halt("refused before the restore");
            coordinator.Start();

            WaitUntil(() => saved.BotState == BotState.Buying && saved.SemaphoreCurrentCount == 1, "the restored bot's buy is cancelled");
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010102m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            WaitUntil(() => saved.LatestMarketData.AskRate == 0.00010102m && saved.SemaphoreCurrentCount == 1, "the ticker is processed");

            Assert.AreEqual(1, bots.Count);
            Assert.AreEqual(0, OrderRequests(exchange, nameof(IExchange.BuyLimit)));
        }

        [TestMethod]
        public async Task FatalError_StopsAllocatingAndHaltsTheBots()
        {
            var exchange = new MockExchange(new BalanceData { CurrencyAbbreviation = "BTC", Total = 1, Amount = 1 });
            exchange.WithMarkets(Symbol, "AAA/BTC");
            exchange.WithTickers(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            exchange.Mock
                .Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()))
                .ReturnsAsync(new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m });
            exchange.Mock
                .Setup(e => e.CancelOrder(BuyOrderId, Symbol))
                .ReturnsAsync(new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m });
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var bots = new List<Bot>();
            var context = new Mock<ICoordinatorContext>();
            context.Setup(c => c.GetBotCount()).Returns(() => bots.Count);
            context.Setup(c => c.GetBots()).Returns(() => bots.ToList());
            context.Setup(c => c.AddBot(It.IsAny<Bot>())).Callback<Bot>(bots.Add);
            var settings = new AppSettings { MaxNumberOfBots = 3, ResyncIntervalMs = 600000, SpreadConfigurations = new[] { Configuration() } };
            var coordinator = new Coordinator(settings, repository, context.Object);
            coordinator.Start();

            WaitUntil(() => bots.Count == 1 && bots[0].botContext.BotState == BotState.BuyOrderActive && bots[0].botContext.SemaphoreCurrentCount == 1, "the bot's buy rests");

            exchange.OnFatalError(new ApiException(ApiErrorType.Unauthorized, "Invalid API-key, IP, or permissions for action."));

            Assert.IsTrue(coordinator.Halted.Wait(TimeSpan.FromSeconds(2)), "the halt completes");
            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.Once);
            Assert.AreEqual(BotState.Buying, bots[0].botContext.BotState, "the cancelled buy had no fills");

            // A market that becomes viable, then one more update so the first one's evaluation has run
            exchange.PushTicker(MockExchange.Ticker("AAA/BTC", ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 300, percentChange: 5));
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010102m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5));
            WaitUntil(() => repository.MarketsData[Symbol].AskRate == 0.00010102m && bots[0].botContext.SemaphoreCurrentCount == 1, "both updates are processed");

            Assert.AreEqual(1, bots.Count, "nothing is allocated after a halt");
            Assert.AreEqual(1, OrderRequests(exchange, nameof(IExchange.BuyLimit)), "the halted bot doesn't buy again");
        }
    }
}
