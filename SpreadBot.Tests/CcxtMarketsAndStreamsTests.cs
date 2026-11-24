using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure.Exchanges.Ccxt;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CcxtMarketsAndStreamsTests
    {
        //CallBase: ccxt's constructor calls the virtual describe(); only the methods each test sets up are replaced
        private static Mock<ccxt.pro.binance> Exchange() => new Mock<ccxt.pro.binance>(new object[] { null }) { CallBase = true };

        private static Dictionary<string, object> Market(string symbol, bool active = true, double? tick = 0.000001, bool spot = true) => new Dictionary<string, object>
        {
            ["id"] = symbol.Replace("/", ""),
            ["symbol"] = symbol,
            ["base"] = symbol.Split('/')[0],
            ["quote"] = symbol.Split('/')[1],
            ["spot"] = spot,
            ["active"] = active,
            ["taker"] = 0.001,
            ["precision"] = new Dictionary<string, object> { ["price"] = tick, ["amount"] = 0.01 }
        };

        private static Task<IDictionary<string, object>> Markets(params Dictionary<string, object>[] markets) =>
            Task.FromResult<IDictionary<string, object>>(markets.ToDictionary(m => (string)m["symbol"], m => (object)m));

        /// <summary>
        /// Streams that never deliver, so Setup's watch loops stay idle; tests replace the ones they need.
        /// </summary>
        private static void WithIdleStreams(Mock<ccxt.pro.binance> exchange)
        {
            exchange.Setup(e => e.WatchTickers(It.IsAny<object>(), It.IsAny<object>())).Returns(new TaskCompletionSource<ccxt.Tickers>().Task);
            exchange.Setup(e => e.WatchBalance(It.IsAny<IDictionary<string, object>>())).Returns(new TaskCompletionSource<ccxt.Balances>().Task);
            exchange.Setup(e => e.WatchOrders(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>())).Returns(new TaskCompletionSource<List<ccxt.Order>>().Task);
        }

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
        public async Task GetMarkets_ReloadsAndReportsInactiveMarketsAsOffline()
        {
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC"), Market("ETH/BTC"), Market("NMR/USDT")));
            exchange.Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC", active: false), Market("ETH/BTC", tick: 0.00001)));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            var markets = (await adapter.GetMarkets()).ToDictionary(m => m.Symbol);

            exchange.Verify(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>()), Times.Once);
            Assert.AreEqual(2, markets.Count, "only the configured quote currencies");
            Assert.AreEqual(EMarketStatus.Offline, markets["NMR/BTC"].Status, "a halted market reaches the bots' data instead of vanishing");
            Assert.AreEqual(EMarketStatus.Online, markets["ETH/BTC"].Status);
            Assert.AreEqual(0.00001m, markets["ETH/BTC"].PriceTick, "the reloaded tick");
        }

        [TestMethod]
        public async Task GetMarkets_KeepsTheLoadedMarketsWhenTheReloadFails()
        {
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            exchange.Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>())).ThrowsAsync(new ccxt.RequestTimeout("binance timed out"));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            var markets = await adapter.GetMarkets();

            Assert.AreEqual("NMR/BTC", markets.Single().Symbol);
        }

        [TestMethod]
        public async Task GetMarkets_AFailedReload_LeavesCcxtsLastGoodLoadForItsOwnCalls()
        {
            // ccxt awaits marketsLoading before most requests; left faulted, every request would fail until a reload succeeds
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });
            var lastGood = Markets(Market("NMR/BTC"));
            exchange.Object.marketsLoading = lastGood;
            exchange.Setup(e => e.setMarkets(It.IsAny<object>(), It.IsAny<object>())).Returns((IDictionary<string, object>)null);
            exchange
                .Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>()))
                .Returns(() =>
                {
                    var failed = Task.FromException<IDictionary<string, object>>(new ccxt.RequestTimeout("binance timed out"));
                    exchange.Object.marketsLoading = failed;
                    return failed;
                });

            await adapter.GetMarkets();

            Assert.AreSame(lastGood, exchange.Object.marketsLoading);
        }

        [TestMethod]
        public async Task GetMarkets_AReloadListingNoMarkets_PutsCcxtsMarketsBack()
        {
            // ccxt already replaced its markets with the empty listing; its requests would fail with BadSymbol
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });
            var lastGood = Markets(Market("NMR/BTC"));
            exchange.Object.marketsLoading = lastGood;
            exchange.Setup(e => e.setMarkets(It.IsAny<object>(), It.IsAny<object>())).Returns((IDictionary<string, object>)null);
            exchange
                .Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>()))
                .Returns(() =>
                {
                    var empty = Markets();
                    exchange.Object.marketsLoading = empty;
                    return empty;
                });

            await adapter.GetMarkets();

            Assert.AreSame(lastGood, exchange.Object.marketsLoading);
            exchange.Verify(e => e.setMarkets(lastGood.Result, It.IsAny<object>()), Times.Once);
        }

        [TestMethod]
        public async Task GetMarkets_WithinTheReloadInterval_DoesNotReload()
        {
            // ccxt's requests await a reload in progress; reloading on every resync would stall them every minute
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(It.IsAny<bool?>(), It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.FromHours(1) };
            await adapter.Setup(new[] { "BTC" });

            await adapter.GetMarkets();
            await adapter.GetMarkets();

            exchange.Verify(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMarkets_ReportsAMarketTheReloadNoLongerListsAsOffline()
        {
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC"), Market("ETH/BTC")));
            exchange.Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("ETH/BTC")));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            var markets = (await adapter.GetMarkets()).ToDictionary(m => m.Symbol);

            Assert.AreEqual(EMarketStatus.Offline, markets["NMR/BTC"].Status);
            Assert.AreEqual(EMarketStatus.Online, markets["ETH/BTC"].Status);
        }

        [TestMethod]
        public async Task GetMarkets_AReloadListingNoMarketsKeepsTheOnesLoadedBefore()
        {
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.setMarkets(It.IsAny<object>(), It.IsAny<object>())).Returns((IDictionary<string, object>)null);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            exchange.Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>())).Returns(Markets());
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            var market = (await adapter.GetMarkets()).Single();

            Assert.AreEqual("NMR/BTC", market.Symbol);
            Assert.AreEqual(EMarketStatus.Online, market.Status);
        }

        [TestMethod]
        public async Task GetTickers_AfterAReload_LeavesOutInactiveMarkets()
        {
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(false, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC"), Market("ETH/BTC")));
            exchange.Setup(e => e.loadMarkets(true, It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC", active: false), Market("ETH/BTC")));
            IList<object> requested = null;
            exchange
                .Setup(e => e.FetchTickers(It.IsAny<IList<object>>(), It.IsAny<IDictionary<string, object>>()))
                .Callback((IList<object> symbols, IDictionary<string, object> _) => requested = symbols)
                .ReturnsAsync(new ccxt.Tickers { tickers = new Dictionary<string, ccxt.Ticker>() });
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            await adapter.GetMarkets();
            await adapter.GetTickers();

            CollectionAssert.AreEqual(new object[] { "ETH/BTC" }, requested.ToArray());
        }

        [TestMethod]
        public async Task PriceTick_InDecimalPlacesMode_IsOneUnitOfTheLastPlace()
        {
            var exchange = Exchange();
            exchange.Object.precisionMode = 2;
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(It.IsAny<bool?>(), It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC", tick: 8)));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            Assert.AreEqual(0.00000001m, (await adapter.GetMarkets()).Single().PriceTick);
        }

        [TestMethod]
        [DataRow(0.0)]
        [DataRow(double.NaN)]
        public async Task MarketWithoutAUsableTick_HasNone(double tick)
        {
            // A zero would overwrite a good tick when merged, and the strategies can't move prices by it
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(It.IsAny<bool?>(), It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC", tick: tick)));
            using var adapter = new CcxtExchange(exchange.Object) { MarketsReloadInterval = TimeSpan.Zero };
            await adapter.Setup(new[] { "BTC" });

            var market = (await adapter.GetMarkets()).Single();

            Assert.IsNull(market.PriceTick);
        }

        [TestMethod]
        public async Task StreamRecovering_ReportsTheInterruptionWithoutWaitingForItsNextUpdate()
        {
            // The order stream may not deliver again for a long time; orders that filled while it was down must be resynced now
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(It.IsAny<bool?>(), It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            exchange
                .SetupSequence(e => e.WatchOrders(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.NetworkError("binance connection closed"))
                .Returns(new TaskCompletionSource<List<ccxt.Order>>().Task);
            using var adapter = new CcxtExchange(exchange.Object) { StreamRetryDelay = TimeSpan.FromMilliseconds(10), ResubscribeGrace = TimeSpan.FromMilliseconds(50) };
            var interruptions = 0;
            adapter.OnStreamInterrupted(() => Interlocked.Increment(ref interruptions));

            await adapter.Setup(new[] { "BTC" });

            WaitUntil(() => Volatile.Read(ref interruptions) == 1, "the recovered order stream reports the interruption");
        }

        [TestMethod]
        public async Task StreamRecoveredOnlyByTheGrace_ReportsAgainOnItsFirstUpdate()
        {
            // A pending watch isn't proof the subscription is live; its first update is, and updates before it may have been missed
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(It.IsAny<bool?>(), It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            var firstUpdate = new TaskCompletionSource<List<ccxt.Order>>();
            exchange
                .SetupSequence(e => e.WatchOrders(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.NetworkError("binance connection closed"))
                .Returns(firstUpdate.Task)
                .Returns(new TaskCompletionSource<List<ccxt.Order>>().Task);
            using var adapter = new CcxtExchange(exchange.Object) { StreamRetryDelay = TimeSpan.FromMilliseconds(10), ResubscribeGrace = TimeSpan.FromMilliseconds(50) };
            var interruptions = 0;
            adapter.OnStreamInterrupted(() => Interlocked.Increment(ref interruptions));

            await adapter.Setup(new[] { "BTC" });
            WaitUntil(() => Volatile.Read(ref interruptions) == 1, "the grace reports the recovery");

            firstUpdate.SetResult(new List<ccxt.Order>());

            WaitUntil(() => Volatile.Read(ref interruptions) == 2, "the first update reports it again");
        }

        [TestMethod]
        public async Task StreamFailingAgainWithinTheGrace_DoesNotReportARecovery()
        {
            var exchange = Exchange();
            WithIdleStreams(exchange);
            exchange.Setup(e => e.loadMarkets(It.IsAny<bool?>(), It.IsAny<IDictionary<string, object>>())).Returns(Markets(Market("NMR/BTC")));
            exchange
                .Setup(e => e.WatchOrders(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<IDictionary<string, object>>()))
                .ThrowsAsync(new ccxt.NetworkError("binance connection closed"));
            using var adapter = new CcxtExchange(exchange.Object) { StreamRetryDelay = TimeSpan.FromMilliseconds(10), ResubscribeGrace = TimeSpan.FromMilliseconds(50) };
            var interruptions = 0;
            adapter.OnStreamInterrupted(() => Interlocked.Increment(ref interruptions));

            await adapter.Setup(new[] { "BTC" });
            WaitUntil(() => exchange.Invocations.Count(i => i.Method.Name == nameof(ccxt.Exchange.WatchOrders)) >= 5, "the stream retries a few times");

            Assert.AreEqual(0, Volatile.Read(ref interruptions));
        }
    }
}
