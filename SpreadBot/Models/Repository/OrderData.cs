using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using System;

namespace SpreadBot.Models.Repository
{
    public class OrderData : IMessage
    {
        public OrderData() { }
        public OrderData(ApiRestResponse<BittrexApiOrderData> apiOrderData)
            : this(apiOrderData.Data)
        {
            this.Sequence = apiOrderData.Sequence;
        }

        public OrderData(BittrexApiOrderData apiOrderData)
            : this(apiOrderData.Delta)
        {
            Sequence = apiOrderData.Sequence;
            AccountId = apiOrderData.AccountId;
        }

        public OrderData(BittrexApiOrderData.Order order)
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

        public int Sequence { get; set; }

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
}
