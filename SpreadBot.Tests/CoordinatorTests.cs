using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Logic;
using System.Linq;

namespace SpreadBot.Tests
{
    [TestClass]
    public class CoordinatorTests
    {
        [TestMethod]
        public void MarketComparer_RanksByVolumeTimesSpreadDescending()
        {
            var markets = new (decimal volume, decimal spread)[]
            {
                (100m, 1m),   // 100
                (10m, 50m),   // 500
                (50m, 4m),    // 200
            };

            var ordered = markets.OrderBy(m => m, Coordinator.GetMarketComparer()).ToArray();

            CollectionAssert.AreEqual(new[] { (10m, 50m), (50m, 4m), (100m, 1m) }, ordered);
        }
    }
}
