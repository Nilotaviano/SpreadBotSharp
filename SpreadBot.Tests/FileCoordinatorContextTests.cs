using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class FileCoordinatorContextTests
    {
        private const string SessionFile = "coordinatorContext.json";

        // coordinatorContext.json is cwd-relative
        private static void InTemporaryDirectory(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var previousDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = directory;
                test(directory);
            }
            finally
            {
                Environment.CurrentDirectory = previousDirectory;
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestMethod]
        [DataRow("{ not json")]
        [DataRow(@"{ ""BotContexts"": [null] }")]
        [DataRow(@"{ ""BotContexts"": [{}] }")] // e.g. a renamed BotContext member: the constructor gets no configuration
        [DataRow(@"{ ""BotContexts"": [{ ""spreadConfiguration"": { ""BaseMarket"": ""BTC"" } }] }")]
        [DataRow(@"{ ""BotContexts"": [{ ""spreadConfiguration"": { ""BaseMarket"": ""BTC"" }, ""LatestMarketData"": { ""Symbol"": ""NMR-BTC"" }, ""CurrentOrderData"": { ""Id"": ""1"" } }] }")]
        public void SessionFileThatCannotBeRestored_IsMovedAsideInsteadOfOverwritten(string content)
        {
            InTemporaryDirectory(directory =>
            {
                File.WriteAllText(SessionFile, content);

                Assert.IsNull(new FileCoordinatorContext().GetPreviousSessionContext());

                Assert.IsFalse(File.Exists(SessionFile), "the next save must not overwrite the file");
                var preserved = Directory.GetFiles(directory, $"{SessionFile}.unreadable-*").Single();
                Assert.AreEqual(content, File.ReadAllText(preserved));
            });
        }

        [TestMethod]
        public void ValidSessionFile_IsRestoredAndLeftInPlace()
        {
            InTemporaryDirectory(directory =>
            {
                const string content = @"{ ""BotContexts"": [{ ""spreadConfiguration"": { ""BaseMarket"": ""BTC"" }, ""LatestMarketData"": { ""Symbol"": ""NMR/BTC"" } }], ""DustPerMarket"": {} }";
                File.WriteAllText(SessionFile, content);

                var session = new FileCoordinatorContext().GetPreviousSessionContext();

                Assert.AreEqual("NMR/BTC", session.BotContexts.Single().LatestMarketData.Symbol);
                Assert.AreEqual(content, File.ReadAllText(SessionFile));
            });
        }

        [TestMethod]
        public void Save_WaitsForABotToFinishItsChange()
        {
            InTemporaryDirectory(directory =>
            {
                var repository = new DataRepository(new MockExchange().Object, new AppSettings { ResyncIntervalMs = 600000 });
                var botContext = new BotContext(new SpreadConfiguration { BaseMarket = "BTC" }, new MarketData { Symbol = "NMR-BTC" }, BotState.Bought, 10);
                var bot = new Bot(repository, botContext, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository));
                var context = new FileCoordinatorContext();

                // Halfway through a change, as the bot would be while processing a message: its sell is placed but not yet its current order
                botContext.Semaphore.Wait();
                context.AddBot(bot);
                botContext.BotState = BotState.SellOrderActive;
                Thread.Sleep(1500); // past the 1 s save debounce

                Assert.IsFalse(File.Exists(SessionFile), "the save must wait for the bot to finish its change");

                botContext.CurrentOrderData = new OrderData { Id = "sell-1", ClientOrderId = "sell-1", Direction = OrderDirection.SELL, Status = OrderStatus.OPEN };
                botContext.Semaphore.Release();

                var stopwatch = Stopwatch.StartNew();
                while (!File.Exists(SessionFile))
                {
                    if (stopwatch.ElapsedMilliseconds > 2000)
                        Assert.Fail("Timed out waiting for the save");
                    Thread.Sleep(10);
                }

                var saved = JsonConvert.DeserializeObject<PreviousSessionContext>(File.ReadAllText(SessionFile)).BotContexts.Single();
                Assert.AreEqual(BotState.SellOrderActive, saved.BotState);
                Assert.AreEqual("sell-1", saved.CurrentOrderData?.Id);

                Thread.Sleep(1500); // the order change requested another save; let it land before the directory is removed
            });
        }

        [TestMethod]
        public void SessionFileThatCannotBeRead_StopsStartupAndStaysInPlace()
        {
            if (!OperatingSystem.IsWindows())
                Assert.Inconclusive("Only Windows enforces the exclusive lock this test relies on");

            InTemporaryDirectory(directory =>
            {
                const string validSession = @"{ ""BotContexts"": [], ""DustPerMarket"": {} }";
                File.WriteAllText(SessionFile, validSession);

                using (File.Open(SessionFile, FileMode.Open, FileAccess.Read, FileShare.None))
                    Assert.ThrowsExactly<IOException>(() => new FileCoordinatorContext().GetPreviousSessionContext());

                Assert.AreEqual(validSession, File.ReadAllText(SessionFile));
                Assert.AreEqual(1, Directory.GetFiles(directory).Length);
            });
        }
    }
}
