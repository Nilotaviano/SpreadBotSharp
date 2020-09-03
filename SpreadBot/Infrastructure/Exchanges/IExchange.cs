using SpreadBot.Models;
using SpreadBot.Models.API;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;
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
        void OnBalance(Action<ApiBalanceData> callback);
        void OnSummaries(Action<ApiMarketSummariesData> callback);
        void OnTickers(Action<ApiTickersData> callback);
        void OnOrder(Action<ApiOrderData> callback);

        //Rest API methods
        Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<OrderData> CancelOrder(string orderId);
    }
}
