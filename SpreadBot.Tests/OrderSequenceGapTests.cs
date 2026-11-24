using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models;
using System;
using System.Diagnostics;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class OrderSequenceGapTests
    {
        private static BittrexApiOrderData OrderMessage(int sequence, string id) => new BittrexApiOrderData
        {
            Sequence = sequence,
            Delta = new BittrexApiOrderData.Order { Id = id, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, MarketSymbol = "NMR-BTC" }
        };

        private static bool WaitUntil(Func<bool> condition)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds > 2000)
                    return false;
                Thread.Sleep(10);
            }
            return true;
        }

        [TestMethod]
        public void SequenceGap_ResyncsImmediately_AndAcceptsLaterMessages()
        {
            var exchange = new MockExchange();
            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();
            exchange.Mock.Invocations.Clear();

            exchange.OnOrder(OrderMessage(1, "order-1"));
            Assert.IsTrue(WaitUntil(() => dataRepository.OrdersData.ContainsKey("order-1")), "first message was not consumed");

            exchange.OnOrder(OrderMessage(3, "order-3"));
            Assert.IsTrue(WaitUntil(() => exchange.Mock.Invocations.Count > 0), "sequence gap did not trigger a resync");
            exchange.Mock.Verify(e => e.GetBalanceData(), Times.Once);

            exchange.OnOrder(OrderMessage(4, "order-4"));
            Assert.IsTrue(WaitUntil(() => dataRepository.OrdersData.ContainsKey("order-4")), "message after the resync was dropped");
            Assert.IsFalse(dataRepository.OrdersData.ContainsKey("order-3"));
        }

        [TestMethod]
        public void StaleSequence_IsSkippedWithoutResync()
        {
            var exchange = new MockExchange();
            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();
            exchange.Mock.Invocations.Clear();

            exchange.OnOrder(OrderMessage(5, "order-5"));
            Assert.IsTrue(WaitUntil(() => dataRepository.OrdersData.ContainsKey("order-5")), "first message was not consumed");

            exchange.OnOrder(OrderMessage(5, "duplicate"));
            exchange.OnOrder(OrderMessage(6, "order-6"));
            Assert.IsTrue(WaitUntil(() => dataRepository.OrdersData.ContainsKey("order-6")), "next message was not consumed");

            Assert.IsFalse(dataRepository.OrdersData.ContainsKey("duplicate"));
            Assert.AreEqual(0, exchange.Mock.Invocations.Count);
        }
    }
}
