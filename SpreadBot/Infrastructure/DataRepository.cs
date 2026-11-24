using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

namespace SpreadBot.Infrastructure
{
    public class DataRepository
    {
        private readonly Timer resyncTimer;
        //Resyncs requested and not yet covered by a run; only the request that finds none runs them
        private int pendingResyncs;

        //I know it's not a Semaphore, but it works like one
        private readonly System.Threading.ManualResetEvent consumeDataSemaphore = new System.Threading.ManualResetEvent(true);

        private readonly BlockingCollection<BalanceData> pendingBalanceMessages = new BlockingCollection<BalanceData>();
        private readonly BlockingCollection<IEnumerable<MarketData>> pendingMarketMessages = new BlockingCollection<IEnumerable<MarketData>>();
        private readonly BlockingCollection<OrderData> pendingOrderMessages = new BlockingCollection<OrderData>();
        private readonly ConcurrentDictionary<string, bool> reportedUntrackedOrders = new ConcurrentDictionary<string, bool>();

        public DataRepository(IExchange exchange, AppSettings appSettings)
        {
            if (!exchange.IsSetup)
                throw new ArgumentException("Exchange is not setup");

            Exchange = exchange;
            resyncTimer = new Timer(appSettings.ResyncIntervalMs);
            resyncTimer.Elapsed += (sender, e) => RequestResync();
            resyncTimer.AutoReset = false;
        }

        public IExchange Exchange { get; }

        /// <summary>
        /// BalanceData dictionary indexed by CurrencyAbbreviation
        /// </summary>
        public ConcurrentDictionary<string, BalanceData> BalancesData { get; } = new ConcurrentDictionary<string, BalanceData>();

        /// <summary>
        /// MarketData dictionary indexed by Symbol
        /// </summary>
        public ConcurrentDictionary<string, MarketData> MarketsData { get; } = new ConcurrentDictionary<string, MarketData>();

        /// <summary>
        /// OrderData dictionary indexed by ClientOrderId
        /// </summary>
        public ConcurrentDictionary<string, OrderData> OrdersData { get; } = new ConcurrentDictionary<string, OrderData>();

        // key: currency abbreviation, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<BalanceData>>> BalanceHandlers { get; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<BalanceData>>>();
        // key: Guid — which will be used for unsubscribing, value: handlers
        private ConcurrentDictionary<Guid, Action<IEnumerable<MarketData>>> AllMarketHandlers { get; } = new ConcurrentDictionary<Guid, Action<IEnumerable<MarketData>>>();
        // key: market symbol, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<MarketData>>> SpecificMarketHandlers { get; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<MarketData>>>();
        // key: client order id, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<OrderData>>> OrderHandlers { get; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<OrderData>>>();
        // key: client order id, value: the subscribed order — what a resync needs (id and symbol) to fetch it even if no update was ever received
        private ConcurrentDictionary<string, OrderData> TrackedOrders { get; } = new ConcurrentDictionary<string, OrderData>();

        public void SubscribeToMarketsData(Guid handlerGuid, Action<IEnumerable<MarketData>> evaluateMarkets)
        {
            AllMarketHandlers[handlerGuid] = evaluateMarkets;

            if (MarketsData.Count > 0)
                evaluateMarkets(MarketsData.Values);
        }

        /// <summary>
        /// Subscribe to a specific currency balance by currencyName
        /// </summary>
        public void SubscribeToCurrencyBalance(string currencyName, Guid handlerGuid, Action<BalanceData> callback)
        {
            var handlers = BalanceHandlers.GetOrAdd(currencyName, _ => new ConcurrentDictionary<Guid, Action<BalanceData>>());
            handlers[handlerGuid] = callback;

            //If there is already data for the currency balance, fire callback
            if (BalancesData.TryGetValue(currencyName, out var balance))
                callback(balance);
        }

        /// <summary>
        /// Subscribe to a specific market by marketName
        /// </summary>
        public void SubscribeToMarketData(string marketName, Guid handlerGuid, Action<MarketData> callback)
        {
            var handlers = SpecificMarketHandlers.GetOrAdd(marketName, _ => new ConcurrentDictionary<Guid, Action<MarketData>>());
            handlers[handlerGuid] = callback;

            //If there is already data for the market, fire callback
            if (MarketsData.TryGetValue(marketName, out var market))
                callback(market);
        }

        /// <summary>
        /// Subscribe to a specific order; resyncs keep fetching it until it closes or is unsubscribed
        /// </summary>
        public void SubscribeToOrderData(OrderData order, Guid handlerGuid, Action<OrderData> callback)
        {
            var clientOrderId = order.ClientOrderId;

            if (clientOrderId == null)
            {
                Logger.Instance.LogUnexpectedError($"Can't subscribe to order {order.Id} on {order.MarketSymbol}: it has no client order id");
                return;
            }

            //An order whose placement is unknown has no id to fetch; until it has one, only the stream and resyncs' open orders reach it
            if (order.Id != null && order.MarketSymbol != null)
                TrackedOrders[clientOrderId] = order;

            var handlers = OrderHandlers.GetOrAdd(clientOrderId, _ => new ConcurrentDictionary<Guid, Action<OrderData>>());
            handlers[handlerGuid] = callback;

            //If there is already data for the order, fire callback
            if (OrdersData.TryGetValue(clientOrderId, out var cached))
                callback(cached);
        }

        public void UnsubscribeToMarketsData(Guid handlerGuid)
        {
            AllMarketHandlers.Remove(handlerGuid, out _);
        }

        public void UnsubscribeToCurrencyBalance(string currencyName, Guid handlerGuid)
        {
            if (BalanceHandlers.TryGetValue(currencyName, out var handlers))
                handlers.Remove(handlerGuid, out _);
        }

        public void UnsubscribeToMarketData(string marketName, Guid handlerGuid)
        {
            if (SpecificMarketHandlers.TryGetValue(marketName, out var handlers))
                handlers.Remove(handlerGuid, out _);
        }

        public void UnsubscribeToOrderData(string clientOrderId, Guid handlerGuid)
        {
            if (OrderHandlers.TryGetValue(clientOrderId, out var handlers))
            {
                handlers.Remove(handlerGuid, out _);

                if (handlers.Count == 0)
                {
                    OrderHandlers.Remove(clientOrderId, out _);
                    TrackedOrders.Remove(clientOrderId, out _);
                }
            }

            OrdersData.Remove(clientOrderId, out _);
        }

        public void StartConsumingData()
        {
            Logger.Instance.LogMessage("Start consuming exchange data");

            Exchange.OnBalance(pendingBalanceMessages.Add);
            Exchange.OnMarketData(pendingMarketMessages.Add);
            Exchange.OnOrder(pendingOrderMessages.Add);
            Exchange.OnStreamInterrupted(RequestResync);

            FetchAllData(Exchange.GetMarkets().Result).Wait();

            resyncTimer.Start();

            StartConsumer(pendingBalanceMessages, UpdateBalanceData);
            StartConsumer(pendingMarketMessages, UpdateMarketData);
            StartConsumer(pendingOrderMessages, UpdateOrderData);
        }

        private void StartConsumer<T>(BlockingCollection<T> queue, Action<T> consumeAction)
        {
            Task.Factory.StartNew(() => ConsumeData(queue, consumeAction), System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void ConsumeData<T>(BlockingCollection<T> queue, Action<T> consumeAction)
        {
            var typeName = typeof(T).Name;
            Logger.Instance.LogMessage($"Start {typeName} stream consumption");

            foreach (var data in queue.GetConsumingEnumerable())
            {
                try
                {
                    consumeDataSemaphore.WaitOne();

                    consumeAction(data);
                }
                catch (Exception e)
                {
                    Logger.Instance.LogUnexpectedError($"Error consuming {typeName} stream data: {e}");
                }
            }

            Logger.Instance.LogUnexpectedError($"Stopped {typeName} stream consumption");
        }

        private void UpdateBalanceData(BalanceData balance)
        {
            BalancesData[balance.CurrencyAbbreviation] = balance;
            InvokeHandlers(BalanceHandlers, balance.CurrencyAbbreviation, balance);
        }

        private void UpdateOrderData(OrderData order)
        {
            //A closed order is final; a late open update must not reopen it
            var current = OrdersData.AddOrUpdate(order.ClientOrderId, order, (id, existing) => existing.Status == OrderStatus.CLOSED ? existing : order);

            InvokeHandlers(OrderHandlers, order.ClientOrderId, current);
        }

        private void UpdateMarketData(IEnumerable<MarketData> marketData)
        {
            var updated = marketData
                .Select(data => MarketsData.AddOrUpdate(data.Symbol, data, (key, existingData) => MergeMarketData(existingData, data)))
                .ToList();

            foreach (var market in updated)
                InvokeHandlers(SpecificMarketHandlers, market.Symbol, market);

            InvokeHandlers(AllMarketHandlers, updated);
        }

        private static void InvokeHandlers<T>(ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<T>>> handlersDict, string key, T data)
        {
            if (handlersDict.TryGetValue(key, out var handlers))
                InvokeHandlers(handlers, data);
        }

        private static void InvokeHandlers<T>(ConcurrentDictionary<Guid, Action<T>> handlers, T data)
        {
            foreach (var handler in handlers.Values)
                handler?.Invoke(data);
        }

        /// <param name="markets">Fetched by the caller first: a markets reload stalls the exchange's other requests, which would fail
        /// with it, and it can take a request timeout, which mustn't keep the consumers paused</param>
        private async Task FetchAllData(IReadOnlyList<MarketData> markets)
        {
            var balances = Exchange.GetBalances();
            var tickers = Exchange.GetTickers();
            var openOrders = Exchange.GetOpenOrders();

            await Task.WhenAll(balances, tickers, openOrders);

            foreach (var balance in balances.Result)
                UpdateBalanceData(balance);

            UpdateMarketData(markets);
            UpdateMarketData(tickers.Result);

            foreach (var order in openOrders.Result)
                UpdateOrderData(order);

            await FetchTrackedOrdersNoLongerOpen(openOrders.Result);

            var untracked = openOrders.Result
                .Where(o => !OrderHandlers.ContainsKey(o.ClientOrderId) && reportedUntrackedOrders.TryAdd(o.ClientOrderId, true))
                .ToList();
            if (untracked.Count > 0)
                Logger.Instance.LogError($"Open orders no bot tracks (placed manually, or left by a failed placement): {string.Join(", ", untracked.Select(o => $"{o.MarketSymbol} {o.Direction} {o.Id}"))}");
        }

        /// <summary>
        /// Orders a bot is waiting on that the exchange no longer lists as open have filled or been cancelled while
        /// the stream wasn't delivering; fetch each one for its final state.
        /// </summary>
        private async Task FetchTrackedOrdersNoLongerOpen(IReadOnlyList<OrderData> openOrders)
        {
            var openIds = new HashSet<string>(openOrders.Select(o => o.ClientOrderId));
            var missing = TrackedOrders.Values
                .Where(o => !openIds.Contains(o.ClientOrderId))
                .Where(o => !OrdersData.TryGetValue(o.ClientOrderId, out var known) || known.Status != OrderStatus.CLOSED)
                .ToList();

            foreach (var order in missing)
            {
                try
                {
                    UpdateOrderData(await Exchange.GetOrder(order.Id, order.MarketSymbol));
                }
                catch (Exception e)
                {
                    Logger.Instance.LogError($"Could not fetch order {order.Id} on {order.MarketSymbol} during resync: {e.Message}");
                }
            }
        }

        private static MarketData MergeMarketData(MarketData existingData, MarketData data)
        {
            return new MarketData
            {
                AskRate = data.AskRate ?? existingData.AskRate,
                BidRate = data.BidRate ?? existingData.BidRate,
                High = data.High ?? existingData.High,
                LastTradeRate = data.LastTradeRate ?? existingData.LastTradeRate,
                Low = data.Low ?? existingData.Low,
                PercentChange = data.PercentChange ?? existingData.PercentChange,
                QuoteVolume = data.QuoteVolume ?? existingData.QuoteVolume,
                UpdatedAt = data.UpdatedAt ?? existingData.UpdatedAt,
                Volume = data.Volume ?? existingData.Volume,
                Symbol = data.Symbol ?? existingData.Symbol,
                PriceTick = data.PriceTick ?? existingData.PriceTick,
                MinTradeSize = data.MinTradeSize ?? existingData.MinTradeSize,
                Notice = data.Notice ?? existingData.Notice,
                CreatedAt = data.CreatedAt ?? existingData.CreatedAt,
                Status = data.Status ?? existingData.Status,
                IsTokenizedSecurity = data.IsTokenizedSecurity ?? existingData.IsTokenizedSecurity
            };
        }

        /// <summary>
        /// One resync runs at a time. A request made while one runs (e.g. a stream recovering after the running one took its
        /// snapshot) gets one more run after it; requests made during the same run share it.
        /// </summary>
        private void RequestResync()
        {
            resyncTimer.Stop();

            //Counted here rather than in the task, so a running resync sees the request before it finishes
            if (System.Threading.Interlocked.Increment(ref pendingResyncs) == 1)
                Task.Run(RunPendingResyncs);
        }

        private void RunPendingResyncs()
        {
            try
            {
                int covered;
                do
                {
                    covered = System.Threading.Volatile.Read(ref pendingResyncs);
                    ResyncOnce();
                }
                while (System.Threading.Interlocked.Add(ref pendingResyncs, -covered) != 0);
            }
            //Only the logger can throw here, and nothing observes this task. Left above zero, the count would make every later request
            //wait for a run that never comes; requests it drops are covered by the timer's next resync
            catch (Exception e)
            {
                System.Threading.Interlocked.Exchange(ref pendingResyncs, 0);

                try
                {
                    Logger.Instance.LogUnexpectedError($"Resyncs stopped unexpectedly; the next one waits for the timer: {e}");
                }
                catch
                {
                    //The logger is what failed
                }
            }
            finally
            {
                resyncTimer.Start();
            }
        }

        private void ResyncOnce()
        {
            try
            {
                Logger.Instance.LogMessage("Resyncing data");

                var markets = Exchange.GetMarkets().Result;

                //Pauses all consumers that call WaitOne()
                consumeDataSemaphore.Reset();

                FetchAllData(markets).Wait();

                Logger.Instance.LogMessage("Resync completed");
            }
            catch (Exception ex)
            {
                Logger.Instance.LogUnexpectedError($"Error while resyncing: {ex}");
            }
            finally
            {
                consumeDataSemaphore.Set();
            }
        }
    }
}
