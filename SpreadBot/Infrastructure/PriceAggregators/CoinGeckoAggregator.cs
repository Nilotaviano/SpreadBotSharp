using System.Timers;
using System.Collections.Generic;
using System.Threading.Tasks;
using SpreadBot.Models.Repository;
using System;
using System.Linq;
using SpreadBot.Models;
using RestSharp;
using Newtonsoft.Json;
using System.Net;

namespace SpreadBot.Infrastructure.PriceAggregators
{
    public class CoinGeckoAggregator
    {
        private readonly CoinsClient _client = new CoinsClient();
        private Dictionary<string, string> _coinIdPerSymbol;
        private readonly Timer _updateCoinsTimer = new Timer(TimeSpan.FromMinutes(20).TotalMilliseconds);

        public CoinGeckoAggregator(Action readyCallback)
        {
            ;
            _updateCoinsTimer.Elapsed += async (sender, e) => await _UpdateCoinsAsync();
            _UpdateCoinsAsync(readyCallback);
        }

        private async Task _UpdateCoinsAsync(Action readyCallback = null)
        {
            try
            {
                var coins = await _client.GetCoinList();

                _coinIdPerSymbol = coins
                    .GroupBy(coinFullData => coinFullData.Symbol.ToUpper())
                    .Where(g => g.Count() == 1) //We don't want dubious symbols
                    .ToDictionary(g => g.Key, g => g.Single().Id);

                readyCallback?.Invoke();
            }
            catch (Exception e)
            {
                Logger.Instance.LogUnexpectedError($"Error while getting coins data from CoinGecko: {e}");
            }
        }

        public async Task<IEnumerable<Market>> GetLatestQuotesAsync(Dictionary<string, IEnumerable<string>> coinsPerBaseMarket)
        {
            var results = await Task.WhenAll(coinsPerBaseMarket.Select(x => GetLatestQuotes(x.Value, x.Key)).ToArray());

            return results.SelectMany(x => x);
        }

        private async Task<IEnumerable<Market>> GetLatestQuotes(IEnumerable<string> symbols, string baseMarket)
        {
            if (_coinIdPerSymbol == null || !_coinIdPerSymbol.Any())
                return Enumerable.Empty<Market>();

            var vsCurrency = baseMarket.StartsWith("usd", StringComparison.OrdinalIgnoreCase) ? "usd" : baseMarket;

            try
            {
                symbols = symbols.Select(s => s.ToUpper());

                var coinIdPerSymbol = _coinIdPerSymbol;

                if (coinIdPerSymbol != null && coinIdPerSymbol.Any())
                {
                    var ids = symbols.Where(s => coinIdPerSymbol.ContainsKey(s)).Select(s => coinIdPerSymbol[s]);

                    if (ids.Any())
                    {
                        var coins = await _client.GetCoinMarkets(vsCurrency, ids.ToArray(), "gecko_desc", perPage: 250, page: 1, priceChangePercentage: "24h", sparkline: false, category: string.Empty);

                        return coins
                            .Where(c => c.PriceChangePercentage24H.HasValue && c.CurrentPrice.HasValue && c.LastUpdated.HasValue && c.TotalVolume.HasValue)
                            .Select(c => new Market()
                            {
                                Symbol = $"{c.Symbol.ToUpper()}-{baseMarket}",
                                AggregatorQuote = new AggregatorQuote()
                                {
                                    PriceChangePercentage24H = Convert.ToDecimal(c.PriceChangePercentage24H.Value),
                                    LastUpdated = c.LastUpdated.Value.DateTime,
                                    CurrentPrice = Convert.ToDecimal(c.CurrentPrice.Value),
                                    volume_24h = Convert.ToDecimal(c.TotalVolume.Value)
                                }
                            }
                        );
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Instance.LogUnexpectedError($"Error while getting quotes from CoinGecko: {e}");
            }

            return Enumerable.Empty<Market>();
        }
    }

    public class CoinsClient
    {
        private readonly RestClient client = new RestClient("https://api.coingecko.com/api/v3/");


        public async Task<IEnumerable<CoinList>> GetCoinList()
        {
            var request = new RestRequest("/coins/list", Method.GET, DataFormat.Json);
            var result = await client.ExecuteAsync(request).ConfigureAwait(false);

            return JsonConvert.DeserializeObject<IEnumerable<CoinList>>(result.Content);
        }

        public async Task<IEnumerable<CoinMarkets>> GetCoinMarkets(string vsCurrency, string[] ids, string order, int? perPage,
            int? page, bool sparkline, string priceChangePercentage, string category)
        {
            var request = new RestRequest("/coins/markets", Method.GET, DataFormat.Json);
            request.AddQueryParameter("vs_currency", vsCurrency.ToLower());
            request.AddQueryParameter("ids", string.Join(',', ids));
            request.AddQueryParameter("per_page", 250.ToString());
            request.AddQueryParameter("page", 1.ToString());
            var result = await client.ExecuteAsync(request).ConfigureAwait(false);

            return JsonConvert.DeserializeObject<IEnumerable<CoinMarkets>>(result.Content);
        }
    }

    public class CoinMarkets
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("image")]
        public Uri Image { get; set; }

        [JsonProperty("current_price")]
        public decimal? CurrentPrice { get; set; }

        [JsonProperty("market_cap")]
        public decimal? MarketCap { get; set; }

        [JsonProperty("total_volume")]
        public decimal? TotalVolume { get; set; }

        [JsonProperty("high_24h")]
        public decimal? High24H { get; set; }

        [JsonProperty("low_24h")]
        public decimal? Low24H { get; set; }

        [JsonProperty("ath")]
        public decimal? Ath { get; set; }

        [JsonProperty("ath_change_percentage")]
        public decimal? AthChangePercentage { get; set; }

        [JsonProperty("ath_date")]
        public DateTimeOffset? AthDate { get; set; }

        [JsonProperty("last_updated")]
        public DateTimeOffset? LastUpdated { get; set; }

        [JsonProperty("price_change_percentage_14d_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage14DInCurrency { get; set; }

        [JsonProperty("price_change_percentage_1h_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage1HInCurrency { get; set; }

        [JsonProperty("price_change_percentage_1y_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage1YInCurrency { get; set; }

        [JsonProperty("price_change_percentage_200d_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage200DInCurrency { get; set; }

        [JsonProperty("price_change_percentage_24h_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage24HInCurrency { get; set; }

        [JsonProperty("price_change_percentage_30d_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage30DInCurrency { get; set; }

        [JsonProperty("price_change_percentage_7d_in_currency", NullValueHandling = NullValueHandling.Ignore)]
        public decimal? PriceChangePercentage7DInCurrency { get; set; }

        [JsonProperty("market_cap_rank")]
        public long? MarketCapRank { get; set; }

        [JsonProperty("price_change_24h")]
        public decimal? PriceChange24H { get; set; }

        [JsonProperty("price_change_percentage_24h")]
        public double? PriceChangePercentage24H { get; set; }

        [JsonProperty("market_cap_change_24h")]
        public decimal? MarketCapChange24H { get; set; }

        [JsonProperty("market_cap_change_percentage_24h")]
        public decimal? MarketCapChangePercentage24H { get; set; }

        [JsonProperty("circulating_supply")]
        public string CirculatingSupply { get; set; }

        [JsonProperty("total_supply")]
        public decimal? TotalSupply { get; set; }
    }

    public class CoinList
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("platforms")]
        public Dictionary<string, string> Platforms { get; set; }
    }
}
