using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;

namespace SpreadBot.Tests
{
    /// <summary>
    /// IExchange mock with empty REST snapshots that captures the websocket callbacks so tests can push messages.
    /// </summary>
    internal class MockExchange
    {
        public Mock<IExchange> Mock { get; } = new Mock<IExchange>();

        public Action<BittrexApiBalanceData> OnBalance { get; private set; }
        public Action<BittrexApiMarketSummariesData> OnSummaries { get; private set; }
        public Action<BittrexApiTickersData> OnTickers { get; private set; }
        public Action<BittrexApiOrderData> OnOrder { get; private set; }

        public MockExchange(params BalanceData[] balances)
        {
            Mock.Setup(e => e.IsSetup).Returns(true);
            Mock.Setup(e => e.FeeRate).Returns(0);
            Mock.Setup(e => e.GetBalanceData()).ReturnsAsync(() => new CompleteBalanceData(1, balances));
            Mock.Setup(e => e.GetMarketSummariesData()).ReturnsAsync(new BittrexApiMarketSummariesData { Deltas = new BittrexApiMarketSummariesData.MarketSummary[0] });
            Mock.Setup(e => e.GetTickersData()).ReturnsAsync(new BittrexApiTickersData { Deltas = new BittrexApiTickersData.Ticker[0] });
            Mock.Setup(e => e.GetClosedOrdersData(It.IsAny<string>())).ReturnsAsync(new ApiRestResponse<BittrexApiOrderData.Order[]> { Data = new BittrexApiOrderData.Order[0] });
            Mock.Setup(e => e.GetOpenOrdersData()).ReturnsAsync(new ApiRestResponse<BittrexApiOrderData.Order[]> { Data = new BittrexApiOrderData.Order[0] });
            Mock.Setup(e => e.GetMarketsData()).ReturnsAsync(new BittrexApiMarketData[0]);

            Mock.Setup(e => e.OnBalance(It.IsAny<Action<BittrexApiBalanceData>>())).Callback((Action<BittrexApiBalanceData> a) => OnBalance = a);
            Mock.Setup(e => e.OnSummaries(It.IsAny<Action<BittrexApiMarketSummariesData>>())).Callback((Action<BittrexApiMarketSummariesData> a) => OnSummaries = a);
            Mock.Setup(e => e.OnTickers(It.IsAny<Action<BittrexApiTickersData>>())).Callback((Action<BittrexApiTickersData> a) => OnTickers = a);
            Mock.Setup(e => e.OnOrder(It.IsAny<Action<BittrexApiOrderData>>())).Callback((Action<BittrexApiOrderData> a) => OnOrder = a);
        }

        public IExchange Object => Mock.Object;
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
