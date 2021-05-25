using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using System;

namespace SpreadBot.Models.Repository
{
    public class Order : IMessage
    {
        public Order() { }

        public Order(BittrexApiOrderData apiOrderData)
            : this(apiOrderData.Delta)
        {
            AccountId = apiOrderData.AccountId;
        }

        public Order(BittrexApiOrderData.Order order)
        {
            Ceiling = order.Ceiling;
            ClientOrderId = order.ClientOrderId ?? order.Id;
            ClosedAt = order.ClosedAt;
            Commission = order.Commission;
            CreatedAt = order.CreatedAt;
            Direction = order.Direction;
            FillQuantity = order.FillQuantity;
            Id = order.Id;
            Limit = order.Limit;
            MarketSymbol = order.MarketSymbol;
            Proceeds = order.Proceeds;
            Quantity = order.Quantity;
            Status = order.Status;
            TimeInForce = order.TimeInForce;
            Type = order.Type;
            UpdatedAt = order.UpdatedAt;
        }

        public MessageType MessageType => MessageType.OrderData;

        public string AccountId { get; set; }
        public string Id { get; set; }
        public string MarketSymbol { get; set; }
        public OrderDirection Direction { get; set; }
        public OrderType Type { get; set; }
        public decimal Quantity { get; set; }
        public decimal Limit { get; set; }
        public decimal Ceiling { get; set; }
        public OrderTimeInForce TimeInForce { get; set; }
        public string ClientOrderId { get; set; }
        public decimal FillQuantity { get; set; }
        public decimal Commission { get; set; }
        public decimal Proceeds { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime ClosedAt { get; set; }
    }

    public class OrderData
    {
        public long? Sequence { get; set; }

        public Order Order { get; set; }
    }
}
