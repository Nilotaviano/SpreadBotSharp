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
        [TestMethod]
        public void UnreadableSessionFile_IsMovedAsideInsteadOfOverwritten()
        {
            // coordinatorContext.json is cwd-relative
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var previousDirectory = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = directory;
                File.WriteAllText("coordinatorContext.json", "{ not json");

                Assert.IsNull(new FileCoordinatorContext().GetPreviousSessionContext());

                Assert.IsFalse(File.Exists("coordinatorContext.json"), "the next save must not overwrite the unreadable file");
                var preserved = Directory.GetFiles(directory, "coordinatorContext.json.unreadable-*").Single();
                Assert.AreEqual("{ not json", File.ReadAllText(preserved));
            }
            finally
            {
                Environment.CurrentDirectory = previousDirectory;
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
