using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
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
    [TestClass]
    public class BotApiErrorTests
    {
        private const string Symbol = "NMR/BTC";
        private const string BuyOrderId = "buy-1";

        /// <summary>
        /// A placement rejected with <paramref name="error"/> while the bot is in <paramref name="state"/> (Buying places a buy, Bought a sell).
        /// </summary>
        [TestMethod]
        [DataRow(BotState.Bought, ApiErrorType.DustTrade, BotState.Buying, false)] // too few coins to sell: buy more first
        [DataRow(BotState.Buying, ApiErrorType.DustTrade, BotState.FinishedWork, true)] // too little money to buy
        [DataRow(BotState.Buying, ApiErrorType.InsufficientFunds, BotState.FinishedWork, true)]
        [DataRow(BotState.Buying, ApiErrorType.MarketOffline, BotState.FinishedWork, true)]
        [DataRow(BotState.Bought, ApiErrorType.MarketOffline, BotState.Bought, false)] // holds coins: keeps trying to sell them
        [DataRow(BotState.Buying, ApiErrorType.RetryLater, BotState.Buying, false)]
        [DataRow(BotState.Buying, ApiErrorType.Throttled, BotState.Buying, false)]
        [DataRow(BotState.Bought, ApiErrorType.Unauthorized, BotState.Bought, false)]
        public void RejectedPlacement_IsHandledPerErrorType(BotState state, ApiErrorType error, BotState expectedState, bool expectFinished)
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(new ApiException(error, error.ToString()));
            exchange.Mock.Setup(e => e.SellLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>())).ThrowsAsync(new ApiException(error, error.ToString()));
            exchange.Mock.Setup(e => e.SellMarket(Symbol, It.IsAny<decimal>())).ReturnsAsync(new OrderData { Id = "dust", ClientOrderId = "dust", MarketSymbol = Symbol, Status = OrderStatus.CLOSED });

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", AllocatedAmountOfBaseCurrency = 0.002m, MinimumNegotiatedAmount = 0.001m, MinimumSpreadPercentage = 1 };
            var held = state == BotState.Bought ? 20m : 0m;
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick }, state, held) { BoughtPrice = 0.0001m };
            var finished = false;
            new Bot(dataRepository, context, _ => finished = true, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();

            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m));

            var placement = state == BotState.Bought ? nameof(exchange.Object.SellLimit) : nameof(exchange.Object.BuyLimit);
            var stopwatch = Stopwatch.StartNew();
            while (exchange.Mock.Invocations.Count(i => i.Method.Name == placement) == 0 || context.SemaphoreCurrentCount != 1)
            {
                if (stopwatch.ElapsedMilliseconds > 2000)
                    Assert.Fail($"Timed out waiting for the rejected {placement} to be handled");
                Thread.Sleep(10);
            }

            Assert.AreEqual(1, exchange.Mock.Invocations.Count(i => i.Method.Name is nameof(exchange.Object.BuyLimit) or nameof(exchange.Object.SellLimit)), $"only the {placement} {state} places");
            Assert.AreEqual(expectedState, context.BotState);
            Assert.AreEqual(expectFinished, finished);
        }

        [TestMethod]
        [DataRow(ApiErrorType.RetryLater)] // e.g. its trades don't show its fees yet: caught in the arm
        [DataRow(ApiErrorType.UnknownError)] // reaches ProcessMessageAsync's handler
        public void OrderNotOpen_WhenFollowUpLookupFails_IsLoggedInsteadOfCrashing(ApiErrorType lookupError)
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            exchange.Mock
                .Setup(e => e.CancelOrder(BuyOrderId, Symbol))
                .ThrowsAsync(new ApiException(ApiErrorType.OrderNotOpen, "ORDER_NOT_OPEN"));
            exchange.Mock
                .Setup(e => e.GetOrder(BuyOrderId, Symbol))
                .ThrowsAsync(new ApiException(lookupError, "exchange unavailable"));

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 60000 });
            dataRepository.StartConsumingData();

            var configuration = new SpreadConfiguration
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 0.002m,
                MinimumNegotiatedAmount = 0.001m,
                MinimumSpreadPercentage = 1
            };
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick }, BotState.BuyOrderActive, 0)
            {
                CurrentOrderData = new OrderData
                {
                    Id = BuyOrderId,
                    ClientOrderId = BuyOrderId,
                    Direction = OrderDirection.BUY,
                    Status = OrderStatus.OPEN,
                    Limit = 0.00009000m
                }
            };

            new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();

            // Bid moved well above our buy limit, so the strategy cancels; the exchange says the order is no longer open
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m));

            var stopwatch = Stopwatch.StartNew();
            while (exchange.Mock.Invocations.Count(i => i.Method.Name == nameof(exchange.Object.GetOrder)) < 2 || context.SemaphoreCurrentCount != 1)
            {
                if (stopwatch.ElapsedMilliseconds > 2000)
                    Assert.Fail("Timed out waiting for the ORDER_NOT_OPEN follow-up lookup");
                Thread.Sleep(10);
            }

            exchange.Mock.Verify(e => e.CancelOrder(BuyOrderId, Symbol), Times.AtLeastOnce);
            Assert.AreEqual(BotState.BuyOrderActive, context.BotState);
            Assert.AreEqual(BuyOrderId, context.CurrentOrderData.Id);
        }
    }
}
