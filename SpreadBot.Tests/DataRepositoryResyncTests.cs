using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class DataRepositoryResyncTests
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

        private static OrderData Order(string id, OrderStatus status, decimal filled = 0) =>
            new OrderData { Id = id, ClientOrderId = id, MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = status, FillQuantity = filled };

        /// <summary>
        /// Consumers are paused during a resync, so once a ticker pushed afterwards is consumed the resync has finished.
        /// </summary>
        private static void WaitForResyncToFinish(MockExchange exchange, DataRepository repository)
        {
            var marker = $"MARKER{Guid.NewGuid():N}/BTC";
            exchange.PushTicker(new MarketData { Symbol = marker });
            WaitUntil(() => repository.MarketsData.ContainsKey(marker), "the resync finishes");
        }

        [TestMethod]
        public void StreamInterruption_FetchesTrackedOrdersNoLongerOpen()
        {
            var exchange = new MockExchange();
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData> { Order("1", OrderStatus.OPEN) });

            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var received = new ConcurrentQueue<OrderData>();
            repository.SubscribeToOrderData(Order("1", OrderStatus.OPEN), Guid.NewGuid(), received.Enqueue);
            Assert.AreEqual(OrderStatus.OPEN, received.Single().Status, "the cached open order is replayed on subscribe");

            // While the stream was down the order filled: it's no longer open on the exchange
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData>());
            exchange.Mock.Setup(e => e.GetOrder("1", Symbol)).ReturnsAsync(Order("1", OrderStatus.CLOSED, filled: 5));

            exchange.OnStreamInterrupted();

            WaitUntil(() => received.Count == 2, "the resync delivers the order's final state");
            var closed = received.Last();
            Assert.AreEqual(OrderStatus.CLOSED, closed.Status);
            Assert.AreEqual(5m, closed.FillQuantity);
        }

        [TestMethod]
        public void StreamInterruption_FetchesATrackedOrderThatNoUpdateEverMentioned()
        {
            // The bot placed the order through REST; the stream went down before it reported anything
            var exchange = new MockExchange();
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var received = new ConcurrentQueue<OrderData>();
            repository.SubscribeToOrderData(Order("7", OrderStatus.OPEN), Guid.NewGuid(), received.Enqueue);
            Assert.IsTrue(received.IsEmpty);

            exchange.Mock.Setup(e => e.GetOrder("7", Symbol)).ReturnsAsync(Order("7", OrderStatus.CLOSED, filled: 2));
            exchange.OnStreamInterrupted();

            WaitUntil(() => received.Count == 1, "the resync fetches the order");
            Assert.AreEqual(OrderStatus.CLOSED, received.Single().Status);
        }

        [TestMethod]
        public void Resync_DoesNotFetchUntrackedStillOpenOrUnsubscribedOrders()
        {
            var exchange = new MockExchange();
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData> { Order("1", OrderStatus.OPEN), Order("2", OrderStatus.OPEN) });

            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var subscription = Guid.NewGuid();
            repository.SubscribeToOrderData(Order("1", OrderStatus.OPEN), Guid.NewGuid(), _ => { });
            repository.SubscribeToOrderData(Order("3", OrderStatus.OPEN), subscription, _ => { });
            repository.UnsubscribeToOrderData("3", subscription);

            // "1" is still open and tracked; "2" is open but untracked; "3" was unsubscribed
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData> { Order("1", OrderStatus.OPEN) });
            exchange.OnStreamInterrupted();
            WaitForResyncToFinish(exchange, repository);

            exchange.Mock.Verify(e => e.GetOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}
