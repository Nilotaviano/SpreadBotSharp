using SpreadBot.Models;
using System;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class BittrexApiOrderData
    {
        public string AccountId { get; set; }
        public int Sequence { get; set; }

        public Order Delta { get; set; }

        public class Order
        {
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

            public OrderToCancelData OrderToCancel { get; set; }

            public class OrderToCancelData
            {
                public string Type { get; set; }

                public string Id { get; set; }
            }
        }
    }
}
