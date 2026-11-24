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
    public class BotRestoreTests
    {
        private const string Symbol = "NMR-BTC";
        private const string SellOrderId = "sell-1";

        private static BotContext RestoredContextWithOpenSellOrder()
        {
            var configuration = new SpreadConfiguration
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 0.002m,
                MinimumNegotiatedAmount = 0.001m,
                MinutesForLoss = 30
            };
            var market = new MarketData { Symbol = Symbol, BidRate = 0.00009999m, AskRate = 0.00010101m, Precision = 8 };

            return new BotContext(configuration, market, BotState.Buying, 0)
            {
                Balance = 0,
                HeldAmount = 20,
                BoughtPrice = 0.0001m,
                BotState = BotState.SellOrderActive,
                CurrentOrderData = new OrderData
                {
                    Id = SellOrderId,
                    ClientOrderId = SellOrderId,
                    Direction = OrderDirection.SELL,
                    Status = OrderStatus.OPEN,
                    Limit = 0.0001m
                }
            };
        }

        private static void WaitUntil(Func<bool> condition)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition() && stopwatch.ElapsedMilliseconds < 2000)
                Thread.Sleep(10);
        }

        [TestMethod]
        public void Start_WhenOrderLookupFails_StillReceivesTheFillOverWebsocket()
        {
            var exchange = new MockExchange();
            exchange.Mock
                .Setup(e => e.GetOrderData(SellOrderId))
                .ThrowsAsync(new ApiException(ApiErrorType.UnknownError, "exchange unavailable"));

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 60000 });
            dataRepository.StartConsumingData();

            var context = RestoredContextWithOpenSellOrder();
            var bot = new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory());
            bot.Start();

            exchange.Mock.Verify(e => e.GetOrderData(SellOrderId), Times.Once);

            exchange.OnOrder(new BittrexApiOrderData
            {
                Sequence = 1,
                Delta = new BittrexApiOrderData.Order
                {
                    Id = SellOrderId,
                    Direction = OrderDirection.SELL,
                    Status = OrderStatus.CLOSED,
                    Limit = 0.0001m,
                    Quantity = 20,
                    FillQuantity = 20,
                    Proceeds = 0.002m,
                    Commission = 0,
                    MarketSymbol = Symbol
                }
            });

            WaitUntil(() => context.BotState == BotState.Buying);

            Assert.AreEqual(BotState.Buying, context.BotState);
            Assert.AreEqual(0m, context.HeldAmount);
            Assert.AreEqual(0.002m, context.Balance);
            Assert.IsNull(context.CurrentOrderData);
        }

        [TestMethod]
        public void Start_WhenOrderLookupSucceeds_ProcessesTheFetchedOrder()
        {
            var exchange = new MockExchange();
            exchange.Mock
                .Setup(e => e.GetOrderData(SellOrderId))
                .ReturnsAsync(new OrderData
                {
                    Id = SellOrderId,
                    ClientOrderId = SellOrderId,
                    Direction = OrderDirection.SELL,
                    Status = OrderStatus.CLOSED,
                    Limit = 0.0001m,
                    FillQuantity = 20,
                    Proceeds = 0.002m
                });

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 60000 });
            dataRepository.StartConsumingData();

            var context = RestoredContextWithOpenSellOrder();
            var bot = new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory());
            bot.Start();

            WaitUntil(() => context.BotState == BotState.Buying);

            Assert.AreEqual(BotState.Buying, context.BotState);
            Assert.AreEqual(0m, context.HeldAmount);
            Assert.AreEqual(0.002m, context.Balance);
        }
    }
}
