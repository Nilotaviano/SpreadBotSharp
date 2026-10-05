using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SpreadBot.Tests
{
    [TestClass]
    public class DataConverterTests
    {
        private static string ToWireFormat(string json)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                deflate.Write(bytes, 0, bytes.Length);
            }
            return Convert.ToBase64String(output.ToArray());
        }

        [TestMethod]
        public void Decode_InflatesBase64DeflateJsonIntoOrderMessage()
        {
            const string json = "{\"accountId\":\"acc-1\",\"sequence\":7,\"delta\":{\"id\":\"order-1\",\"marketSymbol\":\"NMR-BTC\",\"direction\":\"SELL\",\"type\":\"LIMIT\",\"status\":\"CLOSED\",\"quantity\":1.5,\"limit\":0.00010101,\"fillQuantity\":1.5,\"commission\":0.00000030,\"proceeds\":0.00015151,\"closedAt\":\"2021-05-10T14:42:32.123Z\"}}";

            var decoded = DataConverter.Decode<BittrexApiOrderData>(ToWireFormat(json));

            Assert.AreEqual("acc-1", decoded.AccountId);
            Assert.AreEqual(7, decoded.Sequence);
            Assert.AreEqual("order-1", decoded.Delta.Id);
            Assert.AreEqual("NMR-BTC", decoded.Delta.MarketSymbol);
            Assert.AreEqual(OrderDirection.SELL, decoded.Delta.Direction);
            Assert.AreEqual(OrderType.LIMIT, decoded.Delta.Type);
            Assert.AreEqual(OrderStatus.CLOSED, decoded.Delta.Status);
            Assert.AreEqual(1.5m, decoded.Delta.Quantity);
            Assert.AreEqual(0.00010101m, decoded.Delta.Limit);
            Assert.AreEqual(0.0000003m, decoded.Delta.Commission);
            Assert.AreEqual(0.00015151m, decoded.Delta.Proceeds);
            Assert.AreEqual(new DateTime(2021, 5, 10, 14, 42, 32, 123, DateTimeKind.Utc), decoded.Delta.ClosedAt);
            Assert.AreEqual(DateTimeKind.Utc, decoded.Delta.ClosedAt.Kind);
        }
    }
}
