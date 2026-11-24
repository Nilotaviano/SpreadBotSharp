using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Logic;
using SpreadBot.Models.Repository;
using System.Linq;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CoordinatorTests
    {
        private static MarketData Market(string symbol, decimal quoteVolume, decimal bidRate) =>
            new MarketData { Symbol = symbol, QuoteVolume = quoteVolume, AskRate = 100m, BidRate = bidRate };

        [TestMethod]
        public void MarketOrdering_RanksByQuoteVolumeTimesSpreadDescending()
        {
            var markets = new[]
            {
                Market("HIGHVOLUME-BTC", 100m, 99m),  // 1% spread  -> 100
                Market("WIDESPREAD-BTC", 10m, 50m),   // 50% spread -> 500
                Market("BALANCED-BTC", 50m, 96m),     // 4% spread  -> 200
            };

            var ordered = markets
                .OrderBy(Coordinator.GetMarketOrderKey, Coordinator.GetMarketComparer())
                .Select(m => m.Symbol)
                .ToArray();

            CollectionAssert.AreEqual(new[] { "WIDESPREAD-BTC", "BALANCED-BTC", "HIGHVOLUME-BTC" }, ordered);
        }
    }
}
