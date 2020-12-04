using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models;
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
        void OnBalance(Action<BittrexApiBalanceData> callback);
        void OnSummaries(Action<BittrexApiMarketSummariesData> callback);
        void OnTickers(Action<BittrexApiTickersData> callback);
        void OnOrder(Action<BittrexApiOrderData> callback);

        //Rest API methods
        Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<OrderData> CancelOrder(string orderId);

        Task<CompleteBalanceData> GetBalanceData();
        Task<BittrexApiTickersData> GetTickersData();
        Task<BittrexApiMarketSummariesData> GetMarketSummariesData();
        Task<BittrexApiMarketData[]> GetMarketsData();
        Task<ApiRestResponse<BittrexApiOrderData.Order[]>> GetClosedOrdersData(string startAfterOrderId);
    }
}
