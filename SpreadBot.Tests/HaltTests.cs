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

            await bot.Halt();

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
