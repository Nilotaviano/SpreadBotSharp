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
        private int resyncInProgress;

        //I know it's not a Semaphore, but it works like one
        private readonly System.Threading.ManualResetEvent consumeDataSemaphore = new System.Threading.ManualResetEvent(true);

        private readonly BlockingCollection<BalanceData> pendingBalanceMessages = new BlockingCollection<BalanceData>();
        private readonly BlockingCollection<IEnumerable<MarketData>> pendingMarketMessages = new BlockingCollection<IEnumerable<MarketData>>();
        private readonly BlockingCollection<OrderData> pendingOrderMessages = new BlockingCollection<OrderData>();

        public DataRepository(IExchange exchange, AppSettings appSettings)
        {
            if (!exchange.IsSetup)
                throw new ArgumentException("Exchange is not setup");

            Exchange = exchange;
            resyncTimer = new Timer(appSettings.ResyncIntervalMs);
            resyncTimer.Elapsed += (sender, e) => Resync();
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
        /// Subscribe to a specific order by its client order id
        /// </summary>
        public void SubscribeToOrderData(string clientOrderId, Guid handlerGuid, Action<OrderData> callback)
        {
            var handlers = OrderHandlers.GetOrAdd(clientOrderId, _ => new ConcurrentDictionary<Guid, Action<OrderData>>());
            handlers[handlerGuid] = callback;

            //If there is already data for the order, fire callback
            if (OrdersData.TryGetValue(clientOrderId, out var order))
                callback(order);
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
                    OrderHandlers.Remove(clientOrderId, out _);
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

            FetchAllData().Wait();

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

        private async Task FetchAllData()
        {
            var balances = Exchange.GetBalances();
            var markets = Exchange.GetMarkets();
            var tickers = Exchange.GetTickers();
            var openOrders = Exchange.GetOpenOrders();

            await Task.WhenAll(balances, markets, tickers, openOrders);

            foreach (var balance in balances.Result)
                UpdateBalanceData(balance);

            UpdateMarketData(markets.Result);
            UpdateMarketData(tickers.Result);

            foreach (var order in openOrders.Result)
                UpdateOrderData(order);

            await FetchTrackedOrdersNoLongerOpen(openOrders.Result);
        }

        /// <summary>
        /// Orders a bot is waiting on that the exchange no longer lists as open have filled or been cancelled while
        /// the stream wasn't delivering; fetch each one for its final state.
        /// </summary>
        private async Task FetchTrackedOrdersNoLongerOpen(IReadOnlyList<OrderData> openOrders)
        {
            var openIds = new HashSet<string>(openOrders.Select(o => o.ClientOrderId));
            var missing = OrdersData.Values
                .Where(o => o.Status == OrderStatus.OPEN && !openIds.Contains(o.ClientOrderId) && OrderHandlers.ContainsKey(o.ClientOrderId))
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

        private void RequestResync()
        {
            resyncTimer.Stop();
            Task.Run(Resync);
        }

        private void Resync()
        {
            if (System.Threading.Interlocked.Exchange(ref resyncInProgress, 1) == 1)
                return;

            try
            {
                Logger.Instance.LogMessage("Resyncing data");

                //Pauses all consumers that call WaitOne()
                consumeDataSemaphore.Reset();

                FetchAllData().Wait();

                Logger.Instance.LogMessage("Resync completed");
            }
            catch (Exception ex)
            {
                Logger.Instance.LogUnexpectedError($"Error while resyncing: {ex}");
            }
            finally
            {
                consumeDataSemaphore.Set();
                System.Threading.Interlocked.Exchange(ref resyncInProgress, 0);
                resyncTimer.Start();
            }
        }
    }
}
