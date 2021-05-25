using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges
{
    public interface IExchange
    {
        bool IsSetup { get; }

        decimal FeeRate { get; }

        Task Setup();

        //Websocket subscription methods
        //For exchanges that don't support websockets, use an interval-based cycle to call equivalent API methods to simulate Websocket behavior
        void OnBalance(Action<BalanceData> callback);
        void OnSummaries(Action<MarketSummaryData> callback);
        void OnTickers(Action<TickerData> callback);
        void OnOrder(Action<OrderData> callback);

        //Rest API methods
        Task<Order> BuyLimit(string marketSymbol, decimal quantity, decimal limit, string clientOrderId = null);
        Task<Order> SellLimit(string marketSymbol, decimal quantity, decimal limit, string clientOrderId = null);
        Task<Order> BuyMarket(string marketSymbol, decimal quantity);
        Task<Order> SellMarket(string marketSymbol, decimal quantity);
        Task<Order> CancelOrder(string orderId);

        Task<CompleteBalanceData> GetBalanceData();
        Task<TickerData> GetTickersData();
        Task<MarketSummaryData> GetMarketSummariesData();
        Task<Market[]> GetMarketsData();
        Task<Order[]> GetClosedOrdersData(string startAfterOrderId);
        Task<Order[]> GetOpenOrdersData();

        Task<Order> GetOrderData(string orderId);
    }
}
