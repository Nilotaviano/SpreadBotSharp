using Microsoft.VisualStudio.TestTools.UnitTesting;
using RestSharp;
using System.Linq;

namespace SpreadBot.Tests
{
    // BittrexClient.ExecuteAuthenticatedRequest signs BuildUri(request) and the RequestBody parameter value,
    // so any change in how RestSharp produces either one breaks every authenticated Bittrex call.
    [TestClass]
    public class RestSharpContractTests
    {
        [TestMethod]
        public void BuildUri_AppendsResourceAndQueryParametersToBaseUrl()
        {
            var client = new RestClient("https://api.bittrex.com/v3");
            var request = new RestRequest("/orders/closed", Method.GET, DataFormat.Json);
            request.AddQueryParameter("pageSize", "200");
            request.AddQueryParameter("previousPageToken", "abc");

            Assert.AreEqual(
                "https://api.bittrex.com/v3/orders/closed?pageSize=200&previousPageToken=abc",
                client.BuildUri(request).ToString());
        }

        [TestMethod]
        public void AddParameter_RequestBody_ExposesRawBodyString()
        {
            const string body = "{\"marketSymbol\":\"NMR-BTC\"}";
            var request = new RestRequest("/orders", Method.POST, DataFormat.Json);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var bodyParameter = request.Parameters.Single(p => p.Type == ParameterType.RequestBody);

            Assert.AreEqual(body, bodyParameter.Value as string);
            Assert.AreEqual("POST", request.Method.ToString());
        }
    }
}
