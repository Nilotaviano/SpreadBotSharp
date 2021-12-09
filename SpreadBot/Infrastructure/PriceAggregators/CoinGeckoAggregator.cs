using System.Timers;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoinGecko.Clients;
using CoinGecko.Interfaces;
using SpreadBot.Models.Repository;
using System;
using System.Linq;
using SpreadBot.Models;

namespace SpreadBot.Infrastructure.PriceAggregators
{
    public class CoinGeckoAggregator
    {
        private readonly ICoinGeckoClient _client;
        private Dictionary<string, string> _coinIdPerSymbol;
        private readonly Timer _updateCoinsTimer = new Timer(TimeSpan.FromMinutes(20).TotalMilliseconds);

        public CoinGeckoAggregator(Action readyCallback)
        {
            _client = CoinGeckoClient.Instance;
            _updateCoinsTimer.Elapsed += async (sender, e) => await _UpdateCoinsAsync();
            _UpdateCoinsAsync(readyCallback);
        }

        private async Task _UpdateCoinsAsync(Action readyCallback = null)
        {
            try
            {
                var coins = await _client.CoinsClient.GetAllCoinsData();

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

            try
            {
                symbols = symbols.Select(s => s.ToUpper());

                var coinIdPerSymbol = _coinIdPerSymbol;

                if (coinIdPerSymbol != null && coinIdPerSymbol.Any())
                {
                    var ids = symbols.Where(s => coinIdPerSymbol.ContainsKey(s)).Select(s => coinIdPerSymbol[s]);

                    var coins = await _client.CoinsClient.GetCoinMarkets(baseMarket, symbols.ToArray(), "gecko_desc", perPage: 250, page: 1, priceChangePercentage: "24h", sparkline: false, category: null);

                    return coins
                        .Where(c => c.PriceChangePercentage24H.HasValue && c.CurrentPrice.HasValue && c.LastUpdated.HasValue && c.TotalVolume.HasValue)
                        .Select(c => new Market()
                        {
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
            catch (Exception e)
            {
                Logger.Instance.LogUnexpectedError($"Error while getting quotes from CoinGecko: {e}");
            }

            return Enumerable.Empty<Market>();
        }
    }
}
