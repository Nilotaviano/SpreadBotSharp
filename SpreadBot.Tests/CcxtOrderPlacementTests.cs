using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure.Exchanges.Ccxt;
using SpreadBot.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CcxtOrderPlacementTests
    {
        private const string Symbol = "NMR/BTC";

        //CallBase: ccxt's constructor calls the virtual describe(); only the methods each test sets up are replaced
        private static Mock<ccxt.pro.binance> Exchange() => new Mock<ccxt.pro.binance>(new object[] { null }) { CallBase = true };

        private static ccxt.Order Order(string status, string clientOrderId = "client") =>
            new ccxt.Order { id = "42", clientOrderId = clientOrderId, symbol = Symbol, side = "buy", type = "limit", status = status, amount = 10, price = 0.0001 };

        [TestMethod]
        public async Task BuyLimit_SendsPostOnlyWithAClientOrderId()
        {
            var exchange = Exchange();
            IDictionary<string, object> sent = null;
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", 10, 0.0001, It.IsAny<IDictionary<string, object>>()))
                .Callback((string _, string _, string _, double _, double? _, IDictionary<string, object> parameters) => sent = parameters)
                .ReturnsAsync(Order("open"));

            var order = await new CcxtExchange(exchange.Object, TimeSpan.Zero).BuyLimit(Symbol, 10, 0.0001m);

            Assert.AreEqual(OrderStatus.OPEN, order.Status);
            Assert.AreEqual(true, sent["postOnly"]);
            StringAssert.StartsWith((string)sent["clientOrderId"], "sb");
            Assert.IsTrue(((string)sent["clientOrderId"]).Length <= 36);
        }

        [TestMethod]
        public async Task CreateFailingWithoutAnAnswer_ReturnsTheOrderIfTheExchangeAcceptedIt()
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.RequestTimeout("binance timed out"));
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.Is<IDictionary<string, object>>(p => p.ContainsKey("clientOrderId"))))
                .ReturnsAsync(Order("open"));

            var order = await new CcxtExchange(exchange.Object, TimeSpan.Zero).BuyLimit(Symbol, 10, 0.0001m);

            Assert.AreEqual("42", order.Id);
            Assert.AreEqual(OrderStatus.OPEN, order.Status);
        }

        [TestMethod]
        public async Task CreateFailingWithoutAnAnswer_ReportsRetryLaterIfTheOrderDoesNotExist()
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.RequestTimeout("binance timed out"));
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.OrderNotFound("binance Order does not exist."));

            var error = await Assert.ThrowsExactlyAsync<ApiException>(() => new CcxtExchange(exchange.Object, TimeSpan.Zero).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(ApiErrorType.RetryLater, error.ApiErrorType);
        }

        [TestMethod]
        public async Task CreateFailingWithoutAnAnswer_RetriesALookupThatDoesNotFindTheOrderYet()
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.RequestTimeout("binance timed out"));
            exchange
                .SetupSequence(e => e.FetchOrder(null, Symbol, It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.OrderNotFound("binance Order does not exist."))
                .ReturnsAsync(Order("open"));

            var order = await new CcxtExchange(exchange.Object, TimeSpan.Zero).BuyLimit(Symbol, 10, 0.0001m);

            Assert.AreEqual("42", order.Id);
        }

        [TestMethod]
        public async Task CreateResponseWithoutStatus_IsLookedUpForTheFullOrder()
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(Order(null));
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.Is<IDictionary<string, object>>(p => p.ContainsKey("clientOrderId"))))
                .ReturnsAsync(Order("closed"));

            var order = await new CcxtExchange(exchange.Object, TimeSpan.Zero).BuyLimit(Symbol, 10, 0.0001m);

            Assert.AreEqual(OrderStatus.CLOSED, order.Status);
        }

        [TestMethod]
        public async Task CreateResponseWithoutStatus_IsTrackedAsOpenWhenTheLookupFails()
        {
            // The exchange acknowledged the order, so reporting a failure would make the bot place it again
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(Order(null, clientOrderId: null));
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.RequestTimeout("binance timed out"));

            var order = await new CcxtExchange(exchange.Object, TimeSpan.Zero).BuyLimit(Symbol, 10, 0.0001m);

            Assert.AreEqual(OrderStatus.OPEN, order.Status);
            Assert.AreEqual("42", order.Id);
            StringAssert.StartsWith(order.ClientOrderId, "sb");
            Assert.AreEqual(Symbol, order.MarketSymbol);
        }

        [TestMethod]
        public void ToBalanceData_SkipsPartialEntries()
        {
            var balances = new ccxt.Balances
            {
                balances = new Dictionary<string, ccxt.Balance>
                {
                    ["BTC"] = new ccxt.Balance { free = 1, used = 0.5, total = 1.5 },
                    ["NMR"] = new ccxt.Balance { free = null, total = 3 }
                }
            };

            var mapped = new List<SpreadBot.Models.Repository.BalanceData>(CcxtExchange.ToBalanceData(balances));

            Assert.AreEqual(1, mapped.Count);
            Assert.AreEqual("BTC", mapped[0].CurrencyAbbreviation);
            Assert.AreEqual(1m, mapped[0].Amount);
            Assert.AreEqual(1.5m, mapped[0].Total);
        }

        [TestMethod]
        public void ToApiException_RecognizesBinancesNewerNotionalWording()
        {
            Assert.AreEqual(ApiErrorType.DustTrade, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance Order's notional must be no smaller than 5.0")).ApiErrorType);
        }
    }
}
