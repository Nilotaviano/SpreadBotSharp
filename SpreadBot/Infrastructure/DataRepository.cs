using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models;
using SpreadBot.Models.API;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    public class DataRepository
    {
        private readonly IExchange _exchange;

        public DataRepository(IExchange exchange)
        {
            if (!exchange.IsSetup)
                throw new ArgumentException("Exchange is not setup");

            _exchange = exchange;

            _exchange.OnBalance(UpdateBalance);
            _exchange.OnSummaries(UpdateSummaries);
            _exchange.OnTickers(UpdateTickers);
            _exchange.OnOrder(UpdateOrder);
        }

        public ConcurrentDictionary<string, BalanceData> BalancesData { get; private set; } = new ConcurrentDictionary<string, BalanceData>();
        public ConcurrentDictionary<string, MarketData> MarketsData { get; private set; } = new ConcurrentDictionary<string, MarketData>();
        public ConcurrentDictionary<string, OrderData> OrdersData { get; private set; } = new ConcurrentDictionary<string, OrderData>();

        private ConcurrentDictionary<string, List<Action<BalanceData>>> BalanceHandlers { get; set; } = new ConcurrentDictionary<string, List<Action<BalanceData>>>();
        private ConcurrentDictionary<string, List<Action<MarketData>>> MarketHandlers { get; set; } = new ConcurrentDictionary<string, List<Action<MarketData>>>();
        private ConcurrentDictionary<string, List<Action<OrderData>>> OrderHandlers { get; set; } = new ConcurrentDictionary<string, List<Action<OrderData>>>();


        //TODO: Unsubscribing

        /// <summary>
        /// Subscribe to all balance changes
        /// </summary>
        public void SubscribeToBalances(Action<IEnumerable<BalanceData>> callback)
        {

        }
        /// <summary>
        /// Subscribe to all market changes
        /// </summary>
        public void SubscribeToMarketsData(Action<IEnumerable<MarketData>> callback)
        {

        }
        /// <summary>
        /// Subscribe to all order changes
        /// </summary>
        public void SubscribeToOrdersData(Action<IEnumerable<OrderData>> callback)
        {

        }

        /// <summary>
        /// Subscribe to a specific currency balance by currencyName
        /// </summary>
        public void SubscribeToCurrencyBalance(string currencyName, Action<BalanceData> callback)
        {
            List<Action<BalanceData>> handlers;

            if (!BalanceHandlers.TryGetValue(currencyName, out handlers))
                BalanceHandlers[currencyName] = handlers = new List<Action<BalanceData>>();

            handlers.Add(callback);

            //If there is already data for the currency balance, fire callback
            if (BalancesData.ContainsKey(currencyName))
                callback(BalancesData[currencyName]);
        }

        /// <summary>
        /// Subscribe to a specific market by marketName
        /// </summary>
        public void SubscribeToMarketData(string marketName, Action<MarketData> callback)
        {
            List<Action<MarketData>> handlers;

            if (!MarketHandlers.TryGetValue(marketName, out handlers))
                MarketHandlers[marketName] = handlers = new List<Action<MarketData>>();

            handlers.Add(callback);

            //If there is already data for the market, fire callback
            if (MarketsData.ContainsKey(marketName))
                callback(MarketsData[marketName]);
        }

        /// <summary>
        /// Subscribe to a specific order by orderId
        /// </summary>
        public void SubscribeToOrderData(string orderId, Action<OrderData> callback)
        {
            List<Action<OrderData>> handlers;

            if (!OrderHandlers.TryGetValue(orderId, out handlers))
                OrderHandlers[orderId] = handlers = new List<Action<OrderData>>();

            handlers.Add(callback);

            //If there is already data for the order, fire callback
            if (OrdersData.ContainsKey(orderId))
                callback(OrdersData[orderId]);
        }

        private void UpdateBalance(ApiBalanceData balanceData)
        {
            throw new NotImplementedException();
        }

        private void UpdateSummaries(ApiMarketSummariesData balanceData)
        {
            throw new NotImplementedException();
        }

        private void UpdateTickers(ApiTickersData balanceData)
        {
            throw new NotImplementedException();
        }

        private void UpdateOrder(ApiOrderData orderData)
        {
            throw new NotImplementedException();
        }
    }
}
