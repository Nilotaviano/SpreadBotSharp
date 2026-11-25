using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Models.Repository;

namespace SpreadBot.Tests
{
    [TestClass]
    public class MarketFiltersTests
    {
        private static SpreadConfiguration Configuration() => new SpreadConfiguration
        {
            BaseMarket = "BTC",
            AllocatedAmountOfBaseCurrency = 0.003m,
            MinimumNegotiatedAmount = 0.001m,
            MinimumSpreadPercentage = 1,
            MinimumQuoteVolume = 150,
            MaxPercentChangeFromPreviousDay = 40,
            MinimumPrice = 0.000008m
        };

        private static MarketData Market(string symbol = "NMR/BTC", decimal ask = 0.00010101m, decimal volume = 200, decimal change = 5, EMarketStatus status = EMarketStatus.Online, decimal? tick = MockExchange.Tick) =>
            new MarketData { Symbol = symbol, AskRate = ask, BidRate = 0.00009999m, LastTradeRate = 0.0001m, QuoteVolume = volume, PercentChange = change, Status = status, PriceTick = tick };

        private static MarketFilters Filters(params string[] blacklist) =>
            new MarketFilters(new AppSettings { BlacklistedMarkets = blacklist });

        [TestMethod]
        public void Rejection_NamesTheFirstFilterAMarketFails()
        {
            var filters = Filters();

            Assert.IsNull(filters.Rejection(Market(), Configuration()));
            Assert.AreEqual("offline", filters.Rejection(Market(status: EMarketStatus.Offline), Configuration()));
            Assert.AreEqual("no price tick", filters.Rejection(Market(tick: null), Configuration()));
            StringAssert.StartsWith(filters.Rejection(Market(ask: 0.0001001m), Configuration()), "spread ");
            StringAssert.StartsWith(filters.Rejection(Market(volume: 10), Configuration()), "volume ");
            StringAssert.StartsWith(filters.Rejection(Market(change: 80), Configuration()), "up 80");
        }

        [TestMethod]
        public void OneSidedQuote_IsRejected_EvenWhenAnySpreadWillDo()
        {
            var configuration = Configuration();
            configuration.MinimumSpreadPercentage = 0;
            var market = Market();
            market.AskRate = null;

            Assert.AreEqual("no bid or no ask", Filters().Rejection(market, configuration));
        }

        [TestMethod]
        public void BuyingRejection_JudgesEverythingButTheQuote_UntilTheMarketShowsOne()
        {
            var filters = Filters();
            var unquoted = Market();
            unquoted.BidRate = null;
            unquoted.AskRate = null;
            var pumping = Market(change: 80);
            pumping.BidRate = null;
            pumping.AskRate = null;
            var oneSided = Market();
            oneSided.AskRate = null;

            Assert.IsNull(filters.BuyingRejection(unquoted, Configuration()), "waits for a quote");
            Assert.AreEqual("no bid or no ask", filters.Rejection(unquoted, Configuration()), "allocation never takes it");
            StringAssert.StartsWith(filters.BuyingRejection(pumping, Configuration()), "up 80");
            Assert.AreEqual("no bid or no ask", filters.BuyingRejection(oneSided, Configuration()));
            StringAssert.StartsWith(filters.BuyingRejection(Market(ask: 0.0001001m), Configuration()), "spread ");
        }

        [TestMethod]
        public void BlacklistedCoin_RejectsEveryMarketTradingIt()
        {
            var filters = Filters("nmr");

            Assert.AreEqual("blacklisted", filters.Rejection(Market("NMR/BTC"), Configuration()));
            Assert.AreEqual("blacklisted", filters.Rejection(Market("NMR/ETH"), Configuration()));
            Assert.IsNull(filters.Rejection(Market("XYZ/BTC"), Configuration()));
        }

        [TestMethod]
        public void BlacklistedBaseMarket_DoesNotRejectTheMarketsQuotedInIt()
        {
            // BTC names the coin of BTC/USDT, not every market priced in BTC
            var filters = Filters("BTC");

            Assert.IsNull(filters.Rejection(Market("NMR/BTC"), Configuration()));
            Assert.AreEqual("blacklisted", filters.ViabilityRejection(Market("BTC/USDT")));
        }

        [TestMethod]
        public void BlacklistedMarket_RejectsOnlyThatMarket()
        {
            var filters = Filters("nmr/btc");

            Assert.AreEqual("blacklisted", filters.Rejection(Market("NMR/BTC"), Configuration()));
            Assert.IsNull(filters.ViabilityRejection(Market("NMR/ETH")));
        }

        [TestMethod]
        public void Blacklist_FollowsASettingsReload()
        {
            var settings = new AppSettings { SpreadConfigurations = new[] { Configuration() } };
            var filters = new MarketFilters(settings);
            Assert.IsNull(filters.ViabilityRejection(Market()));

            settings.Reload(new AppSettings { BlacklistedMarkets = new[] { "NMR" }, SpreadConfigurations = settings.SpreadConfigurations });
            Assert.AreEqual("blacklisted", filters.ViabilityRejection(Market()));

            settings.Reload(new AppSettings { SpreadConfigurations = settings.SpreadConfigurations });
            Assert.IsNull(filters.ViabilityRejection(Market()), "a reload without the setting clears it");
        }
    }
}
