using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class UnitTest1
    {
        [TestMethod]
        public void BasicBotFlowTest()
        {
            const string symbol = "NMR/BTC";
            var openingMarketData = MockExchange.Ticker(symbol, ask: 10101 * MockExchange.Tick, bid: 9999 * MockExchange.Tick, quoteVolume: 10, percentChange: 5);
            openingMarketData.PriceTick = MockExchange.Tick;

            SpreadConfiguration spreadConfiguration = new SpreadConfiguration()
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 100000 * MockExchange.Tick,
                SpreadThresholdBeforeCancelingCurrentOrder = 10 * MockExchange.Tick,
                MaxPercentChangeFromPreviousDay = 10,
                MinimumQuoteVolume = 10,
                MinimumSpreadPercentage = 1,
                MinutesForLoss = 0,
                MinimumNegotiatedAmount = 50000 * MockExchange.Tick,
                MinimumPrice = 1000 * MockExchange.Tick
            };
            var appSettings = new AppSettings()
            {
                ApiKey = "ApiKey",
                ApiSecret = "ApiSecret",
                MaxNumberOfBots = 1,
                ResyncIntervalMs = 30000,
                SpreadConfigurations = new[] { spreadConfiguration }
            };

            var exchange = new MockExchange(new BalanceData { Amount = 1, Total = 1, CurrencyAbbreviation = "BTC" });
            exchange.WithMarkets(symbol);

            var bidPrice = openingMarketData.BidRate.Value + MockExchange.Tick;
            var askPrice = openingMarketData.AskRate.Value - MockExchange.Tick;
            var quantity = spreadConfiguration.AllocatedAmountOfBaseCurrency / bidPrice;

            ManualResetEvent buyEvent = new ManualResetEvent(false);
            ManualResetEvent sellEvent = new ManualResetEvent(false);
            ManualResetEvent marketEvent = new ManualResetEvent(false);

            var buyOrderId = "buyOrder1";
            var sellOrderId = "sellOrder1";

            exchange.Mock
                .Setup(e => e.BuyLimit(symbol, quantity, bidPrice))
                .ReturnsAsync(new OrderData() { Id = buyOrderId, ClientOrderId = buyOrderId, MarketSymbol = symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN })
                .Callback(() =>
                {
                    Thread.Sleep(10);

                    exchange.OnOrder(new OrderData
                    {
                        Id = buyOrderId,
                        ClientOrderId = buyOrderId,
                        Direction = OrderDirection.BUY,
                        Status = OrderStatus.CLOSED,
                        Limit = bidPrice,
                        Quantity = quantity,
                        FillQuantity = quantity,
                        Commission = 0,
                        Proceeds = spreadConfiguration.AllocatedAmountOfBaseCurrency,
                        MarketSymbol = symbol
                    });
                });

            exchange.Mock
                .Setup(e => e.SellLimit(symbol, quantity, askPrice))
                .ReturnsAsync(new OrderData() { Id = sellOrderId, ClientOrderId = sellOrderId, MarketSymbol = symbol, Direction = OrderDirection.SELL, Status = OrderStatus.OPEN })
                .Callback(() =>
                {
                    Thread.Sleep(10);

                    exchange.OnOrder(new OrderData
                    {
                        Id = sellOrderId,
                        ClientOrderId = sellOrderId,
                        Direction = OrderDirection.SELL,
                        Status = OrderStatus.CLOSED,
                        Limit = bidPrice,
                        Quantity = quantity,
                        FillQuantity = quantity,
                        Commission = 0,
                        Proceeds = spreadConfiguration.AllocatedAmountOfBaseCurrency,
                        MarketSymbol = symbol
                    });
                });

            var datarepository = new DataRepository(exchange.Object, appSettings);
            datarepository.StartConsumingData();

            var testGuid = Guid.NewGuid();

            datarepository.SubscribeToMarketData(symbol, testGuid, (data) =>
            {
                if (data.QuoteVolume.HasValue && data.AskRate.HasValue)
                    marketEvent.Set();
            });
            datarepository.SubscribeToOrderData(new OrderData { Id = buyOrderId, ClientOrderId = buyOrderId, MarketSymbol = symbol }, testGuid, (data) =>
            {
                if (data.Direction == OrderDirection.BUY)
                    buyEvent.Set();
            });
            datarepository.SubscribeToOrderData(new OrderData { Id = sellOrderId, ClientOrderId = sellOrderId, MarketSymbol = symbol }, testGuid, (data) =>
            {
                if (data.Direction == OrderDirection.SELL)
                    sellEvent.Set();
            });

            exchange.PushTicker(openingMarketData);

            var bot = new Bot(datarepository, spreadConfiguration, openingMarketData, 0, (bot) => { }, TestStrategies.Factory(), TestLedger.Empty(datarepository));
            bot.Start();

            Assert.IsTrue(marketEvent.WaitOne(TimeSpan.FromSeconds(5)), "market data not delivered");
            Assert.IsTrue(buyEvent.WaitOne(TimeSpan.FromSeconds(5)), "buy order not delivered");
            Assert.IsTrue(sellEvent.WaitOne(TimeSpan.FromSeconds(5)), "sell order not delivered");

            datarepository.UnsubscribeToMarketData(symbol, testGuid);
            datarepository.UnsubscribeToOrderData(buyOrderId, testGuid);
            datarepository.UnsubscribeToOrderData(sellOrderId, testGuid);

            exchange.Mock.Verify(e => e.IsSetup, Times.Once);
            exchange.Mock.Verify(e => e.OnBalance(It.IsAny<Action<BalanceData>>()), Times.Once);
            exchange.Mock.Verify(e => e.OnMarketData(It.IsAny<Action<System.Collections.Generic.IEnumerable<MarketData>>>()), Times.Once);
            exchange.Mock.Verify(e => e.OnOrder(It.IsAny<Action<OrderData>>()), Times.Once);
            exchange.Mock.Verify(e => e.GetBalances(), Times.Once);
            exchange.Mock.Verify(e => e.BuyLimit(symbol, quantity, bidPrice), Times.Once);
            exchange.Mock.Verify(e => e.SellLimit(symbol, quantity, askPrice), Times.Once);
        }
    }
}
