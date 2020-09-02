using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    public class OrderData : IMessage
    {
        public MessageType MessageType => MessageType.OrderData;

        public string AccountId { get; set; }
        public string Id { get; set; }
        public string MarketSymbol { get; set; }
        public string Direction { get; set; }
        public string Type { get; set; }
        public decimal Quantity { get; set; }
        public decimal Limit { get; set; }
        public decimal Ceiling { get; set; }
        public string TimeInForce { get; set; }
        public string ClientOrderId { get; set; }
        public decimal FillQuantity { get; set; }
        public decimal Commission { get; set; }
        public decimal Proceeds { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime ClosedAt { get; set; }
    }
}
