using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models;
using SpreadBot.Models.API;
using SpreadBot.Models.Repository;
using System;
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

        public Dictionary<string, BalanceData> BalancesData { get; private set; } = new Dictionary<string, BalanceData>();
        public Dictionary<string, MarketData> MarketsData { get; private set; } = new Dictionary<string, MarketData>();
        public Dictionary<string, OrderData> OrdersData { get; private set; } = new Dictionary<string, OrderData>();

        private Dictionary<string, List<Action>> BalanceHandlers { get; set; } = new Dictionary<string, List<Action>>();
        private Dictionary<string, List<Action>> MarketHandlers { get; set; } = new Dictionary<string, List<Action>>();
        private Dictionary<string, List<Action>> OrderHandlers { get; set; } = new Dictionary<string, List<Action>>();


        //TODO: Unsubscribing

        /// <summary>
        /// Subscribe to all balance changes
        /// </summary>
        public void SubscribeToBalances(Action callback)
        {

        }
        /// <summary>
        /// Subscribe to all market changes
        /// </summary>
        public void SubscribeToMarketsData(Action callback)
        {

        }
        /// <summary>
        /// Subscribe to all order changes
        /// </summary>
        public void SubscribeToOrdersData(Action callback)
        {

        }

        /// <summary>
        /// Subscribe to a specific currency balance by currencyName
        /// </summary>
        public void SubscribeToCurrencyBalance(string currencyName, Action callback)
        {
            List<Action> handlers;

            if (!BalanceHandlers.TryGetValue(currencyName, out handlers))
                BalanceHandlers[currencyName] = handlers = new List<Action>();

            handlers.Add(callback);

            //If there is already data for the currency balance, fire callback
            if (BalancesData.ContainsKey(currencyName))
                callback();
        }

        /// <summary>
        /// Subscribe to a specific market by marketName
        /// </summary>
        public void SubscribeToMarketData(string marketName, Action callback)
        {
            List<Action> handlers;

            if (!MarketHandlers.TryGetValue(marketName, out handlers))
                MarketHandlers[marketName] = handlers = new List<Action>();

            handlers.Add(callback);

            //If there is already data for the market, fire callback
            if (MarketsData.ContainsKey(marketName))
                callback();
        }

        /// <summary>
        /// Subscribe to a specific order by orderId
        /// </summary>
        public void SubscribeToOrderData(string orderId, Action callback)
        {
            List<Action> handlers;

            if (!OrderHandlers.TryGetValue(orderId, out handlers))
                OrderHandlers[orderId] = handlers = new List<Action>();

            handlers.Add(callback);

            //If there is already data for the order, fire callback
            if (OrdersData.ContainsKey(orderId))
                callback();
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
