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
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    /// <summary>
    /// Market data that waited for the bot behind an order request is stale once newer data arrived: only the newest is acted on.
    /// </summary>
    [TestClass]
    public class MarketDataCoalescingTests
    {
        private const string Symbol = "NMR/BTC";

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
        public void TickersQueuedBehindAnOrderRequest_OnlyTheNewestIsActedOn()
        {
            var exchange = new MockExchange();
            exchange.WithMarkets(Symbol);
            var placing = new TaskCompletionSource<OrderData>();
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), 0.00010000m)).Returns(placing.Task);
            exchange.Mock
                .Setup(e => e.CancelOrder("b1", Symbol))
                .ReturnsAsync(new OrderData { Id = "b1", ClientOrderId = "sb-b1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED });

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", AllocatedAmountOfBaseCurrency = 0.002m, MinimumNegotiatedAmount = 0.001m, MinimumSpreadPercentage = 1, SpreadThresholdBeforeCancelingCurrentOrder = MockExchange.Tick };
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, PriceTick = MockExchange.Tick }, BotState.Buying, 0);
            new Bot(dataRepository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(dataRepository)).Start();

            // The bot bids one tick above 0.00009999 and waits for the exchange's answer
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m));
            WaitUntil(() => context.SemaphoreCurrentCount == 0, "the buy is being placed");

            // Meanwhile someone outbids it, then cancels: the bid is the bot's own again
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010150m, bid: 0.00010005m));
            exchange.PushTicker(MockExchange.Ticker(Symbol, ask: 0.00010150m, bid: 0.00010000m));
            // The market consumer hands tickers over in order, so once a later one is cached both reached the bot
            exchange.PushTicker(MockExchange.Ticker("MARKER/BTC", ask: 1, bid: 1));
            WaitUntil(() => dataRepository.MarketsData.ContainsKey("MARKER/BTC"), "both tickers reached the bot");

            placing.SetResult(new OrderData { Id = "b1", ClientOrderId = "sb-b1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Limit = 0.00010000m, Quantity = 19, Status = OrderStatus.OPEN });

            WaitUntil(() => context.BotState == BotState.BuyOrderActive && context.LatestMarketData.BidRate == 0.00010000m && context.SemaphoreCurrentCount == 1, "the newest ticker is processed");
            exchange.Mock.Verify(e => e.CancelOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never, "the outbid it reacted to was already gone");
            Assert.AreEqual("b1", context.CurrentOrderData.Id);
        }
    }
}
