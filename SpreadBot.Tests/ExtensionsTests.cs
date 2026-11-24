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
        public void Hash_EmptyString_ReturnsLowercaseSha512Hex()
        {
            Assert.AreEqual(
                "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e",
                string.Empty.Hash());
        }

        [TestMethod]
        public void Hash_JsonBody_ReturnsLowercaseSha512Hex()
        {
            Assert.AreEqual(
                "efb7a8298f905ae743dbe2152e162415f62a16d2d5ac5c78816dcd57114e7a574729b813988f1d0984cf6f38c4fcc9a37ea9fec3da351983536f72785d7ab707",
                "{\"a\":1}".Hash());
        }

        [TestMethod]
        public void Sign_ReturnsLowercaseHmacSha512Hex()
        {
            Assert.AreEqual(
                "b42af09057bac1e2d41708e48a902e09b5ff7f12ab428a4fe86653c73dd248fb82f948a549f7b791a5b41915ee4d1ec3935357e4e2317250d0372afa2ebeeb3a",
                "The quick brown fox jumps over the lazy dog".Sign("key"));
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
