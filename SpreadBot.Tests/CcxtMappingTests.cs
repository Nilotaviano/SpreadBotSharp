using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure.Exchanges.Ccxt;
using SpreadBot.Models;
using System.Collections.Generic;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CcxtMappingTests
    {
        [TestMethod]
        public void ToOrderData_SplitsFeesByCurrencyAndMapsFields()
        {
            var order = new ccxt.Order
            {
                id = "123",
                clientOrderId = "client-123",
                symbol = "NMR/BTC",
                side = "buy",
                type = "limit",
                status = "closed",
                amount = 10,
                price = 0.0001,
                filled = 10,
                cost = 0.001,
                fees = new List<ccxt.Fee>
                {
                    new ccxt.Fee { currency = "NMR", cost = 0.01 },
                    new ccxt.Fee { currency = "BTC", cost = 0.000001 }
                }
            };

            var data = CcxtExchange.ToOrderData(order);

            Assert.AreEqual("123", data.Id);
            Assert.AreEqual("client-123", data.ClientOrderId);
            Assert.AreEqual("NMR/BTC", data.MarketSymbol);
            Assert.AreEqual(OrderDirection.BUY, data.Direction);
            Assert.AreEqual(OrderType.LIMIT, data.Type);
            Assert.AreEqual(OrderStatus.CLOSED, data.Status);
            Assert.AreEqual(10m, data.Quantity);
            Assert.AreEqual(0.0001m, data.Limit);
            Assert.AreEqual(10m, data.FillQuantity);
            Assert.AreEqual(0.001m, data.Proceeds);
            Assert.AreEqual(0.01m, data.CoinCommission);
            Assert.AreEqual(0.000001m, data.Commission);
        }

        [TestMethod]
        [DataRow("open", OrderStatus.OPEN)]
        [DataRow("closed", OrderStatus.CLOSED)]
        [DataRow("canceled", OrderStatus.CLOSED)]
        [DataRow("expired", OrderStatus.CLOSED)]
        [DataRow("rejected", OrderStatus.CLOSED)]
        public void ToOrderData_TreatsEveryFinalStatusAsClosed(string status, OrderStatus expected)
        {
            var data = CcxtExchange.ToOrderData(new ccxt.Order { id = "1", symbol = "NMR/BTC", side = "sell", status = status });

            Assert.AreEqual(expected, data.Status);
            Assert.AreEqual("1", data.ClientOrderId, "falls back to the exchange id");
        }

        [TestMethod]
        public void ToMarketData_MapsTickerFields()
        {
            var data = CcxtExchange.ToMarketData(new ccxt.Ticker { symbol = "NMR/BTC", bid = 0.00009999, ask = 0.00010101, last = 0.0001, quoteVolume = 150, percentage = 5 });

            Assert.AreEqual("NMR/BTC", data.Symbol);
            Assert.AreEqual(0.00009999m, data.BidRate);
            Assert.AreEqual(0.00010101m, data.AskRate);
            Assert.AreEqual(0.0001m, data.LastTradeRate);
            Assert.AreEqual(150m, data.QuoteVolume);
            Assert.AreEqual(5m, data.PercentChange);
            Assert.AreEqual("BTC", data.BaseMarket);
            Assert.AreEqual("NMR", data.Target);
        }

        [TestMethod]
        public void ToApiException_MapsCcxtErrorsToApiErrorTypes()
        {
            Assert.AreEqual(ApiErrorType.InsufficientFunds, CcxtExchange.ToApiException(new ccxt.InsufficientFunds("binance Account has insufficient balance")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.OrderNotOpen, CcxtExchange.ToApiException(new ccxt.OrderNotFound("binance Unknown order sent.")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.RetryLater, CcxtExchange.ToApiException(new ccxt.OrderImmediatelyFillable("binance Order would immediately match and take.")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.DustTrade, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance Filter failure: NOTIONAL")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.UnknownError, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance Price not in range")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.Throttled, CcxtExchange.ToApiException(new ccxt.RateLimitExceeded("binance Too many requests")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.RetryLater, CcxtExchange.ToApiException(new ccxt.RequestTimeout("binance timed out")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.Unauthorized, CcxtExchange.ToApiException(new ccxt.AuthenticationError("binance Invalid API-key")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.Unauthorized, CcxtExchange.ToApiException(new ccxt.PermissionDenied("binance not permitted")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.MarketOffline, CcxtExchange.ToApiException(new ccxt.PermissionDenied("binance {\"code\":-2010,\"msg\":\"This symbol is not permitted for this account.\"}")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.MarketOffline, CcxtExchange.ToApiException(new ccxt.PermissionDenied("binance {\"code\":-2010,\"msg\":\"This symbol is restricted for this account.\"}")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.MarketOffline, CcxtExchange.ToApiException(new ccxt.BadSymbol("binance does not have market symbol")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.MarketOffline, CcxtExchange.ToApiException(new ccxt.MarketClosed("binance Market is closed.")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.RetryLater, CcxtExchange.ToApiException(new ccxt.OperationRejected("binance Server is busy, please wait and try again")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.Unauthorized, CcxtExchange.ToApiException(new ccxt.RestrictedLocation("binance Service unavailable from a restricted location")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.Unauthorized, CcxtExchange.ToApiException(new ccxt.ManualInteractionNeeded("binance Please confirm the agreement first")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.UnknownError, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance stop price is zero or less")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.DustTrade, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance amount of NMR/BTC must be greater than minimum amount precision of 0.01")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.DustTrade, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance order cost = amount * price is zero or less {\"code\":-1013,\"msg\":\"Price * QTY is zero or less.\"}")).ApiErrorType);
            Assert.AreEqual(ApiErrorType.UnknownError, CcxtExchange.ToApiException(new ccxt.InvalidOrder("binance price of NMR/BTC must be greater than minimum price precision of 0.000001")).ApiErrorType);
        }
    }
}
