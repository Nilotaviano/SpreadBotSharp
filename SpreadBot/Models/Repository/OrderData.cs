using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models.API;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    public class OrderData : IMessage
    {
        public OrderData() { }
        public OrderData(ApiRestResponse<ApiOrderData> apiOrderData)
            : this(apiOrderData.Data)
        {
            this.Sequence = apiOrderData.Sequence;
        }

        public OrderData(ApiOrderData apiOrderData)
        {
            Sequence = apiOrderData.Sequence;
            AccountId = apiOrderData.AccountId;
            Ceiling = apiOrderData.Delta.Ceiling;
            ClientOrderId = apiOrderData.Delta.ClientOrderId;
            ClosedAt = apiOrderData.Delta.ClosedAt;
            Commission = apiOrderData.Delta.Commission;
            CreatedAt = apiOrderData.Delta.CreatedAt;
            Direction = apiOrderData.Delta.Direction;
            FillQuantity = apiOrderData.Delta.FillQuantity;
            Id = apiOrderData.Delta.Id;
            Limit = apiOrderData.Delta.Limit;
            MarketSymbol = apiOrderData.Delta.MarketSymbol;
            Proceeds = apiOrderData.Delta.Proceeds;
            Quantity = apiOrderData.Delta.Quantity;
            Status = apiOrderData.Delta.Status;
            TimeInForce = apiOrderData.Delta.TimeInForce;
            Type = apiOrderData.Delta.Type;
            UpdatedAt = apiOrderData.Delta.UpdatedAt;
        }

        public MessageType MessageType => MessageType.OrderData;

        public int Sequence { get; private set; }

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
