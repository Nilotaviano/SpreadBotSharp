using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SpreadBot.Tests
{
    /// <summary>
    /// An order whose placement is unknown may be live, so the bot must not place another one until the exchange
    /// reports it or can no longer accept it.
    /// </summary>
    [TestClass]
    public class UnknownOrderTests
    {
        private const string Symbol = "NMR/BTC";
        private const string UnknownClientOrderId = "sb-unknown";

        private static OrderData Unknown(DateTime sentAt) => new OrderData
        {
            ClientOrderId = UnknownClientOrderId,
            MarketSymbol = Symbol,
            Direction = OrderDirection.BUY,
            Type = OrderType.LIMIT,
            Quantity = 20,
            Limit = 0.0001m,
            Status = OrderStatus.UNKNOWN,
            CreatedAt = sentAt
        };

        private static OrderData Reported(string id, string clientOrderId, OrderStatus status, OrderDirection direction = OrderDirection.BUY, decimal fill = 0, decimal proceeds = 0) => new OrderData
        {
            Id = id,
            ClientOrderId = clientOrderId,
            MarketSymbol = Symbol,
            Direction = direction,
            Type = OrderType.LIMIT,
            Quantity = 20,
            Limit = 0.0001m,
            FillQuantity = fill,
            Proceeds = proceeds,
            Status = status
        };

        private static OrderStateUnknownException UnknownPlacement(DateTime sentAt) => new OrderStateUnknownException(Unknown(sentAt), "binance timed out");

        private static MockExchange Exchange()
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            return exchange;
        }

        private static BotContext StartBuyingBot(MockExchange exchange, OrderData currentOrder = null) => StartBuyingBot(exchange, out _, currentOrder);

        private static BotContext StartBuyingBot(MockExchange exchange, out DataRepository dataRepository, OrderData currentOrder = null)
        {
            dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();

            var configuration = new SpreadConfiguration
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 0.002m,
                MinimumNegotiatedAmount = 0.001m,
                MinimumSpreadPercentage = 1
            };
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick }, BotState.Buying, 0)
            {
                CurrentOrderData = currentOrder
            };

            new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();

            return context;
        }

        private static decimal AskOf(int sequence) => 0.00010101m + sequence * MockExchange.Tick;

        private static MarketData Ticker(int sequence) => MockExchange.Ticker(Symbol, ask: AskOf(sequence), bid: 0.00009999m, last: 0.0001m);

        private static void ProcessTicker(MockExchange exchange, BotContext context, int sequence)
        {
            exchange.PushTicker(Ticker(sequence));
            WaitUntil(() => context.LatestMarketData.AskRate == AskOf(sequence) && context.SemaphoreCurrentCount == 1, $"ticker {sequence} is processed");
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

        private static int Lookups(MockExchange exchange) =>
            exchange.Mock.Invocations.Count(i => i.Method.Name == nameof(IExchange.GetOrderByClientId));

        [TestMethod]
        public void NotFoundYet_IsNotPlacedAgain()
        {
            var exchange = Exchange();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow));
            exchange.Mock.Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol)).ThrowsAsync(new ApiException(ApiErrorType.OrderNotOpen, "Order does not exist."));
            var context = StartBuyingBot(exchange);

            ProcessTicker(exchange, context, 1);
            Assert.AreEqual(OrderStatus.UNKNOWN, context.CurrentOrderData?.Status);

            for (var sequence = 2; sequence <= 6; sequence++)
                ProcessTicker(exchange, context, sequence);

            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Once);
            Assert.IsTrue(Lookups(exchange) is >= 1 and <= 2, $"lookups are spaced out, not one per ticker ({Lookups(exchange)} for 5 tickers)");
            Assert.AreEqual(BotState.Buying, context.BotState);
            Assert.AreEqual(UnknownClientOrderId, context.CurrentOrderData?.ClientOrderId);
        }

        [TestMethod]
        public void LookupFailingWithAnErrorThatWouldFinishABuyingBot_KeepsWaiting()
        {
            // MarketOffline while Buying finishes a bot whose buy was rejected; here the buy may be live
            var exchange = Exchange();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow.AddMinutes(-2)));
            exchange.Mock.Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol)).ThrowsAsync(new ApiException(ApiErrorType.MarketOffline, "binance does not have market symbol NMR/BTC"));
            var context = StartBuyingBot(exchange);

            ProcessTicker(exchange, context, 1);
            ProcessTicker(exchange, context, 2);

            Assert.AreEqual(1, Lookups(exchange));
            Assert.AreEqual(BotState.Buying, context.BotState);
            Assert.AreEqual(OrderStatus.UNKNOWN, context.CurrentOrderData?.Status, "past the request lifetime, only a confirmed absence drops it");
            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Once);
        }

        [TestMethod]
        public void FoundByTheLookup_BecomesTheCurrentOrder()
        {
            var exchange = Exchange();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow));
            exchange.Mock.Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol)).ReturnsAsync(Reported("42", UnknownClientOrderId, OrderStatus.OPEN));
            var context = StartBuyingBot(exchange);

            ProcessTicker(exchange, context, 1);
            ProcessTicker(exchange, context, 2);

            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            Assert.AreEqual("42", context.CurrentOrderData.Id);
            Assert.AreEqual(OrderStatus.OPEN, context.CurrentOrderData.Status);
            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Once);
        }

        [TestMethod]
        public void ReportedByTheStream_BecomesTheCurrentOrder()
        {
            var exchange = Exchange();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow));
            var context = StartBuyingBot(exchange);

            ProcessTicker(exchange, context, 1);
            exchange.OnOrder(Reported("42", UnknownClientOrderId, OrderStatus.OPEN));

            WaitUntil(() => context.BotState == BotState.BuyOrderActive && context.SemaphoreCurrentCount == 1, "the streamed order is adopted");
            Assert.AreEqual("42", context.CurrentOrderData.Id);
            Assert.AreEqual(0, Lookups(exchange));
        }

        [TestMethod]
        public void StillNotFoundAfterTheExchangeCanNoLongerAcceptIt_IsPlacedAgain()
        {
            var exchange = Exchange();
            exchange.Mock
                .SetupSequence(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()))
                .ThrowsAsync(UnknownPlacement(DateTime.UtcNow.AddMinutes(-2)))
                .ReturnsAsync(Reported("43", "sb-second", OrderStatus.OPEN));
            exchange.Mock.Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol)).ThrowsAsync(new ApiException(ApiErrorType.OrderNotOpen, "Order does not exist."));
            var context = StartBuyingBot(exchange);

            ProcessTicker(exchange, context, 1);
            ProcessTicker(exchange, context, 2);
            Assert.IsNull(context.CurrentOrderData, "an order the exchange can no longer accept was never placed");
            Assert.AreEqual(BotState.Buying, context.BotState);

            ProcessTicker(exchange, context, 3);

            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            Assert.AreEqual("43", context.CurrentOrderData.Id);
            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Exactly(2));
        }

        [TestMethod]
        public void ReportedByTheStreamWhileTheLookupMissesIt_IsAdoptedNotDropped()
        {
            // Past the request lifetime a confirmed absence drops the order, but the stream's report is already cached and waits behind the lookup
            var exchange = Exchange();
            DataRepository dataRepository = null;
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow.AddMinutes(-2)));
            exchange.Mock
                .Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol))
                .Callback(() =>
                {
                    exchange.OnOrder(Reported("42", UnknownClientOrderId, OrderStatus.OPEN));
                    WaitUntil(() => dataRepository.OrdersData.ContainsKey(UnknownClientOrderId), "the stream's report is cached");
                })
                .ThrowsAsync(new ApiException(ApiErrorType.OrderNotOpen, "Order does not exist."));
            var context = StartBuyingBot(exchange, out dataRepository);

            ProcessTicker(exchange, context, 1);
            exchange.PushTicker(Ticker(2));
            WaitUntil(() => context.BotState == BotState.BuyOrderActive && context.SemaphoreCurrentCount == 1, "the reported order is adopted");
            ProcessTicker(exchange, context, 3);

            Assert.AreEqual("42", context.CurrentOrderData.Id);
            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Once);
        }

        [TestMethod]
        [DataRow(null, OrderStatus.OPEN)]
        [DataRow("42", OrderStatus.UNDEFINED)] // ccxt's status was null
        public void LookupAnswerWithoutAnIdOrStatus_KeepsWaiting(string id, OrderStatus status)
        {
            var exchange = Exchange();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow));
            exchange.Mock.Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol)).ReturnsAsync(Reported(id, UnknownClientOrderId, status));
            var context = StartBuyingBot(exchange);

            for (var sequence = 1; sequence <= 3; sequence++)
                ProcessTicker(exchange, context, sequence);

            Assert.AreEqual(1, Lookups(exchange));
            Assert.AreEqual(OrderStatus.UNKNOWN, context.CurrentOrderData?.Status);
            Assert.AreEqual(BotState.Buying, context.BotState);
            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Once);
        }

        [TestMethod]
        public void StreamReportWithoutAStatus_IsNotAdopted()
        {
            // Adopting it would unsubscribe the client order id, so the order's later updates would never reach the bot
            var exchange = Exchange();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(UnknownPlacement(DateTime.UtcNow));
            var context = StartBuyingBot(exchange, out var dataRepository);

            ProcessTicker(exchange, context, 1);
            exchange.OnOrder(Reported("42", UnknownClientOrderId, OrderStatus.UNDEFINED));
            WaitUntil(() => dataRepository.OrdersData.TryGetValue(UnknownClientOrderId, out var cached) && cached.Status == OrderStatus.UNDEFINED && context.SemaphoreCurrentCount == 1, "the report without a status is processed");
            Assert.AreEqual(OrderStatus.UNKNOWN, context.CurrentOrderData?.Status);

            exchange.OnOrder(Reported("42", UnknownClientOrderId, OrderStatus.OPEN));

            WaitUntil(() => context.BotState == BotState.BuyOrderActive && context.SemaphoreCurrentCount == 1, "the report with a status is adopted");
            Assert.AreEqual("42", context.CurrentOrderData.Id);
            Assert.AreEqual(OrderStatus.OPEN, context.CurrentOrderData.Status);
        }

        [TestMethod]
        public void RestoredSession_LooksTheOrderUpByClientIdAndBooksItsFill()
        {
            var exchange = Exchange();
            exchange.WithTickers(Ticker(0));
            exchange.Mock
                .Setup(e => e.GetOrderByClientId(UnknownClientOrderId, Symbol))
                .ReturnsAsync(Reported("42", UnknownClientOrderId, OrderStatus.CLOSED, fill: 20, proceeds: 0.0019m));
            exchange.Mock
                .Setup(e => e.SellLimit(Symbol, 20, It.IsAny<decimal>()))
                .ReturnsAsync(Reported("s1", "sb-sell", OrderStatus.OPEN, OrderDirection.SELL));

            var context = StartBuyingBot(exchange, Unknown(DateTime.UtcNow.AddSeconds(-10)));

            WaitUntil(() => context.BotState == BotState.SellOrderActive && context.SemaphoreCurrentCount == 1, "the found buy is booked and a sell placed");
            Assert.AreEqual(20m, context.HeldAmount);
            Assert.AreEqual(0.0001m, context.Balance);
            Assert.AreEqual("s1", context.CurrentOrderData.Id);
            exchange.Mock.Verify(e => e.GetOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}
