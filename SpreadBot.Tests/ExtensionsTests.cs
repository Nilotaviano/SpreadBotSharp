using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;
using System;
using System.IO;

namespace SpreadBot.Tests
{
    [TestClass]
    public class ExtensionsTests
    {
        [TestMethod]
        public void CeilToTick_RoundsUpToTheNextTick()
        {
            Assert.AreEqual(0.00010000m, 0.0000999901m.CeilToTick(0.00000100m));
            Assert.AreEqual(1.25m, 1.25m.CeilToTick(0.05m));
            Assert.AreEqual(1.30m, 1.26m.CeilToTick(0.05m));
            Assert.AreEqual(1.2345m, 1.2345m.CeilToTick(null));
        }

        [TestMethod]
        public void WriteAllTextAtomic_ReplacesExistingFileAndLeavesNoTempFile()
        {
            var directory = Directory.CreateTempSubdirectory("spreadbot-tests-").FullName;
            try
            {
                var path = Path.Combine(directory, "context.json");
                File.WriteAllText(path, "old contents that are longer than the new ones");

                Extensions.WriteAllTextAtomic(path, "{\"new\":true}");

                Assert.AreEqual("{\"new\":true}", File.ReadAllText(path));
                CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(directory));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestMethod]
        public void WriteAllTextAtomic_WhenTempFileCannotBeCreated_LeavesOriginalFileIntact()
        {
            var directory = Directory.CreateTempSubdirectory("spreadbot-tests-").FullName;
            try
            {
                var path = Path.Combine(directory, "context.json");
                File.WriteAllText(path, "{\"previous\":\"état\"}");
                Directory.CreateDirectory(path + ".tmp");

                var exception = Assert.Throws<Exception>(() => Extensions.WriteAllTextAtomic(path, "{\"new\":true}"));

                Assert.IsTrue(exception is UnauthorizedAccessException || exception is IOException, exception.ToString());

                Assert.AreEqual("{\"previous\":\"état\"}", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [TestMethod]
        public void WriteAllTextAtomic_CreatesMissingFile()
        {
            var directory = Directory.CreateTempSubdirectory("spreadbot-tests-").FullName;
            try
            {
                var path = Path.Combine(directory, "profit.json");

                Extensions.WriteAllTextAtomic(path, "{}");

                Assert.AreEqual("{}", File.ReadAllText(path));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
