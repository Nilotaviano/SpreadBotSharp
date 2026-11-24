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
        public void UnparsableSessionFile_IsMovedAsideInsteadOfOverwritten()
        {
            InTemporaryDirectory(directory =>
            {
                File.WriteAllText(SessionFile, "{ not json");

                Assert.IsNull(new FileCoordinatorContext().GetPreviousSessionContext());

                Assert.IsFalse(File.Exists(SessionFile), "the next save must not overwrite the unparsable file");
                var preserved = Directory.GetFiles(directory, $"{SessionFile}.unreadable-*").Single();
                Assert.AreEqual("{ not json", File.ReadAllText(preserved));
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
