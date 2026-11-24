using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure.Exchanges.Ccxt;
using SpreadBot.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CcxtOrderPlacementTests
    {
        private const string Symbol = "NMR/BTC";

        //CallBase: ccxt's constructor calls the virtual describe(); only the methods each test sets up are replaced
        private static Mock<ccxt.pro.binance> Exchange() => new Mock<ccxt.pro.binance>(new object[] { null }) { CallBase = true };

        private static ccxt.BaseError CcxtError(string name) =>
            (ccxt.BaseError)Activator.CreateInstance(typeof(ccxt.BaseError).Assembly.GetType($"ccxt.{name}", throwOnError: true), $"binance {name}");

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

            var order = await new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m);

            Assert.AreEqual(OrderStatus.OPEN, order.Status);
            Assert.AreEqual(true, sent["postOnly"]);
            StringAssert.StartsWith((string)sent["clientOrderId"], "sb");
            Assert.IsTrue(((string)sent["clientOrderId"]).Length <= 36);
        }

        [TestMethod]
        public async Task CreateFailingWithoutAnAnswer_ReportsTheOrderAsSentWithAnUnknownState()
        {
            // The exchange may have accepted it before the answer was lost; failing would make the bot place it again
            var exchange = Exchange();
            IDictionary<string, object> sent = null;
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", 10, 0.0001, It.IsAny<IDictionary<string, object>>()))
                .Callback((string _, string _, string _, double _, double? _, IDictionary<string, object> parameters) => sent = parameters)
                .ThrowsAsync(new ccxt.RequestTimeout("binance timed out"));

            var before = DateTime.UtcNow;
            var error = await Assert.ThrowsExactlyAsync<OrderStateUnknownException>(() => new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(ApiErrorType.OrderStateUnknown, error.ApiErrorType);
            Assert.AreEqual(OrderStatus.UNKNOWN, error.Order.Status);
            Assert.AreEqual((string)sent["clientOrderId"], error.Order.ClientOrderId);
            Assert.IsNull(error.Order.Id);
            Assert.AreEqual(Symbol, error.Order.MarketSymbol);
            Assert.AreEqual(OrderDirection.BUY, error.Order.Direction);
            Assert.AreEqual(10m, error.Order.Quantity);
            Assert.AreEqual(0.0001m, error.Order.Limit);
            Assert.IsTrue(error.Order.CreatedAt >= before);
            exchange.Verify(e => e.FetchOrder(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IDictionary<string, object>>()), Times.Never);
        }

        [TestMethod]
        [DataRow(nameof(ccxt.ExchangeNotAvailable))]
        [DataRow(nameof(ccxt.BadResponse))]
        [DataRow(nameof(ccxt.OperationFailed))] // Binance -1000/-1001/-1006: "execution status unknown"
        public async Task CreateFailingWithAnUnclearOutcome_ReportsAnUnknownState(string errorName)
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(CcxtError(errorName));

            var error = await Assert.ThrowsExactlyAsync<OrderStateUnknownException>(() => new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(OrderStatus.UNKNOWN, error.Order.Status);
        }

        [TestMethod]
        [DataRow(typeof(TaskCanceledException))] // HttpClient's timeout, which ccxt doesn't wrap
        [DataRow(typeof(IOException))]
        [DataRow(typeof(InvalidCastException))] // a response ccxt fails to parse
        public async Task CreateFailingOutsideCcxtsErrors_ReportsAnUnknownState(Type exceptionType)
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType));

            var error = await Assert.ThrowsExactlyAsync<OrderStateUnknownException>(() => new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(OrderStatus.UNKNOWN, error.Order.Status);
        }

        [TestMethod]
        [DataRow(typeof(TaskCanceledException))]
        [DataRow(typeof(IOException))]
        public async Task RequestFailingInTransportOutsideCcxtsErrors_IsRetriedLater(Type exceptionType)
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType));

            var error = await Assert.ThrowsExactlyAsync<ApiException>(() => new CcxtExchange(exchange.Object).GetOrderByClientId("client", Symbol));

            Assert.AreEqual(ApiErrorType.RetryLater, error.ApiErrorType);
        }

        [TestMethod]
        public void Requests_TimeOutAfterCcxtsTimeoutOption()
        {
            var ccxtExchange = Exchange().Object;
            ccxtExchange.timeout = 2500;

            _ = new CcxtExchange(ccxtExchange);

            Assert.AreEqual(TimeSpan.FromMilliseconds(2500), ccxtExchange.httpClient.Timeout);
        }

        [TestMethod]
        [DataRow(nameof(ccxt.RateLimitExceeded), ApiErrorType.Throttled)]
        [DataRow(nameof(ccxt.DDoSProtection), ApiErrorType.Throttled)]
        [DataRow(nameof(ccxt.InvalidNonce), ApiErrorType.RetryLater)]
        public async Task CreateRefusedBeforeReachingTheOrderBook_IsAPlainFailure(string errorName, ApiErrorType expected)
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(CcxtError(errorName));

            var error = await Assert.ThrowsExactlyAsync<ApiException>(() => new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(expected, error.ApiErrorType);
        }

        [TestMethod]
        [DataRow(null, "42")]
        [DataRow("open", null)]
        public async Task AcknowledgementWithoutStatusOrId_ReportsAnUnknownState(string status, string id)
        {
            var acknowledgement = Order(status);
            acknowledgement.id = id;
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(acknowledgement);

            var error = await Assert.ThrowsExactlyAsync<OrderStateUnknownException>(() => new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(OrderStatus.UNKNOWN, error.Order.Status);
            StringAssert.StartsWith(error.Order.ClientOrderId, "sb");
        }

        [TestMethod]
        public async Task AcknowledgementThatCannotBeMapped_ReportsAnUnknownState()
        {
            var acknowledgement = Order("open");
            acknowledgement.timestamp = long.MaxValue; // out of DateTimeOffset's range
            var exchange = Exchange();
            exchange
                .Setup(e => e.CreateOrder(Symbol, "limit", "buy", It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(acknowledgement);

            var error = await Assert.ThrowsExactlyAsync<OrderStateUnknownException>(() => new CcxtExchange(exchange.Object).BuyLimit(Symbol, 10, 0.0001m));

            Assert.AreEqual(OrderStatus.UNKNOWN, error.Order.Status);
        }

        [TestMethod]
        public async Task Setup_RejectsAnExchangeWhosePricePrecisionIsSignificantDigits()
        {
            var ccxtExchange = Exchange().Object;
            ccxtExchange.precisionMode = 3;

            var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(() => new CcxtExchange(ccxtExchange).Setup(new[] { "BTC" }));

            StringAssert.Contains(error.Message, "significant digits");
        }

        [TestMethod]
        public void OrderRequestLifetime_IsTheRecvWindowPlusAMargin()
        {
            // ccxt's Binance signs requests with a 10 s recvWindow
            Assert.AreEqual(TimeSpan.FromSeconds(60), new CcxtExchange(Exchange().Object).OrderRequestLifetime);
        }

        [TestMethod]
        public async Task GetOrderByClientId_FetchesByTheClientOrderId()
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.Is<IDictionary<string, object>>(p => (string)p["clientOrderId"] == "client")))
                .ReturnsAsync(Order("open"));

            var order = await new CcxtExchange(exchange.Object).GetOrderByClientId("client", Symbol);

            Assert.AreEqual("42", order.Id);
            Assert.AreEqual(OrderStatus.OPEN, order.Status);
        }

        private static ccxt.Order FilledWithoutFees(double filled) =>
            new ccxt.Order { id = "42", clientOrderId = "client", symbol = Symbol, side = "buy", type = "limit", status = "closed", amount = 10, price = 0.0001, filled = filled, cost = filled * 0.0001 };

        private static ccxt.Trade Trade(double amount, double fee, string feeCurrency) =>
            new ccxt.Trade { order = "42", symbol = Symbol, amount = amount, fee = new ccxt.Fee { cost = fee, currency = feeCurrency } };

        [TestMethod]
        public async Task GetOrder_ClosedWithFillsButNoFees_TakesTheFeesFromItsTrades()
        {
            // Binance's order endpoints carry no commission; booking the close without it would overstate the coins held
            var exchange = Exchange();
            exchange.Setup(e => e.FetchOrder("42", Symbol, It.IsAny<IDictionary<string, object>>())).ReturnsAsync(FilledWithoutFees(10));
            exchange
                .Setup(e => e.FetchOrderTrades("42", Symbol, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(new List<ccxt.Trade> { Trade(4, 0.004, "NMR"), Trade(6, 0.006, "NMR"), Trade(0, 0.0000001, "BTC") });

            var order = await new CcxtExchange(exchange.Object).GetOrder("42", Symbol);

            Assert.AreEqual(OrderStatus.CLOSED, order.Status);
            Assert.AreEqual(0.01m, order.CoinCommission);
            Assert.AreEqual(0.0000001m, order.Commission);
        }

        [TestMethod]
        public async Task GetOrderByClientId_CancelledWithPartialFills_TakesTheFeesFromItsTrades()
        {
            // The lookup of an order whose placement was unknown, and a cancel that raced fills, both close it from REST
            var answer = FilledWithoutFees(4);
            answer.status = "canceled";
            var exchange = Exchange();
            exchange.Setup(e => e.FetchOrder(null, Symbol, It.IsAny<IDictionary<string, object>>())).ReturnsAsync(answer);
            exchange
                .Setup(e => e.FetchOrderTrades("42", Symbol, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(new List<ccxt.Trade>
                {
                    new ccxt.Trade { order = "42", symbol = Symbol, amount = 4, fees = new List<ccxt.Fee> { new ccxt.Fee { cost = 0.004, currency = "NMR" }, new ccxt.Fee { cost = 0.0000002, currency = "BTC" } } }
                });

            var order = await new CcxtExchange(exchange.Object).GetOrderByClientId("client", Symbol);

            Assert.AreEqual(OrderStatus.CLOSED, order.Status);
            Assert.AreEqual(0.004m, order.CoinCommission);
            Assert.AreEqual(0.0000002m, order.Commission);
        }

        [TestMethod]
        public async Task GetOrder_FilledInMoreTradesThanOneLookupReturns_BooksTheFeesItShows()
        {
            // Retrying would never show more, so the close would never be booked
            var exchange = Exchange();
            exchange.Setup(e => e.FetchOrder("42", Symbol, It.IsAny<IDictionary<string, object>>())).ReturnsAsync(FilledWithoutFees(2000));
            exchange
                .Setup(e => e.FetchOrderTrades("42", Symbol, It.IsAny<long?>(), 1000, It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(Enumerable.Range(0, 1000).Select(_ => Trade(1, 0.001, "NMR")).ToList());

            var order = await new CcxtExchange(exchange.Object).GetOrder("42", Symbol);

            Assert.AreEqual(1m, order.CoinCommission);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        public async Task GetOrder_ClosedWithFillsTheTradesDoNotCoverYet_IsRetriedLater(int tradesSoFar)
        {
            var exchange = Exchange();
            exchange.Setup(e => e.FetchOrder("42", Symbol, It.IsAny<IDictionary<string, object>>())).ReturnsAsync(FilledWithoutFees(10));
            exchange
                .Setup(e => e.FetchOrderTrades("42", Symbol, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()))
                .ReturnsAsync(Enumerable.Range(0, tradesSoFar).Select(_ => Trade(4, 0.004, "NMR")).ToList());

            var error = await Assert.ThrowsExactlyAsync<ApiException>(() => new CcxtExchange(exchange.Object).GetOrder("42", Symbol));

            Assert.AreEqual(ApiErrorType.RetryLater, error.ApiErrorType);
        }

        [TestMethod]
        [DataRow("closed", 0.0, false)] // nothing filled, so no fees
        [DataRow("open", 4.0, false)] // the close is booked later
        [DataRow("closed", 10.0, true)] // the answer has them
        public async Task GetOrder_WithoutFeesToFind_DoesNotFetchTrades(string status, double filled, bool withFee)
        {
            var answer = FilledWithoutFees(filled);
            answer.status = status;
            if (withFee)
                answer.fee = new ccxt.Fee { cost = 0.01, currency = "NMR" };
            var exchange = Exchange();
            exchange.Setup(e => e.FetchOrder("42", Symbol, It.IsAny<IDictionary<string, object>>())).ReturnsAsync(answer);

            await new CcxtExchange(exchange.Object).GetOrder("42", Symbol);

            exchange.Verify(e => e.FetchOrderTrades(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()), Times.Never);
        }

        [TestMethod]
        public async Task GetOrderByClientId_ReportsAnOrderTheExchangeDoesNotKnowAsNotOpen()
        {
            var exchange = Exchange();
            exchange
                .Setup(e => e.FetchOrder(null, Symbol, It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.OrderNotFound("binance Order does not exist."));

            var error = await Assert.ThrowsExactlyAsync<ApiException>(() => new CcxtExchange(exchange.Object).GetOrderByClientId("client", Symbol));

            Assert.AreEqual(ApiErrorType.OrderNotOpen, error.ApiErrorType);
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
