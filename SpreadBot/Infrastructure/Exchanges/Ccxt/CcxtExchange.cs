using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges.Ccxt
{
    /// <summary>
    /// IExchange over CCXT (ccxt.pro for websocket streams). Holds the base ccxt.Exchange type, so any exchange id with a ccxt.pro class works.
    /// </summary>
    public sealed class CcxtExchange : IExchange, IDisposable
    {
        private const int TickSizePrecisionMode = 4;
        private const decimal DefaultFeeRate = 0.001m;
        private static readonly TimeSpan StreamRetryDelay = TimeSpan.FromSeconds(5);

        private readonly ccxt.Exchange exchange;
        private readonly CancellationTokenSource streams = new CancellationTokenSource();

        private readonly ConcurrentBag<Action<BalanceData>> onBalanceCallbacks = new ConcurrentBag<Action<BalanceData>>();
        private readonly ConcurrentBag<Action<IEnumerable<MarketData>>> onMarketDataCallbacks = new ConcurrentBag<Action<IEnumerable<MarketData>>>();
        private readonly ConcurrentBag<Action<OrderData>> onOrderCallbacks = new ConcurrentBag<Action<OrderData>>();
        private readonly ConcurrentBag<Action> onStreamInterruptedCallbacks = new ConcurrentBag<Action>();

        private Dictionary<string, ccxt.MarketInterface> markets = new Dictionary<string, ccxt.MarketInterface>();
        //ccxt's typed methods take symbol lists as object lists
        private List<object> watchedSymbols = new List<object>();

        public CcxtExchange(string exchangeId, string apiKey, string apiSecret, bool useSandbox)
        {
            var type = Type.GetType($"ccxt.pro.{exchangeId}, ccxt")
                ?? throw new ArgumentException($"CCXT has no websocket-capable exchange '{exchangeId}'", nameof(exchangeId));

            var config = new Dictionary<string, object>
            {
                ["apiKey"] = apiKey,
                ["secret"] = apiSecret,
                ["enableRateLimit"] = true,
                ["options"] = new Dictionary<string, object>
                {
                    ["defaultType"] = "spot",
                    //Resyncs fetch every open order at once; the higher request weight is fine at that frequency
                    ["warnOnFetchOpenOrdersWithoutSymbol"] = false,
                    //Binance rejects requests whose timestamp drifts from its clock
                    ["adjustForTimeDifference"] = true
                }
            };

            exchange = (ccxt.Exchange)Activator.CreateInstance(type, config);

            if (useSandbox)
                exchange.setSandboxMode(true);
        }

        public bool IsSetup { get; private set; }

        public decimal FeeRate { get; private set; } = DefaultFeeRate;

        public async Task Setup(IEnumerable<string> quoteCurrencies)
        {
            var quotes = new HashSet<string>(quoteCurrencies);

            markets = await Call(() => exchange.LoadMarkets());
            watchedSymbols = markets.Values
                .Where(m => m.spot && m.active != false && quotes.Contains(m.quote))
                .Select(m => (object)m.symbol)
                .ToList();

            var takerFee = markets.Values.Where(m => m.spot && m.taker.HasValue).Select(m => m.taker).FirstOrDefault();
            if (takerFee.HasValue)
                FeeRate = (decimal)takerFee.Value;

            Logger.Instance.LogMessage($"Loaded {markets.Count} markets from {exchange.id}; watching {watchedSymbols.Count} with quote {string.Join(",", quotes)}");

            StartStream("tickers", async () =>
            {
                var tickers = await exchange.WatchTickers(watchedSymbols);
                var marketData = tickers.tickers.Values.Select(ToMarketData).ToList();
                foreach (var callback in onMarketDataCallbacks)
                    callback(marketData);
            });
            StartStream("balance", async () =>
            {
                var balances = await exchange.WatchBalance();
                foreach (var balance in ToBalanceData(balances))
                    foreach (var callback in onBalanceCallbacks)
                        callback(balance);
            });
            StartStream("orders", async () =>
            {
                var orders = await exchange.WatchOrders();
                foreach (var order in orders.Select(ToOrderData))
                    foreach (var callback in onOrderCallbacks)
                        callback(order);
            });

            IsSetup = true;
        }

        public void OnBalance(Action<BalanceData> callback) => onBalanceCallbacks.Add(callback);
        public void OnMarketData(Action<IEnumerable<MarketData>> callback) => onMarketDataCallbacks.Add(callback);
        public void OnOrder(Action<OrderData> callback) => onOrderCallbacks.Add(callback);
        public void OnStreamInterrupted(Action callback) => onStreamInterruptedCallbacks.Add(callback);

        public async Task<IReadOnlyList<BalanceData>> GetBalances()
        {
            return ToBalanceData(await Call(() => exchange.FetchBalance())).ToList();
        }

        public Task<IReadOnlyList<MarketData>> GetMarkets()
        {
            IReadOnlyList<MarketData> result = watchedSymbols.Select(s => ToMarketData(markets[(string)s])).ToList();
            return Task.FromResult(result);
        }

        public async Task<IReadOnlyList<MarketData>> GetTickers()
        {
            var tickers = await Call(() => exchange.FetchTickers(watchedSymbols));
            return tickers.tickers.Values.Select(ToMarketData).ToList();
        }

        public async Task<IReadOnlyList<OrderData>> GetOpenOrders()
        {
            var orders = await Call(() => exchange.FetchOpenOrders());
            return orders.Select(ToOrderData).ToList();
        }

        public async Task<OrderData> GetOrder(string orderId, string marketSymbol)
        {
            return ToOrderData(await Call(() => exchange.FetchOrder(orderId, marketSymbol)));
        }

        public Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit) => CreateMakerOrder(marketSymbol, "buy", quantity, limit);

        public Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit) => CreateMakerOrder(marketSymbol, "sell", quantity, limit);

        public async Task<OrderData> SellMarket(string marketSymbol, decimal quantity)
        {
            return ToOrderData(await Call(() => exchange.CreateOrder(marketSymbol, "market", "sell", (double)quantity)));
        }

        public async Task<OrderData> CancelOrder(string orderId, string marketSymbol)
        {
            await Call(() => exchange.CancelOrder(orderId, marketSymbol));

            //Some exchanges answer a cancel with a partial order; fetch it so fills and status are complete
            return await GetOrder(orderId, marketSymbol);
        }

        private async Task<OrderData> CreateMakerOrder(string marketSymbol, string side, decimal quantity, decimal limit)
        {
            //Post-only: a spread bot must never take liquidity; if the price would cross, the exchange rejects the order
            var parameters = new Dictionary<string, object> { ["postOnly"] = true };

            return ToOrderData(await Call(() => exchange.CreateOrder(marketSymbol, "limit", side, (double)quantity, (double)limit, parameters)));
        }

        private void StartStream(string name, Func<Task> receive)
        {
            Task.Factory.StartNew(async () =>
            {
                var interrupted = false;

                while (!streams.IsCancellationRequested)
                {
                    try
                    {
                        await receive();

                        if (interrupted)
                        {
                            interrupted = false;
                            Logger.Instance.LogMessage($"{name} stream recovered");
                            foreach (var callback in onStreamInterruptedCallbacks)
                                callback();
                        }
                    }
                    catch (Exception e) when (!streams.IsCancellationRequested)
                    {
                        interrupted = true;
                        Logger.Instance.LogError($"{name} stream failed, retrying in {StreamRetryDelay.TotalSeconds}s: {e.Message}");

                        try
                        {
                            await Task.Delay(StreamRetryDelay, streams.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private static async Task<T> Call<T>(Func<Task<T>> request)
        {
            try
            {
                return await request();
            }
            catch (ccxt.BaseError e)
            {
                throw ToApiException(e);
            }
        }

        internal static ApiException ToApiException(ccxt.BaseError error)
        {
            var type = error switch
            {
                ccxt.InsufficientFunds => ApiErrorType.InsufficientFunds,
                ccxt.OrderNotFound => ApiErrorType.OrderNotOpen,
                ccxt.OrderImmediatelyFillable => ApiErrorType.RetryLater,
                ccxt.InvalidOrder when IsBelowMinimum(error.Message) => ApiErrorType.DustTrade,
                ccxt.RateLimitExceeded => ApiErrorType.Throttled,
                ccxt.DDoSProtection => ApiErrorType.Throttled,
                ccxt.NetworkError => ApiErrorType.RetryLater,
                ccxt.AuthenticationError => ApiErrorType.Unauthorized,
                ccxt.BadSymbol => ApiErrorType.MarketOffline,
                _ => ApiErrorType.UnknownError
            };

            return new ApiException(type, error.Message);
        }

        //Binance filter failures for minimum notional / lot size, e.g. "Filter failure: NOTIONAL"
        private static bool IsBelowMinimum(string message) =>
            message != null && (message.Contains("NOTIONAL") || message.Contains("LOT_SIZE"));

        internal static OrderData ToOrderData(ccxt.Order order)
        {
            var (coin, quote) = SplitSymbol(order.symbol);
            var fees = order.fees != null && order.fees.Count > 0
                ? order.fees
                : order.fee.HasValue ? new List<ccxt.Fee> { order.fee.Value } : new List<ccxt.Fee>();

            return new OrderData
            {
                Id = order.id,
                ClientOrderId = order.clientOrderId ?? order.id,
                MarketSymbol = order.symbol,
                Direction = order.side == "buy" ? OrderDirection.BUY : order.side == "sell" ? OrderDirection.SELL : OrderDirection.UNDEFINED,
                Type = order.type == "limit" ? OrderType.LIMIT : order.type == "market" ? OrderType.MARKET : OrderType.UNDEFINED,
                Quantity = ToDecimal(order.amount) ?? 0,
                Limit = ToDecimal(order.price) ?? 0,
                FillQuantity = ToDecimal(order.filled) ?? 0,
                Proceeds = ToDecimal(order.cost) ?? 0,
                Commission = fees.Where(f => f.currency == quote).Sum(f => ToDecimal(f.cost) ?? 0),
                CoinCommission = fees.Where(f => f.currency == coin).Sum(f => ToDecimal(f.cost) ?? 0),
                Status = order.status == "open" ? OrderStatus.OPEN : order.status == null ? OrderStatus.UNDEFINED : OrderStatus.CLOSED,
                CreatedAt = ToDateTime(order.timestamp) ?? default,
                UpdatedAt = ToDateTime(order.lastUpdateTimestamp ?? order.timestamp) ?? default,
                ClosedAt = order.status == "open" ? default : ToDateTime(order.lastTradeTimestamp ?? order.lastUpdateTimestamp) ?? default
            };
        }

        internal static MarketData ToMarketData(ccxt.Ticker ticker)
        {
            return new MarketData
            {
                Symbol = ticker.symbol,
                BidRate = ToDecimal(ticker.bid),
                AskRate = ToDecimal(ticker.ask),
                LastTradeRate = ToDecimal(ticker.last),
                High = ToDecimal(ticker.high),
                Low = ToDecimal(ticker.low),
                Volume = ToDecimal(ticker.baseVolume),
                QuoteVolume = ToDecimal(ticker.quoteVolume),
                PercentChange = ToDecimal(ticker.percentage),
                UpdatedAt = ToDateTime(ticker.timestamp)
            };
        }

        private MarketData ToMarketData(ccxt.MarketInterface market)
        {
            var price = market.precision?.price;

            return new MarketData
            {
                Symbol = market.symbol,
                Status = market.active == false ? EMarketStatus.Offline : EMarketStatus.Online,
                MinTradeSize = ToDecimal(market.limits?.amount?.min),
                PriceTick = price.HasValue ? PriceTick(price.Value) : null,
                CreatedAt = ToDateTime(market.created)
            };
        }

        private decimal PriceTick(double precision)
        {
            //Tick-size mode gives the tick itself; the other modes give a number of decimal places
            return Convert.ToInt32(exchange.precisionMode) == TickSizePrecisionMode
                ? (decimal)precision
                : 1m / (decimal)Math.Pow(10, precision);
        }

        private static IEnumerable<BalanceData> ToBalanceData(ccxt.Balances balances)
        {
            return balances.balances.Select(b => new BalanceData
            {
                CurrencyAbbreviation = b.Key,
                Amount = ToDecimal(b.Value.free) ?? 0,
                Total = ToDecimal(b.Value.total) ?? 0
            });
        }

        private static (string coin, string quote) SplitSymbol(string symbol)
        {
            var parts = symbol?.Split('/') ?? Array.Empty<string>();
            return parts.Length == 2 ? (parts[0], parts[1].Split(':')[0]) : (null, null);
        }

        private static decimal? ToDecimal(double? value) =>
            value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) ? (decimal)value.Value : null;

        private static DateTime? ToDateTime(long? milliseconds) =>
            milliseconds.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds.Value).UtcDateTime : null;

        public void Dispose()
        {
            streams.Cancel();
            exchange.Close().GetAwaiter().GetResult();
            streams.Dispose();
        }
    }
}
