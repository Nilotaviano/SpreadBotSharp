using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class BuyBookingTests
    {
        private const string Symbol = "NMR-BTC";
        private const string BuyOrderId = "buy-2";
        private const decimal BoughtPrice = 0.00012000m;

        [TestMethod]
        public void BuyCancelledWithoutFills_KeepsThePriceAndLossTimerOfTheCoinsHeld()
        {
            var heldSince = DateTime.UtcNow.AddMinutes(-20);
            var exchange = new MockExchange();
            exchange.Mock
                .Setup(e => e.CancelOrder(BuyOrderId))
                .ReturnsAsync(new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.00009000m });

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();

            var configuration = new SpreadConfiguration
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 0.002m,
                MinimumNegotiatedAmount = 0.0001m,
                MinimumSpreadPercentage = 1,
                MinutesForLoss = 30,
                MinimumProfitPercentage = 1
            };
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, Precision = 8 }, BotState.BuyOrderActive, 5)
            {
                BoughtPrice = BoughtPrice,
                BuyFilledAt = heldSince,
                CurrentOrderData = new OrderData { Id = BuyOrderId, ClientOrderId = BuyOrderId, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.00009000m }
            };

            new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();

            // The bid moved well above the buy limit, so the strategy cancels the buy; it had no fills
            exchange.OnTickers(new BittrexApiTickersData
            {
                Sequence = 1,
                Deltas = new[] { new BittrexApiTickersData.Ticker { Symbol = Symbol, AskRate = 0.00010101m, BidRate = 0.00009999m } }
            });

            var stopwatch = Stopwatch.StartNew();
            while (context.BotState != BotState.Bought || context.SemaphoreCurrentCount != 1)
            {
                if (stopwatch.ElapsedMilliseconds > 2000)
                    Assert.Fail("Timed out waiting for the cancelled buy to be booked");
                Thread.Sleep(10);
            }

            Assert.AreEqual(5m, context.HeldAmount);
            Assert.AreEqual(BoughtPrice, context.BoughtPrice);
            Assert.AreEqual(heldSince, context.BuyFilledAt);
        }
    }
}
