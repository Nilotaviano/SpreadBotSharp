using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        private const int SignificantDigitsPrecisionMode = 3;
        private const int TickSizePrecisionMode = 4;
        private const decimal DefaultFeeRate = 0.001m;
        //Binance's myTrades returns at most 1000
        private const int MaxTradesPerLookup = 1000;
        private const decimal AmountTolerance = 0.000000001m;
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

        private HashSet<string> quoteCurrencies = new HashSet<string>();
        //Spot markets of the quote currencies, active or not, so a halt or delisting reaches MarketsData as Offline
        private volatile IReadOnlyList<ccxt.MarketInterface> tradedMarkets = new List<ccxt.MarketInterface>();
        //The active ones; ccxt's typed methods take symbol lists as object lists
        private volatile List<object> watchedSymbols = new List<object>();

        internal TimeSpan StreamRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
        //How long a stream that failed gets to subscribe again before it reports the interruption, so updates missed meanwhile are resynced
        internal TimeSpan ResubscribeGrace { get; init; } = TimeSpan.FromSeconds(2);

        public CcxtExchange(string exchangeId, string apiKey, string apiSecret, bool useSandbox)
            : this(CreateExchange(exchangeId, apiKey, apiSecret, useSandbox))
        {
        }

        internal CcxtExchange(ccxt.Exchange exchange)
        {
            this.exchange = exchange;

            //ccxt's C# fetch (4.5.94) never applies its timeout option (10 s by default), so requests would wait for HttpClient's 100 s default
            exchange.httpClient.Timeout = TimeSpan.FromMilliseconds(Convert.ToDouble(exchange.timeout));
        }

        private static ccxt.Exchange CreateExchange(string exchangeId, string apiKey, string apiSecret, bool useSandbox)
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
                    ["adjustForTimeDifference"] = true,
                    //Resyncs reload the markets; Binance would also load its futures markets, which the bot never trades
                    ["fetchMarkets"] = new Dictionary<string, object> { ["types"] = new List<object> { "spot" } }
                }
            };

            var exchange = (ccxt.Exchange)Activator.CreateInstance(type, config);

            if (useSandbox)
                exchange.setSandboxMode(true);

            return exchange;
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
            //A number of significant digits gives no fixed price tick, and the strategies move prices by the tick
            if (Convert.ToInt32(exchange.precisionMode) == SignificantDigitsPrecisionMode)
                throw new NotSupportedException($"{exchange.id} gives price precision as significant digits, which has no fixed price tick");

            this.quoteCurrencies = new HashSet<string>(quoteCurrencies);

            await LoadMarkets(reload: false);

            //The highest fee among the traded markets, so buys always leave room for it
            var takerFees = tradedMarkets.Where(m => m.active != false).Select(m => m.taker).Where(fee => fee.HasValue).Select(fee => fee.Value).ToList();
            if (takerFees.Count > 0)
                FeeRate = (decimal)takerFees.Max();

            Logger.Instance.LogMessage($"Loaded {tradedMarkets.Count} markets from {exchange.id} with quote {string.Join(",", this.quoteCurrencies)}; watching the {watchedSymbols.Count} active ones");

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

        //Status, tick and minimum size change (halts, delistings, filter updates), so every call reloads them
        public async Task<IReadOnlyList<MarketData>> GetMarkets()
        {
            var before = tradedMarkets;
            var lastLoad = exchange.marketsLoading;

            try
            {
                await LoadMarkets(reload: true);
            }
            //Failing would fail the whole resync, balances and orders included
            catch (Exception e)
            {
                //ccxt keeps the failed load, and its methods await it before most requests: they'd all fail until a reload succeeds
                exchange.marketsLoading = lastLoad;
                Logger.Instance.LogError($"Reloading markets failed, keeping the ones loaded before: {e.GetType().Name}: {e.Message}");
            }

            var reloaded = tradedMarkets;
            //A market the reload no longer lists (delisted) would otherwise keep its last status
            var gone = before.Select(m => m.symbol).Except(reloaded.Select(m => m.symbol))
                .Select(symbol => new MarketData { Symbol = symbol, Status = EMarketStatus.Offline });

            return reloaded.Select(ToMarketData).Concat(gone).ToList();
        }

        //The typed LoadMarkets can't reload
        private async Task LoadMarkets(bool reload)
        {
            var loaded = await Call(() => exchange.loadMarkets(reload));
            var traded = loaded.Values
                .Select(market => new ccxt.MarketInterface(market))
                .Where(m => m.spot && quoteCurrencies.Contains(m.quote))
                .ToList();

            if (traded.Count == 0 && tradedMarkets.Count > 0)
            {
                Logger.Instance.LogError($"Reloading markets listed none with quote {string.Join(",", quoteCurrencies)}; keeping the ones loaded before");
                return;
            }

            tradedMarkets = traded;
            watchedSymbols = traded.Where(m => m.active != false).Select(m => (object)m.symbol).ToList();
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
            return await WithFees(await Call(() => exchange.FetchOrder(orderId, marketSymbol)));
        }

        public async Task<OrderData> GetOrderByClientId(string clientOrderId, string marketSymbol)
        {
            var parameters = new Dictionary<string, object> { ["clientOrderId"] = clientOrderId };
            return await WithFees(await Call(() => exchange.FetchOrder(null, marketSymbol, parameters)));
        }

        /// <summary>
        /// Some order endpoints (Binance's) report no fees, and a closed order is booked once, so a fetched closed order with fills
        /// takes its fees from its trades. Until they cover the fill, it fails as RetryLater instead of being booked without fees.
        /// </summary>
        private async Task<OrderData> WithFees(ccxt.Order order)
        {
            if (order.status == "open" || order.status == null || !(order.filled > 0) || Fees(order).Any(IsKnown))
                return ToOrderData(order);

            var trades = await Call(() => exchange.FetchOrderTrades(order.id, order.symbol, null, MaxTradesPerLookup));
            var traded = trades.Sum(t => ToDecimal(t.amount) ?? 0);
            var filled = ToDecimal(order.filled) ?? 0;
            //Each amount crossed double -> decimal on its own, so their sum may miss the fill by a rounding error
            if (filled - traded > filled * AmountTolerance)
            {
                //Retrying won't show more trades than one lookup returns; their fees are as close as it gets
                if (trades.Count >= MaxTradesPerLookup)
                    Logger.Instance.LogError($"Order {order.id} on {order.symbol} filled {filled} in more than {MaxTradesPerLookup} trades; booking the fees of the {traded} they show");
                else
                    throw new ApiException(ApiErrorType.RetryLater, $"Order {order.id} on {order.symbol} filled {filled}, but its trades show {traded} so far, so its fees aren't known yet");
            }

            order.fees = trades.SelectMany(trade => trade.fees?.Count > 0 ? trade.fees : trade.fee.HasValue ? new List<ccxt.Fee> { trade.fee.Value } : new List<ccxt.Fee>()).ToList();
            order.fee = null;
            return ToOrderData(order);
        }

        private static List<ccxt.Fee> Fees(ccxt.Order order) =>
            order.fees != null && order.fees.Count > 0
                ? order.fees
                : order.fee.HasValue ? new List<ccxt.Fee> { order.fee.Value } : new List<ccxt.Fee>();

        private static bool IsKnown(ccxt.Fee fee) => fee.currency != null && fee.cost.HasValue;

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
            //Rate limits (RateLimitExceeded, DDoSProtection) and timestamp rejections (InvalidNonce) refuse the request itself. Any other
            //failed operation (network errors, unreadable responses, Binance's -1000/-1001/-1006 "execution status unknown") may have
            //happened after the exchange accepted it
            catch (ccxt.OperationFailed e) when (e is not ccxt.RateLimitExceeded && e is not ccxt.DDoSProtection && e is not ccxt.InvalidNonce)
            {
                throw new OrderStateUnknownException(sent, $"Creating {side} {type} order {clientOrderId} on {marketSymbol} failed without an answer: {e.Message}");
            }
            catch (ccxt.BaseError e)
            {
                throw ToApiException(e);
            }
            //ccxt's fetch wraps only HttpRequestException/WebException/HttpListenerException; a timeout, a broken response stream
            //or a response ccxt can't parse escapes as is
            catch (Exception e)
            {
                throw new OrderStateUnknownException(sent, $"Creating {side} {type} order {clientOrderId} on {marketSymbol} failed without an answer: {e.GetType().Name}: {e.Message}");
            }

            if (order.status == null || order.id == null)
                throw new OrderStateUnknownException(sent, $"Order {clientOrderId} on {marketSymbol} was acknowledged without a status or id (id: {order.id}, status: {order.status})");

            try
            {
                return ToOrderData(order);
            }
            //The exchange accepted it: failing as a plain error would make the caller place it again
            catch (Exception e)
            {
                throw new OrderStateUnknownException(sent, $"Order {clientOrderId} on {marketSymbol} was acknowledged with an answer that can't be mapped: {e.GetType().Name}: {e.Message}");
            }
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
                var reportOnFirstUpdate = false;

                void reportInterruption(string reason)
                {
                    Logger.Instance.LogMessage($"{name} stream {reason}");
                    foreach (var callback in onStreamInterruptedCallbacks)
                        callback();
                }

                while (!streams.IsCancellationRequested)
                {
                    try
                    {
                        var receiving = receive();

                        //The order and balance streams may next deliver much later, so report the interruption once the stream had time to
                        //subscribe again: a resync from then on catches what it missed
                        if (interrupted)
                        {
                            await Task.WhenAny(receiving, Task.Delay(ResubscribeGrace, streams.Token));

                            if (!receiving.IsFaulted && !streams.IsCancellationRequested)
                            {
                                interrupted = false;
                                //Only an update proves the subscription is live, so it reports once more
                                reportOnFirstUpdate = !receiving.IsCompleted;
                                reportInterruption("recovered");
                            }
                        }

                        await receiving;

                        if (reportOnFirstUpdate)
                        {
                            reportOnFirstUpdate = false;
                            reportInterruption("delivered its first update since recovering");
                        }
                    }
                    catch (Exception e) when (!streams.IsCancellationRequested)
                    {
                        interrupted = true;
                        reportOnFirstUpdate = false;
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
            //ccxt's fetch wraps only HttpRequestException/WebException/HttpListenerException; a timeout or a broken response stream
            //is what its RequestTimeout/NetworkError would be
            catch (Exception e) when (e is OperationCanceledException || e is IOException)
            {
                throw new ApiException(ApiErrorType.RetryLater, $"{e.GetType().Name}: {e.Message}");
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
                ccxt.MarketClosed => ApiErrorType.MarketOffline,
                //The account can't trade until someone acts: retrying won't help
                ccxt.RestrictedLocation => ApiErrorType.Unauthorized,
                ccxt.ManualInteractionNeeded => ApiErrorType.Unauthorized,
                //e.g. Binance's -1004 "server is busy"
                ccxt.OperationRejected => ApiErrorType.RetryLater,
                _ => ApiErrorType.UnknownError
            };

            return new ApiException(type, error.Message);
        }

        //Binance filter failures for minimum notional / lot size, e.g. "Filter failure: NOTIONAL" or "Order's notional must be no smaller than ...";
        //ccxt's own checks before sending: "amount of X must be greater than minimum amount precision of ..." when the amount truncates
        //to zero, and Binance's "Price * QTY is zero or less", which ccxt rewords as "order cost = amount * price is zero or less"
        private static readonly string[] BelowMinimumMarkers = { "NOTIONAL", "LOT_SIZE", "minimum amount precision", "amount * price is zero or less", "Price * QTY is zero or less" };

        private static bool IsBelowMinimum(string message) =>
            message != null && BelowMinimumMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

        internal static OrderData ToOrderData(ccxt.Order order)
        {
            var (coin, quote) = SplitSymbol(order.symbol);
            var fees = Fees(order);

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

        //No tick rather than a zero one: a merge would let a zero overwrite a good tick, and the strategies can't move prices by it
        private decimal? PriceTick(double precision)
        {
            //Tick-size mode gives the tick itself; the other modes give a number of decimal places
            var tick = Convert.ToInt32(exchange.precisionMode) == TickSizePrecisionMode
                ? ToDecimal(precision)
                : ToDecimal(1 / Math.Pow(10, precision));

            return tick > 0 ? tick : null;
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
