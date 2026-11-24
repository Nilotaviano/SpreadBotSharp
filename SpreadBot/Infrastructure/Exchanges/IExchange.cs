using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges
{
    /// <summary>
    /// Exchange-neutral seam. Symbols are in CCXT's "BASE/QUOTE" form (e.g. "NMR/BTC").
    /// Methods throw <see cref="Models.ApiException"/> with an <see cref="Models.ApiErrorType"/> for exchange-reported failures.
    /// </summary>
    public interface IExchange
    {
        bool IsSetup { get; }

        decimal FeeRate { get; }

        /// <summary>
        /// Loads markets and starts the streams for the given quote currencies.
        /// </summary>
        Task Setup(IEnumerable<string> quoteCurrencies);

        //Streams: callbacks run on the exchange's stream threads
        void OnBalance(Action<BalanceData> callback);
        void OnMarketData(Action<IEnumerable<MarketData>> callback);
        void OnOrder(Action<OrderData> callback);

        /// <summary>
        /// Raised when a stream failed or reconnected, so updates may have been missed and state must be refetched.
        /// </summary>
        void OnStreamInterrupted(Action callback);

        //Snapshots
        Task<IReadOnlyList<BalanceData>> GetBalances();
        Task<IReadOnlyList<MarketData>> GetMarkets();
        Task<IReadOnlyList<MarketData>> GetTickers();
        Task<IReadOnlyList<OrderData>> GetOpenOrders();
        Task<OrderData> GetOrder(string orderId, string marketSymbol);
        Task<OrderData> GetOrderByClientId(string clientOrderId, string marketSymbol);

        //Orders: when the answer doesn't say whether an order was placed, they throw Models.OrderStateUnknownException
        Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit);
        Task<OrderData> SellMarket(string marketSymbol, decimal quantity);
        Task<OrderData> CancelOrder(string orderId, string marketSymbol);
    }
}
