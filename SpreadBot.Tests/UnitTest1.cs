using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
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
            var openingMarketData = new MarketData()
            {
                AskRate = 10101.Satoshi(),
                BidRate = 9999.Satoshi(),
                PercentChange = 5,
                QuoteVolume = 10,
                Symbol = "NMR-BTC",
                Precision = 8
            };
            var openingBalanceData = new CompleteBalanceData(1, new[] { new BalanceData() { Amount = 1, CurrencyAbbreviation = "BTC" } });

            SpreadConfiguration spreadConfiguration = new SpreadConfiguration()
            {
                AllocatedAmountOfBaseCurrency = 100000.Satoshi(),
                SpreadThresholdBeforeCancelingCurrentOrder = 10.Satoshi(),
                MaxPercentChangeFromPreviousDay = 10,
                MinimumQuoteVolume = 10,
                MinimumSpreadPercentage = 1,
                MinutesForLoss = 0,
                MinimumProfitPercentage = 0
            };
            var appSettings = new AppSettings()
            {
                ApiKey = "ApiKey",
                ApiSecret = "ApiSecret",
                BaseMarket = "BTC",
                MaxNumberOfBots = 1,
                MinimumNegotiatedAmount = 50000.Satoshi(),
                MinimumPrice = 1000.Satoshi(),
                ResyncIntervalMs = 30000,
                SpreadConfigurations = new[] { spreadConfiguration }
            };

            Action<BittrexApiBalanceData> onBalancecallback = null;
            Action<BittrexApiMarketSummariesData> onSummariesCallBack = null;
            Action<BittrexApiTickersData> onTickersCallback = null;
            Action<BittrexApiOrderData> onOrderCallback = null;
            var mockExchange = new Mock<IExchange>();

            mockExchange
                .Setup(e => e.IsSetup)
                .Returns(true);
            mockExchange
                .Setup(e => e.FeeRate)
                .Returns(0);
            mockExchange
                .Setup(e => e.GetBalanceData())
                .ReturnsAsync(openingBalanceData);
            mockExchange
                .Setup(e => e.GetMarketSummariesData())
                .ReturnsAsync(new BittrexApiMarketSummariesData { Deltas = new BittrexApiMarketSummariesData.MarketSummary[0] });
            mockExchange
                .Setup(e => e.GetTickersData())
                .ReturnsAsync(new BittrexApiTickersData { Deltas = new BittrexApiTickersData.Ticker[0] });
            mockExchange
                .Setup(e => e.GetClosedOrdersData(It.IsAny<string>()))
                .ReturnsAsync(new ApiRestResponse<BittrexApiOrderData.Order[]> { Data = new BittrexApiOrderData.Order[0] });
            mockExchange
                .Setup(e => e.OnBalance(It.IsAny<Action<BittrexApiBalanceData>>()))
                .Callback((Action<BittrexApiBalanceData> action) => onBalancecallback = action);
            mockExchange
                .Setup(e => e.OnSummaries(It.IsAny<Action<BittrexApiMarketSummariesData>>()))
                .Callback((Action<BittrexApiMarketSummariesData> action) => onSummariesCallBack = action);
            mockExchange
                .Setup(e => e.OnTickers(It.IsAny<Action<BittrexApiTickersData>>()))
                .Callback((Action<BittrexApiTickersData> action) => onTickersCallback = action);
            mockExchange
                .Setup(e => e.OnOrder(It.IsAny<Action<BittrexApiOrderData>>()))
                .Callback((Action<BittrexApiOrderData> action) => onOrderCallback = action);

            var bidPrice = openingMarketData.BidRate.Value + 1.Satoshi();
            var askPrice = openingMarketData.AskRate.Value - 1.Satoshi();
            var quantity = spreadConfiguration.AllocatedAmountOfBaseCurrency / bidPrice;

            ManualResetEvent buyEvent = new ManualResetEvent(false);
            ManualResetEvent sellEvent = new ManualResetEvent(false);
            ManualResetEvent summariesEvent = new ManualResetEvent(false);
            ManualResetEvent tickersEvent = new ManualResetEvent(false);

            var buyOrderId = "buyOrder1";
            var sellOrderId = "sellOrder1";

            mockExchange
                .Setup(e => e.BuyLimit(openingMarketData.Symbol, quantity, bidPrice))
                .ReturnsAsync(new OrderData() { Id = buyOrderId, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN })
                .Callback(() =>
                {
                    Thread.Sleep(10);

                    onOrderCallback(new BittrexApiOrderData()
                    {
                        Sequence = 1,
                        Delta = new BittrexApiOrderData.Order
                        {
                            Id = buyOrderId,
                            Direction = OrderDirection.BUY,
                            Status = OrderStatus.CLOSED,
                            Limit = bidPrice,
                            Quantity = quantity,
                            FillQuantity = quantity,
                            Commission = 0,
                            Proceeds = spreadConfiguration.AllocatedAmountOfBaseCurrency,
                            MarketSymbol = openingMarketData.Symbol
                        }
                    });
                });

            mockExchange
                .Setup(e => e.SellLimit(openingMarketData.Symbol, quantity, askPrice))
                .ReturnsAsync(new OrderData() { Id = sellOrderId, Direction = OrderDirection.SELL, Status = OrderStatus.OPEN })
                .Callback(() =>
                {
                    Thread.Sleep(10);

                    onOrderCallback(new BittrexApiOrderData()
                    {
                        Sequence = 2,
                        Delta = new BittrexApiOrderData.Order
                        {
                            Id = sellOrderId,
                            Direction = OrderDirection.SELL,
                            Status = OrderStatus.CLOSED,
                            Limit = bidPrice,
                            Quantity = quantity,
                            FillQuantity = quantity,
                            Commission = 0,
                            Proceeds = spreadConfiguration.AllocatedAmountOfBaseCurrency,
                            MarketSymbol = openingMarketData.Symbol
                        }
                    });
                });

            var datarepository = new DataRepository(mockExchange.Object, appSettings);
            datarepository.StartConsumingData();

            var testGuid = new Guid();

            datarepository.SubscribeToMarketData(openingMarketData.Symbol, testGuid, (data) =>
            {
                if (data.QuoteVolume.HasValue)
                    summariesEvent.Set();
                if (data.AskRate.HasValue)
                    tickersEvent.Set();
            });
            datarepository.SubscribeToOrderData(buyOrderId, testGuid, (data) =>
            {
                if (data.Direction == OrderDirection.BUY)
                    buyEvent.Set();
            });
            datarepository.SubscribeToOrderData(sellOrderId, testGuid, (data) =>
            {
                if (data.Direction == OrderDirection.SELL)
                    sellEvent.Set();
            });

            onSummariesCallBack(new BittrexApiMarketSummariesData()
            {
                Sequence = 1,
                Deltas = new[]
                {
                    new BittrexApiMarketSummariesData.MarketSummary()
                    {
                        PercentChange = openingMarketData.PercentChange.Value,
                        QuoteVolume = openingMarketData.QuoteVolume.Value,
                        Symbol = openingMarketData.Symbol
                    }
                }
            });

            // Without this sleep test breaks because it tries to cancel the buy order
            Thread.Sleep(10);

            onTickersCallback(new BittrexApiTickersData()
            {
                Sequence = 1,
                Deltas = new[]
                {
                    new BittrexApiTickersData.Ticker()
                    {
                        AskRate = openingMarketData.AskRate.Value,
                        BidRate = openingMarketData.BidRate.Value,
                        Symbol = openingMarketData.Symbol
                    }
                }
            });

            var bot = new Bot(appSettings, datarepository, spreadConfiguration, openingMarketData, (bot) => { }, new BotStrategiesFactory());
            bot.Start();

            summariesEvent.WaitOne(100);
            tickersEvent.WaitOne(100);
            buyEvent.WaitOne(100);
            sellEvent.WaitOne(100);

            datarepository.UnsubscribeToMarketData(openingMarketData.Symbol, testGuid);
            datarepository.UnsubscribeToOrderData(buyOrderId, testGuid);
            datarepository.UnsubscribeToOrderData(sellOrderId, testGuid);

            mockExchange.Verify(e => e.IsSetup, Times.Once);
            mockExchange.Verify(e => e.OnBalance(It.IsAny<Action<BittrexApiBalanceData>>()), Times.Once);
            mockExchange.Verify(e => e.OnSummaries(It.IsAny<Action<BittrexApiMarketSummariesData>>()), Times.Once);
            mockExchange.Verify(e => e.OnTickers(It.IsAny<Action<BittrexApiTickersData>>()), Times.Once);
            mockExchange.Verify(e => e.OnOrder(It.IsAny<Action<BittrexApiOrderData>>()), Times.Once);
            mockExchange.Verify(e => e.GetBalanceData(), Times.Once);
            mockExchange.Verify(e => e.BuyLimit(openingMarketData.Symbol, quantity, bidPrice), Times.Once);
            mockExchange.Verify(e => e.SellLimit(openingMarketData.Symbol, quantity, askPrice), Times.Once);
        }
    }
}
