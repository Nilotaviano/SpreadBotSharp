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
