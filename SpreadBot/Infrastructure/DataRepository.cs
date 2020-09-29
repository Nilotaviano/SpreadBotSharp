using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models.API;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    /*
     * TODO list:
     * Implement Rest API redundancy (make API calls every X minutes so that we have a fallback in case the Websocket becomes unreliable)
     */
    public class DataRepository
    {
        private int? lastBalanceSequence;
        private int? lastSummarySequence;
        private int? lastTickerSequence;
        private int? lastOrderSequence;

        private DateTime lastOrderSnapshot;

        public DataRepository(IExchange exchange)
        {
            if (!exchange.IsSetup)
                throw new ArgumentException("Exchange is not setup");

            pendingBalanceMessages = new BlockingCollection<ApiBalanceData>();
            pendingMarketSummaryMessages = new BlockingCollection<ApiMarketSummariesData>();
            pendingOrderMessages = new BlockingCollection<ApiOrderData>();
            pendingTickersMessages = new BlockingCollection<ApiTickersData>();

            Exchange = exchange;

            lastOrderSnapshot = DateTime.UtcNow;
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
        // key: market symbol, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<MarketData>>> MarketHandlers { get; set; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<MarketData>>>();
        // key: order id, value: handlers dictionary indexed by a Guid — which will be used for unsubscribing
        private ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<OrderData>>> OrderHandlers { get; set; } = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<OrderData>>>();

        private BlockingCollection<ApiBalanceData> pendingBalanceMessages;
        private BlockingCollection<ApiMarketSummariesData> pendingMarketSummaryMessages;
        private BlockingCollection<ApiOrderData> pendingOrderMessages;
        private BlockingCollection<ApiTickersData> pendingTickersMessages;

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
            if (!MarketHandlers.TryGetValue(marketName, out ConcurrentDictionary<Guid, Action<MarketData>> handlers))
                MarketHandlers[marketName] = handlers = new ConcurrentDictionary<Guid, Action<MarketData>>();

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
            if (!MarketHandlers.TryGetValue(marketName, out ConcurrentDictionary<Guid, Action<MarketData>> handlers))
                MarketHandlers[marketName] = handlers = new ConcurrentDictionary<Guid, Action<MarketData>>();

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
            Exchange.OnBalance(pendingBalanceMessages.Add);
            Exchange.OnSummaries(pendingMarketSummaryMessages.Add);
            Exchange.OnTickers(pendingTickersMessages.Add);
            Exchange.OnOrder(pendingOrderMessages.Add);

            FetchAllData();

            Task.Run(ConsumeBalanceData);
            Task.Run(ConsumeOrderData);
            Task.Run(ConsumeMarketSummaryData);
            Task.Run(ConsumeTickersData);
        }

        private void ConsumeBalanceData()
        {
            foreach (var balanceData in pendingBalanceMessages.GetConsumingEnumerable())
            {
                if (lastBalanceSequence.HasValue && balanceData.Sequence != lastBalanceSequence.Value + 1)
                    continue;

                var balance = new BalanceData(balanceData.Delta);

                this.BalancesData[balance.CurrencyAbbreviation] = balance;

                InvokeHandlers(this.BalanceHandlers, balance.CurrencyAbbreviation, balance);

                lastBalanceSequence = balanceData.Sequence;
            }
        }

        private void ConsumeOrderData()
        {
            foreach (var orderData in pendingOrderMessages.GetConsumingEnumerable())
            {
                if (lastOrderSequence.HasValue && orderData.Sequence != lastOrderSequence.Value + 1)
                    continue;

                var data = new OrderData(orderData);

                InvokeHandlers(this.OrderHandlers, data.Id, data);

                lastOrderSequence = orderData.Sequence;
            }
        }

        private void ConsumeMarketSummaryData()
        {
            foreach (var summaryData in pendingMarketSummaryMessages.GetConsumingEnumerable())
            {
                if (lastSummarySequence.HasValue && summaryData.Sequence != lastSummarySequence.Value + 1)
                    continue;

                var marketData = summaryData.Deltas.Select(delta => new MarketData(delta));

                UpdateMarketData(marketData);

                lastSummarySequence = summaryData.Sequence;
            }
        }

        private void ConsumeTickersData()
        {
            foreach (var tickersData in pendingTickersMessages.GetConsumingEnumerable())
            {
                if (lastTickerSequence.HasValue && tickersData.Sequence != lastTickerSequence.Value + 1)
                    continue;

                var marketData = tickersData.Deltas.Select(delta => new MarketData(delta));

                UpdateMarketData(marketData);

                lastTickerSequence = tickersData.Sequence;
            }
        }

        private void UpdateMarketData(IEnumerable<MarketData> marketData)
        {
            marketData.AsParallel().ForAll(UpdateMarketData);
        }

        private void UpdateMarketData(MarketData data)
        {
            var newData = this.MarketsData.AddOrUpdate(data.Symbol,
                                                        data,
                                                        (key, existingData) => this.MergeMarketData(existingData, data));
            InvokeHandlers(this.MarketHandlers, data.Symbol, newData);
        }

        private void InvokeHandlers<T>(ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<T>>> handlersDict, string key, T data)
        {
            var handlers = handlersDict.GetOrAdd(key, new ConcurrentDictionary<Guid, Action<T>>());

            handlers.Values.AsParallel()
                           .ForAll(handler => handler?.Invoke(data));
        }

        private void FetchAllData()
        {
            Task.Run(FetchBalanceData);
            Task.Run(FetchMarketSummariesData);
            Task.Run(FetchTickersData);

            // TODO Signature not working
            Task.Run(FetchClosedOrdersData);
        }

        private async Task FetchBalanceData()
        {
            var balances = await Exchange.GetBalanceData();

            balances.Balances?.AsParallel().ForAll(balance =>
            {
                BalancesData[balance.CurrencyAbbreviation] = balance;
                InvokeHandlers(BalanceHandlers, balance.CurrencyAbbreviation, balance);
            });

            lastBalanceSequence = balances.Sequence;
        }

        private async Task FetchMarketSummariesData()
        {
            var summaries = await Exchange.GetMarketSummariesData();

            summaries.Deltas?.AsParallel().ForAll(summary =>
            {
                var marketData = new MarketData(summary);

                UpdateMarketData(marketData);
            });

            lastSummarySequence = summaries.Sequence;
        }

        private async Task FetchTickersData()
        {
            var tickers = await Exchange.GetTickersData();

            tickers.Deltas?.AsParallel().ForAll(ticker =>
            {
                var marketData = new MarketData(ticker);

                UpdateMarketData(marketData);
            });

            lastSummarySequence = tickers.Sequence;
        }

        private async Task FetchClosedOrdersData()
        {
            var closedOrders = await Exchange.GetClosedOrdersData(lastOrderSnapshot);

            closedOrders.Data?.AsParallel().ForAll(order =>
            {
                var orderData = new OrderData(order);
                InvokeHandlers(OrderHandlers, orderData.Id, orderData);
            });

            // There is not sequence information in ClosedOrders
            // lastOrderSequence = closedOrders.Sequence;
            lastOrderSnapshot = DateTime.UtcNow;
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
                Symbol = data.Symbol
            };
        }

        internal void SubscribeToMarketsData(Action<IEnumerable<MarketData>> evaluateMarkets)
        {
            throw new NotImplementedException();
        }
    }
}
