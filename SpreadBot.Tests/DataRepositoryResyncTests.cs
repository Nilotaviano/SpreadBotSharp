using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
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

        [TestMethod]
        public void StreamInterruption_FetchesTrackedOrdersNoLongerOpen()
        {
            var exchange = new MockExchange();
            var openOrder = new OrderData { Id = "1", ClientOrderId = "1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN };
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData> { openOrder });

            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var received = new List<OrderData>();
            repository.SubscribeToOrderData("1", Guid.NewGuid(), received.Add);
            Assert.AreEqual(OrderStatus.OPEN, received.Single().Status, "the cached open order is replayed on subscribe");

            // While the stream was down the order filled: it's no longer open on the exchange
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData>());
            exchange.Mock.Setup(e => e.GetOrder("1", Symbol)).ReturnsAsync(new OrderData { Id = "1", ClientOrderId = "1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, FillQuantity = 5 });

            exchange.OnStreamInterrupted();

            WaitUntil(() => received.Count == 2, "the resync delivers the order's final state");
            Assert.AreEqual(OrderStatus.CLOSED, received[1].Status);
            Assert.AreEqual(5m, received[1].FillQuantity);
        }

        [TestMethod]
        public void Resync_DoesNotFetchUntrackedOrStillOpenOrders()
        {
            var exchange = new MockExchange();
            var tracked = new OrderData { Id = "1", ClientOrderId = "1", MarketSymbol = Symbol, Status = OrderStatus.OPEN };
            var untracked = new OrderData { Id = "2", ClientOrderId = "2", MarketSymbol = Symbol, Status = OrderStatus.OPEN };
            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData> { tracked, untracked });

            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();
            repository.SubscribeToOrderData("1", Guid.NewGuid(), _ => { });

            exchange.Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData> { tracked });
            exchange.Mock.Invocations.Clear();
            exchange.OnStreamInterrupted();

            WaitUntil(() => exchange.Mock.Invocations.Any(i => i.Method.Name == nameof(exchange.Object.GetOpenOrders)), "the resync runs");
            Thread.Sleep(50);
            exchange.Mock.Verify(e => e.GetOrder(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }
    }
}
