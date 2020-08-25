using SpreadBot.Models;
using SpreadBot.Models.API;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges
{
    public interface IExchange
    {
        bool IsSetup { get; }

        Task Setup();

        //Websocket subscription methods
        //For exchanges that don't support websockets, use an interval-based cycle to call equivalent API methods to simulate Websocket behavior
        void OnBalance(Action<ApiBalanceData> callback);
        void OnSummaries(Action<ApiMarketSummariesData> callback);
        void OnTickers(Action<ApiTickersData> callback);
        void OnOrder(Action<ApiOrderData> callback);

        //Rest API methods
        Task<ApiOrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<ApiOrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<ApiOrderData> CancelOrder(string orderId);
    }
}
