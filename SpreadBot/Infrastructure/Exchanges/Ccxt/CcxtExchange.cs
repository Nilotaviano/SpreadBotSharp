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
        //Covers clock drift and the lookup's own latency on top of the exchange's recvWindow
        private static readonly TimeSpan OrderRequestLifetimeMargin = TimeSpan.FromSeconds(50);
        //Exchanges without a recvWindow don't bound when a delayed request may still be accepted; this is a generous guess
        private static readonly TimeSpan DefaultOrderRequestLifetime = TimeSpan.FromMinutes(5);

        private readonly ccxt.Exchange exchange;
        private readonly CancellationTokenSource streams = new CancellationTokenSource();
        private int disposed;

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

        internal CcxtExchange(ccxt.Exchange exchange)
        {
            this.exchange = exchange;
        }

        public bool IsSetup { get; private set; }

        public decimal FeeRate { get; private set; } = DefaultFeeRate;

        //Binance-style exchanges reject a request signed more than recvWindow ago (10 s in ccxt's Binance)
        public TimeSpan OrderRequestLifetime =>
            exchange.options.TryGetValue("recvWindow", out var recvWindow) && recvWindow != null
                ? TimeSpan.FromMilliseconds(Convert.ToDouble(recvWindow)) + OrderRequestLifetimeMargin
                : DefaultOrderRequestLifetime;

        public async Task Setup(IEnumerable<string> quoteCurrencies)
        {
            var quotes = new HashSet<string>(quoteCurrencies);

            markets = await Call(() => exchange.LoadMarkets());
            watchedSymbols = markets.Values
                .Where(m => m.spot && m.active != false && quotes.Contains(m.quote))
                .Select(m => (object)m.symbol)
                .ToList();

            //The highest fee among the traded markets, so buys always leave room for it
            var takerFees = watchedSymbols.Select(s => markets[(string)s].taker).Where(fee => fee.HasValue).Select(fee => fee.Value).ToList();
            if (takerFees.Count > 0)
                FeeRate = (decimal)takerFees.Max();

            Logger.Instance.LogMessage($"Loaded {markets.Count} markets from {exchange.id}; watching {watchedSymbols.Count} with quote {string.Join(",", quotes)}");

            StartStream("tickers", async () =>
            {
                var tickers = await exchange.WatchTickers(watchedSymbols);
                Dispatch(onMarketDataCallbacks, (IEnumerable<MarketData>)tickers.tickers.Values.Select(ToMarketData).ToList());
            });
            StartStream("balance", async () =>
            {
                var balances = await exchange.WatchBalance();
                foreach (var balance in ToBalanceData(balances))
                    Dispatch(onBalanceCallbacks, balance);
            });
            StartStream("orders", async () =>
            {
                var orders = await exchange.WatchOrders();
                foreach (var order in orders.Select(ToOrderData).ToList())
                    Dispatch(onOrderCallbacks, order);
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

        public async Task<OrderData> GetOrderByClientId(string clientOrderId, string marketSymbol)
        {
            var parameters = new Dictionary<string, object> { ["clientOrderId"] = clientOrderId };
            return ToOrderData(await Call(() => exchange.FetchOrder(null, marketSymbol, parameters)));
        }

        public Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit) => CreateMakerOrder(marketSymbol, "buy", quantity, limit);

        public Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit) => CreateMakerOrder(marketSymbol, "sell", quantity, limit);

        public Task<OrderData> SellMarket(string marketSymbol, decimal quantity) => CreateOrder(marketSymbol, "market", "sell", quantity, null);

        public async Task<OrderData> CancelOrder(string orderId, string marketSymbol)
        {
            await Call(() => exchange.CancelOrder(orderId, marketSymbol));

            //Some exchanges answer a cancel with a partial order; fetch it so fills and status are complete
            return await GetOrder(orderId, marketSymbol);
        }

        //Post-only: a spread bot must never take liquidity; if the price would cross, the exchange rejects the order
        private Task<OrderData> CreateMakerOrder(string marketSymbol, string side, decimal quantity, decimal limit) =>
            CreateOrder(marketSymbol, "limit", side, quantity, limit, new Dictionary<string, object> { ["postOnly"] = true });

        /// <summary>
        /// Places an order under a fresh client order id. If the answer doesn't show the order's state (no answer at all,
        /// or an acknowledgement without a status or id), throws <see cref="OrderStateUnknownException"/> with the order
        /// as sent, so the caller can look it up by that id: placing it again could leave a second order on the exchange.
        /// </summary>
        private async Task<OrderData> CreateOrder(string marketSymbol, string type, string side, decimal quantity, decimal? limit, Dictionary<string, object> parameters = null)
        {
            var clientOrderId = NewClientOrderId();
            parameters ??= new Dictionary<string, object>();
            parameters["clientOrderId"] = clientOrderId;

            var sent = new OrderData
            {
                ClientOrderId = clientOrderId,
                MarketSymbol = marketSymbol,
                Direction = side == "buy" ? OrderDirection.BUY : OrderDirection.SELL,
                Type = type == "limit" ? OrderType.LIMIT : OrderType.MARKET,
                Quantity = quantity,
                Limit = limit ?? 0,
                Status = OrderStatus.UNKNOWN,
                CreatedAt = DateTime.UtcNow
            };

            ccxt.Order order;
            try
            {
                order = await exchange.CreateOrder(marketSymbol, type, side, (double)quantity, limit.HasValue ? (double)limit.Value : (double?)null, parameters);
            }
            //Rate limits and timestamp rejections refuse the request itself. Any other failed operation (network errors, unreadable
            //responses, Binance's -1000/-1001/-1006 "execution status unknown") may have happened after the exchange accepted it
            catch (ccxt.OperationFailed e) when (e is not ccxt.RateLimitExceeded && e is not ccxt.DDoSProtection && e is not ccxt.InvalidNonce)
            {
                throw new OrderStateUnknownException(sent, $"Creating {side} {type} order {clientOrderId} on {marketSymbol} failed without an answer: {e.Message}");
            }
            catch (ccxt.BaseError e)
            {
                throw ToApiException(e);
            }

            if (order.status == null || order.id == null)
                throw new OrderStateUnknownException(sent, $"Order {clientOrderId} on {marketSymbol} was acknowledged without a status or id (id: {order.id}, status: {order.status})");

            return ToOrderData(order);
        }

        //Binance allows up to 36 characters from [.A-Za-z0-9:/_-]
        private static string NewClientOrderId() => "sb" + Guid.NewGuid().ToString("N");

        private static void Dispatch<T>(IEnumerable<Action<T>> callbacks, T data)
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(data);
                }
                catch (Exception e)
                {
                    Logger.Instance.LogUnexpectedError($"Stream callback failed for {typeof(T).Name}: {e}");
                }
            }
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

        //Binance filter failures for minimum notional / lot size, e.g. "Filter failure: NOTIONAL" or "Order's notional must be no smaller than ..."
        private static bool IsBelowMinimum(string message) =>
            message != null && (message.Contains("NOTIONAL", StringComparison.OrdinalIgnoreCase) || message.Contains("LOT_SIZE", StringComparison.OrdinalIgnoreCase));

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

        internal static IEnumerable<BalanceData> ToBalanceData(ccxt.Balances balances)
        {
            //A partial entry would otherwise overwrite a known balance with zero
            return balances.balances.Where(b => b.Value.free.HasValue && b.Value.total.HasValue).Select(b => new BalanceData
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
            if (Interlocked.Exchange(ref disposed, 1) == 1)
                return;

            streams.Cancel();

            if (!exchange.Close().Wait(TimeSpan.FromSeconds(5)))
                Logger.Instance.LogError("Timed out closing the exchange connections");

            streams.Dispose();
        }
    }
}
