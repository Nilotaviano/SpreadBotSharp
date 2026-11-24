using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
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
        private const string Symbol = "NMR/BTC";
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
            var market = new MarketData { Symbol = Symbol, BidRate = 0.00009999m, AskRate = 0.00010101m, PriceTick = MockExchange.Tick };

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
        public void Start_WhenOrderLookupFails_StillReceivesTheFillOverWebsocket()
        {
            var exchange = new MockExchange();
            exchange.Mock
                .Setup(e => e.GetOrder(SellOrderId, Symbol))
                .ThrowsAsync(new ApiException(ApiErrorType.UnknownError, "exchange unavailable"));

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 60000 });
            dataRepository.StartConsumingData();

            var context = RestoredContextWithOpenSellOrder();
            var bot = new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository));
            bot.Start();

            exchange.Mock.Verify(e => e.GetOrder(SellOrderId, Symbol), Times.Once);

            exchange.OnOrder(new OrderData
            {
                ClientOrderId = SellOrderId,
                Id = SellOrderId,
                Direction = OrderDirection.SELL,
                Status = OrderStatus.CLOSED,
                Limit = 0.0001m,
                Quantity = 20,
                FillQuantity = 20,
                Proceeds = 0.002m,
                Commission = 0,
                MarketSymbol = Symbol
            });

            WaitUntil(() => context.BotState == BotState.Buying && context.SemaphoreCurrentCount == 1, "the sell fill is processed");

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
                .Setup(e => e.GetOrder(SellOrderId, Symbol))
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
            var bot = new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository));
            bot.Start();

            WaitUntil(() => context.BotState == BotState.Buying && context.SemaphoreCurrentCount == 1, "the sell fill is processed");

            Assert.AreEqual(BotState.Buying, context.BotState);
            Assert.AreEqual(0m, context.HeldAmount);
            Assert.AreEqual(0.002m, context.Balance);
        }

        [TestMethod]
        public void BuyFill_WithFeeTakenInTheCoin_HoldsTheNetAmount()
        {
            const string buyOrderId = "buy-1";
            var exchange = new MockExchange();
            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 60000 });
            dataRepository.StartConsumingData();

            var configuration = new SpreadConfiguration { BaseMarket = "BTC", AllocatedAmountOfBaseCurrency = 0.002m, MinimumNegotiatedAmount = 0.0001m };
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick, AskRate = 0.0001m }, BotState.BuyOrderActive, 0)
            {
                Balance = 0.002m,
                CurrentOrderData = new OrderData { Id = buyOrderId, ClientOrderId = buyOrderId, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN }
            };
            new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();

            exchange.OnOrder(new OrderData
            {
                Id = buyOrderId,
                ClientOrderId = buyOrderId,
                MarketSymbol = Symbol,
                Direction = OrderDirection.BUY,
                Status = OrderStatus.CLOSED,
                FillQuantity = 20,
                Proceeds = 0.002m,
                CoinCommission = 0.02m
            });

            WaitUntil(() => context.CurrentOrderData == null && context.SemaphoreCurrentCount == 1, "the buy fill is processed");

            Assert.AreEqual(19.98m, context.HeldAmount);
            Assert.AreEqual(0m, context.Balance);
        }

        [TestMethod]
        public void Start_WithHeldCoinsButNoFillTime_StartsTheLossTimer()
        {
            var dataRepository = new DataRepository(new MockExchange().Object, new AppSettings { ResyncIntervalMs = 60000 });
            var context = RestoredContextWithOpenSellOrder();
            context.CurrentOrderData = null;
            context.BotState = BotState.Bought;
            Assert.IsNull(context.BuyFilledAt);

            var before = DateTime.UtcNow;
            new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();
            var after = DateTime.UtcNow;

            Assert.IsTrue(context.BuyFilledAt >= before && context.BuyFilledAt <= after);
        }

        [TestMethod]
        public void Start_NewBotInheritingDust_HasNoFillTimeUntilItBuys()
        {
            var dataRepository = new DataRepository(new MockExchange().Object, new AppSettings { ResyncIntervalMs = 60000 });
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", AllocatedAmountOfBaseCurrency = 0.002m, MinimumNegotiatedAmount = 0.001m };
            var market = new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick };

            var bot = new Bot(dataRepository, configuration, market, 20, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository));
            bot.Start();

            Assert.AreEqual(BotState.Buying, bot.botContext.BotState);
            Assert.IsNull(bot.botContext.BuyFilledAt);
        }
    }
}
