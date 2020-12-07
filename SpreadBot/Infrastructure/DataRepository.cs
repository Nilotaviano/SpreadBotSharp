using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

namespace SpreadBot.Infrastructure
{
    /*
     * TODO list:
     * Implement Rest API redundancy (make API calls every X minutes so that we have a fallback in case the Websocket becomes unreliable)
     */
    public class DataRepository
    {
        private Timer resyncTimer;
        private int? lastBalanceSequence;
        private int? lastSummarySequence;
        private int? lastTickerSequence;
        private int? lastOrderSequence;

        //I know it's not a Semaphore, but it works like one
        private readonly System.Threading.ManualResetEvent consumeDataSemaphore = new System.Threading.ManualResetEvent(true);

        private string mostRecentClosedOrderId;

        public DataRepository(IExchange exchange, AppSettings appSettings)
        {
            if (!exchange.IsSetup)
                throw new ArgumentException("Exchange is not setup");

            pendingBalanceMessages = new BlockingCollection<BittrexApiBalanceData>();
            pendingMarketSummaryMessages = new BlockingCollection<BittrexApiMarketSummariesData>();
            pendingOrderMessages = new BlockingCollection<BittrexApiOrderData>();
            pendingTickersMessages = new BlockingCollection<BittrexApiTickersData>();

            Exchange = exchange;

            mostRecentClosedOrderId = null;
            resyncTimer = new Timer(appSettings.ResyncIntervalMs);
            resyncTimer.Elapsed += ResyncTimer_Elapsed;
            resyncTimer.AutoReset = true;
        }

        public IExchange Exchange { get; }

        /// <summary>
        /// BalanceData dictionary indexed by CurrencyAbbreviation
        /// </summary>
        public ConcurrentDictionary<string, BalanceData> BalancesData { get; private set; } = new ConcurrentDictionary<string, BalanceData>();
        /// <summary>
        /// MarketData dictionary indexed by Symbol
        /// </summary>
        public ConcurrentDictionary<string, MarketData> MarketsData { get; private set; } = new ConcurrentDictionary<string, MarketData>();

        // key: currency abbreviation, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<BalanceData>>> BalanceHandlers { get; set; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<BalanceData>>>();
        // key: Guid — which will be used for unsubscribing, value: handlers 
        private ConcurrentDictionary<Guid, Action<IEnumerable<MarketData>>> AllMarketHandlers { get; set; } = new ConcurrentDictionary<Guid, Action<IEnumerable<MarketData>>>();
        // key: market symbol, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<MarketData>>> SpecificMarketHandlers { get; set; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<MarketData>>>();
        // key: order id, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<OrderData>>> OrderHandlers { get; set; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<OrderData>>>();

        private BlockingCollection<BittrexApiBalanceData> pendingBalanceMessages;
        private BlockingCollection<BittrexApiMarketSummariesData> pendingMarketSummaryMessages;
        private BlockingCollection<BittrexApiOrderData> pendingOrderMessages;
        private BlockingCollection<BittrexApiTickersData> pendingTickersMessages;

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
            if (!BalanceHandlers.TryGetValue(currencyName, out ConcurrentDictionary<Guid, Action<BalanceData>> handlers))
                BalanceHandlers[currencyName] = handlers = new ConcurrentDictionary<Guid, Action<BalanceData>>();

            handlers[handlerGuid] = callback;

            //If there is already data for the currency balance, fire callback
            if (BalancesData.ContainsKey(currencyName))
                callback(BalancesData[currencyName]);
        }

        /// <summary>
        /// Subscribe to a specific market by marketName
        /// </summary>
        public void SubscribeToMarketData(string marketName, Guid handlerGuid, Action<MarketData> callback)
        {
            if (!SpecificMarketHandlers.TryGetValue(marketName, out ConcurrentDictionary<Guid, Action<MarketData>> handlers))
                SpecificMarketHandlers[marketName] = handlers = new ConcurrentDictionary<Guid, Action<MarketData>>();

            handlers[handlerGuid] = callback;

            //If there is already data for the market, fire callback
            if (MarketsData.ContainsKey(marketName))
                callback(MarketsData[marketName]);
        }

        /// <summary>
        /// Subscribe to a specific order by orderId
        /// </summary>
        public void SubscribeToOrderData(string orderId, Guid handlerGuid, Action<OrderData> callback)
        {
            if (!OrderHandlers.TryGetValue(orderId, out ConcurrentDictionary<Guid, Action<OrderData>> handlers))
                OrderHandlers[orderId] = handlers = new ConcurrentDictionary<Guid, Action<OrderData>>();

            handlers[handlerGuid] = callback;
        }

        public void UnsubscribeToMarketsData(Guid handlerGuid)
        {
            AllMarketHandlers.Remove(handlerGuid, out _);
        }

        /// <summary>
        /// Unsubscribe to a specific currency balance by currencyName
        /// </summary>
        public void UnsubscribeToCurrencyBalance(string currencyName, Guid handlerGuid)
        {
            if (!BalanceHandlers.TryGetValue(currencyName, out ConcurrentDictionary<Guid, Action<BalanceData>> handlers))
                BalanceHandlers[currencyName] = handlers = new ConcurrentDictionary<Guid, Action<BalanceData>>();

            handlers.Remove(handlerGuid, out _);
        }

        /// <summary>
        /// Unsubscribe to a specific market by marketName
        /// </summary>
        public void UnsubscribeToMarketData(string marketName, Guid handlerGuid)
        {
            if (!SpecificMarketHandlers.TryGetValue(marketName, out ConcurrentDictionary<Guid, Action<MarketData>> handlers))
                SpecificMarketHandlers[marketName] = handlers = new ConcurrentDictionary<Guid, Action<MarketData>>();

            handlers.Remove(handlerGuid, out _);
        }

        /// <summary>
        /// Unsubscribe to a specific order by orderId
        /// </summary>
        public void UnsubscribeToOrderData(string orderId, Guid handlerGuid)
        {
            if (!OrderHandlers.TryGetValue(orderId, out ConcurrentDictionary<Guid, Action<OrderData>> handlers))
                handlers.Remove(handlerGuid, out _);
        }

        public void StartConsumingData()
        {
            Logger.Instance.LogMessage("Start consuming WS data");

            Exchange.OnBalance(pendingBalanceMessages.Add);
            Exchange.OnSummaries(pendingMarketSummaryMessages.Add);
            Exchange.OnTickers(pendingTickersMessages.Add);
            Exchange.OnOrder(pendingOrderMessages.Add);

            FetchAllData();

            resyncTimer.Start();

            Task.Run(ConsumeBalanceData);
            Task.Run(ConsumeOrderData);
            Task.Run(ConsumeMarketSummaryData);
            Task.Run(ConsumeTickersData);
        }

        private void ResumeConsumingData()
        {
            Logger.Instance.LogMessage("Resume consuming WS data");

            //Resumes all threads that called WaitOne()
            consumeDataSemaphore.Set();
        }

        private void StopConsumingData()
        {
            Logger.Instance.LogMessage("Stop consuming WS data");

            //Pauses all threads that call WaitOne()
            consumeDataSemaphore.Reset();
        }

        private void ConsumeBalanceData()
        {
            ConsumeData(pendingBalanceMessages, balanceData =>
            {
                if (lastBalanceSequence.HasValue && balanceData.Sequence <= lastBalanceSequence.Value)
                {
                    Logger.Instance.LogMessage("Balance WS data skipped");
                    return;
                }

                var balance = new BalanceData(balanceData.Delta);

                this.BalancesData[balance.CurrencyAbbreviation] = balance;

                InvokeHandlers(this.BalanceHandlers, balance.CurrencyAbbreviation, balance);

                lastBalanceSequence = balanceData.Sequence;
            });
        }

        private void ConsumeOrderData()
        {
            ConsumeData(pendingOrderMessages, orderData =>
            {
                if (lastOrderSequence.HasValue && orderData.Sequence != lastOrderSequence.Value + 1)
                {
                    Logger.Instance.LogMessage("Order WS data skipped");
                    return;
                }

                var data = new OrderData(orderData);

                InvokeHandlers(this.OrderHandlers, data.Id, data);

                lastOrderSequence = orderData.Sequence;
            });
        }

        private void ConsumeMarketSummaryData()
        {
            ConsumeData(pendingMarketSummaryMessages, summaryData =>
            {
                if (lastSummarySequence.HasValue && summaryData.Sequence <= lastSummarySequence.Value)
                {
                    Logger.Instance.LogMessage("MarketSummary WS data skipped");
                    return;
                }

                var marketData = summaryData.Deltas.Select(delta => new MarketData(delta));

                UpdateMarketData(marketData);

                lastSummarySequence = summaryData.Sequence;
            });
        }

        private void ConsumeTickersData()
        {
            ConsumeData(pendingTickersMessages, tickersData =>
            {
                if (lastTickerSequence.HasValue && tickersData.Sequence <= lastTickerSequence.Value)
                {
                    Logger.Instance.LogMessage("Ticker WS data skipped");
                    return;
                }

                var marketData = tickersData.Deltas.Select(delta => new MarketData(delta));

                UpdateMarketData(marketData);

                lastTickerSequence = tickersData.Sequence;
            });
        }

        private void ConsumeData<T>(BlockingCollection<T> queue, Action<T> consumeAction)
        {
            var typeName = typeof(T).Name;
            Logger.Instance.LogMessage($"Start {typeName} WS consumption");

            foreach (var data in queue.GetConsumingEnumerable())
            {
                try
                {
                    consumeDataSemaphore.WaitOne();

                    consumeAction(data);
                }
                catch (Exception e)
                {
                    Logger.Instance.LogUnexpectedError($"Error consuming {typeName} WS data: {e}");
                }
            }

            Logger.Instance.LogUnexpectedError($"Stopped {typeName} WS data consumption");
        }

        private void UpdateMarketData(IEnumerable<MarketData> marketData)
        {
            foreach (var market in marketData)
                UpdateMarketData(market);
        }

        private void UpdateMarketData(MarketData data)
        {
            var newData = this.MarketsData.AddOrUpdate(data.Symbol,
                                                        data,
                                                        (key, existingData) => this.MergeMarketData(existingData, data));
            InvokeHandlers(this.SpecificMarketHandlers, data.Symbol, newData);

            InvokeHandlers(AllMarketHandlers, new MarketData[] { newData });
        }

        private void InvokeHandlers<T>(ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<T>>> handlersDict, string key, T data)
        {
            if (handlersDict.TryGetValue(key, out var handlers))
            {
                InvokeHandlers(handlers, data);
            }
        }

        private static void InvokeHandlers<T>(ConcurrentDictionary<Guid, Action<T>> handlers, T data)
        {
            foreach (var handler in handlers.Values)
                handler?.Invoke(data);
        }

        private void FetchAllData()
        {
            Task.WaitAll(
                FetchBalanceData(),
                FetchMarketSummariesData(),
                FetchTickersData(),
                FetchClosedOrdersData(),
                FetchMarketsData()
            );
        }

        private async Task FetchBalanceData()
        {
            //TODO: Handle exceptions here
            var balances = await Exchange.GetBalanceData();

            if (balances.Balances != null)
            {
                foreach (var balance in balances.Balances)
                {
                    BalancesData[balance.CurrencyAbbreviation] = balance;
                    InvokeHandlers(BalanceHandlers, balance.CurrencyAbbreviation, balance);
                }
            }

            lastBalanceSequence = balances.Sequence;
        }

        private async Task FetchMarketSummariesData()
        {
            //TODO: Handle exceptions here
            var summaries = await Exchange.GetMarketSummariesData();

            if (summaries.Deltas != null)
            {
                foreach (var summary in summaries.Deltas)
                {
                    var marketData = new MarketData(summary);
                    UpdateMarketData(marketData);
                }
            }

            lastSummarySequence = summaries.Sequence;
        }

        private async Task FetchTickersData()
        {
            //TODO: Handle exceptions here
            var tickers = await Exchange.GetTickersData();

            if (tickers.Deltas != null)
            {
                foreach (var ticker in tickers.Deltas)
                {
                    var marketData = new MarketData(ticker);
                    UpdateMarketData(marketData);
                }
            }

            lastTickerSequence = tickers.Sequence;
        }


        private async Task FetchMarketsData()
        {
            //TODO: Handle exceptions here
            var markets = await Exchange.GetMarketsData();

            if (markets != null)
            {
                foreach (var market in markets)
                {
                    var marketData = new MarketData(market);
                    UpdateMarketData(marketData);
                }
            }
        }

        private async Task FetchClosedOrdersData()
        {
            //TODO: Handle exceptions here
            var closedOrders = await Exchange.GetClosedOrdersData(mostRecentClosedOrderId);

            if (closedOrders.Data != null && closedOrders.Data.Any())
            {
                mostRecentClosedOrderId = closedOrders.Data?.FirstOrDefault()?.Id;

                foreach (var order in closedOrders.Data)
                {
                    var orderData = new OrderData(order);
                    InvokeHandlers(OrderHandlers, orderData.Id, orderData);
                }
            }

            // There is no sequence information in ClosedOrders
            lastOrderSequence = null;
        }

        private MarketData MergeMarketData(MarketData existingData, MarketData data)
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
                Precision = data.Precision ?? existingData.Precision,
                MinTradeSize = data.MinTradeSize ?? existingData.MinTradeSize,
                Notice = data.Notice ?? existingData.Notice,
                CreatedAt = data.CreatedAt ?? existingData.CreatedAt,
            };
        }

        private void ResyncTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                Logger.Instance.LogMessage("Resyncing data");

                StopConsumingData();

                FetchAllData();

                Logger.Instance.LogMessage("Resync completed");
            }
            catch (Exception ex)
            {
                Logger.Instance.LogUnexpectedError($"Error while resyncing: {ex}");
            }
            finally
            {
                ResumeConsumingData();
            }
        }
    }
}
