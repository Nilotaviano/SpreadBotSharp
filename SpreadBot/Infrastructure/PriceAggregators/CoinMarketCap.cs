using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using RestSharp;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.PriceAggregators
{
    public class CoinMarketCap
    {
        private readonly AppSettings appSettings;
        private readonly RestClient client = new RestClient("https://pro-api.coinmarketcap.com/v1/");
        private readonly List<string> invalidSymbolsForCoinMarketCap = new List<string>();

        private readonly JsonSerializer snakeCaseJsonSerializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new SnakeCaseNamingStrategy() }
        });

        public CoinMarketCap(AppSettings appSettings)
        {
            this.appSettings = appSettings;
        }

        public async Task<IEnumerable<MarketData>> GetLatestQuotes(IEnumerable<string> symbols, bool retry = true)
        {
            var result = Enumerable.Empty<MarketData>();
            var request = new RestRequest("/cryptocurrency/quotes/latest", Method.GET, DataFormat.Json);
            var currencies = string.Join(",", symbols.Except(invalidSymbolsForCoinMarketCap));
            request.AddQueryParameter("symbol", currencies);
            request.AddQueryParameter("convert", appSettings.BaseMarket);

            request.AddHeader("X-CMC_PRO_API_KEY", appSettings.CoinMarketCapApiKey);
            request.AddHeader("Accepts", "application/json");


            IRestResponse<string> restResponse = await client.ExecuteAsync<string>(request);

            switch (restResponse.StatusCode)
            {
                case HttpStatusCode.OK:
                    {
                        var jObject = JObject.Parse(restResponse.Data);
                        result = jObject["data"].Values().Select(q =>
                            new MarketData()
                            {
                                Symbol = $"{q["symbol"].Value<string>()}-{appSettings.BaseMarket}",
                                AggregatorQuote = q["quote"][appSettings.BaseMarket].ToObject<AggregatorQuote>(snakeCaseJsonSerializer)
                            }
                        );
                        break;
                    }
                case HttpStatusCode.BadRequest:
                    {
                        var jObject = JObject.Parse(restResponse.Data);
                        var status = jObject["status"].ToObject<ResponseStatusData>();

                        if (status.error_message.Contains("\"symbol\""))
                        {
                            var invalidSymbols = status.error_message
                                .Replace("Invalid values for \"symbol\": \"", string.Empty)
                                .Replace("Invalid value for \"symbol\": \"", string.Empty)
                                .Replace("\"", string.Empty)
                                .Split(",");
                            invalidSymbolsForCoinMarketCap.AddRange(invalidSymbols);

                            if (retry)
                                return await GetLatestQuotes(symbols, retry: false);
                        }
                        else
                            Logger.Instance.LogUnexpectedError($"Unexpected error on GetLatestQuotes request. Content:{restResponse.Content}");
                        break;
                    }
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.InternalServerError:
                    Logger.Instance.LogUnexpectedError($"Unexpected error on GetLatestQuotes request. Content:{restResponse.Content}");
                    break;
            }


            return result.GroupBy(m => m.Symbol).Select(g => g.First()); //Distinct by symbol, just in case
        }

        private class ResponseStatusData
        {
            public DateTime timestamp { get; set; }
            public int error_code { get; set; }
            public string error_message { get; set; }
            public int elapsed { get; set; }
            public int credit_count { get; set; }
        }
    }
}
