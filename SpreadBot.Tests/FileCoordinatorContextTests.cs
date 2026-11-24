using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;
using System;
using System.IO;
using System.Linq;

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
