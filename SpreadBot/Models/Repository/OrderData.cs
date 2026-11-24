using System;

namespace SpreadBot.Models.Repository
{
    public class OrderData : IMessage
    {
        public MessageType MessageType => MessageType.OrderData;

        public string Id { get; set; }
        public string ClientOrderId { get; set; }
        public string MarketSymbol { get; set; }
        public OrderDirection Direction { get; set; }
        public OrderType Type { get; set; }
        public decimal Quantity { get; set; }
        public decimal Limit { get; set; }
        public decimal FillQuantity { get; set; }
        /// <summary>Quote currency spent (buy) or received (sell) by the fills, before fees.</summary>
        public decimal Proceeds { get; set; }
        /// <summary>Fee charged in the quote currency.</summary>
        public decimal Commission { get; set; }
        /// <summary>Fee charged in the bought/sold coin (e.g. Binance takes buy fees from the received coin).</summary>
        public decimal CoinCommission { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime ClosedAt { get; set; }
    }
}
