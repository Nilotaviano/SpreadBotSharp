using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SpreadBot.Tests
{
    /// <summary>
    /// IExchange mock with empty snapshots that captures the stream callbacks so tests can push updates.
    /// </summary>
    internal class MockExchange
    {
        public const decimal Tick = 0.00000001m;

        public Mock<IExchange> Mock { get; } = new Mock<IExchange>();

        public Action<BalanceData> OnBalance { get; private set; }
        public Action<IEnumerable<MarketData>> OnMarketData { get; private set; }
        public Action<OrderData> OnOrder { get; private set; }
        public Action OnStreamInterrupted { get; private set; }
        public Action<ApiException> OnFatalError { get; private set; }

        public MockExchange(params BalanceData[] balances)
        {
            Mock.Setup(e => e.IsSetup).Returns(true);
            Mock.Setup(e => e.FeeRate).Returns(0);
            Mock.Setup(e => e.OrderRequestLifetime).Returns(TimeSpan.FromSeconds(60));
            Mock.Setup(e => e.GetBalances()).ReturnsAsync(() => balances.ToList());
            Mock.Setup(e => e.GetMarkets()).ReturnsAsync(new List<MarketData>());
            Mock.Setup(e => e.GetTickers()).ReturnsAsync(new List<MarketData>());
            Mock.Setup(e => e.GetOpenOrders()).ReturnsAsync(new List<OrderData>());

            Mock.Setup(e => e.OnBalance(It.IsAny<Action<BalanceData>>())).Callback((Action<BalanceData> a) => OnBalance = a);
            Mock.Setup(e => e.OnMarketData(It.IsAny<Action<IEnumerable<MarketData>>>())).Callback((Action<IEnumerable<MarketData>> a) => OnMarketData = a);
            Mock.Setup(e => e.OnOrder(It.IsAny<Action<OrderData>>())).Callback((Action<OrderData> a) => OnOrder = a);
            Mock.Setup(e => e.OnStreamInterrupted(It.IsAny<Action>())).Callback((Action a) => OnStreamInterrupted = a);
            Mock.Setup(e => e.OnFatalError(It.IsAny<Action<ApiException>>())).Callback((Action<ApiException> a) => OnFatalError = a);
        }

        public IExchange Object => Mock.Object;

        /// <summary>
        /// Snapshot markets, online and quotable at <see cref="Tick"/>, returned by GetMarkets.
        /// </summary>
        public void WithMarkets(params string[] symbols)
        {
            Mock.Setup(e => e.GetMarkets()).ReturnsAsync(symbols
                .Select(s => new MarketData { Symbol = s, Status = EMarketStatus.Online, PriceTick = Tick })
                .ToList());
        }

        public void WithTickers(params MarketData[] tickers)
        {
            Mock.Setup(e => e.GetTickers()).ReturnsAsync(tickers.ToList());
        }

        public static MarketData Ticker(string symbol, decimal ask, decimal bid, decimal? last = null, decimal? quoteVolume = null, decimal? percentChange = null) =>
            new MarketData { Symbol = symbol, AskRate = ask, BidRate = bid, LastTradeRate = last, QuoteVolume = quoteVolume, PercentChange = percentChange };

        public void PushTicker(MarketData ticker) => OnMarketData(new[] { ticker });
    }

    internal static class TestLedger
    {
        /// <summary>
        /// A ledger with no other running bots.
        /// </summary>
        public static BalanceLedger Empty(DataRepository dataRepository) =>
            new BalanceLedger(dataRepository, new Mock<ICoordinatorContext>().Object);
    }
}
