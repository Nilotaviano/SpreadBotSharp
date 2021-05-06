using Huobi.Net.Objects;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using System;

namespace SpreadBot.Models.Repository
{
    public class Order : IMessage
    {
        public Order() { }
        public Order(ApiRestResponse<BittrexApiOrderData> apiOrderData)
            : this(apiOrderData.Data)
        {
            this.Sequence = apiOrderData.Sequence;
        }

        public Order(BittrexApiOrderData apiOrderData)
            : this(apiOrderData.Delta)
        {
            Sequence = apiOrderData.Sequence;
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

        public Order(HuobiOrder order)
        {
            //Ceiling = order.Ceiling;
            ClientOrderId = order.ClientOrderId ?? order.Id.ToString();
            ClosedAt = order.FinishedAt;
            Commission = order.FilledFees;
            CreatedAt = order.CreatedAt;
            Direction = order.Type switch
            {
                HuobiOrderType.LimitBuy => OrderDirection.BUY,
                HuobiOrderType.LimitMakerBuy => OrderDirection.BUY,
                HuobiOrderType.MarketBuy => OrderDirection.BUY,
                HuobiOrderType.StopLimitBuy => OrderDirection.BUY,
                HuobiOrderType.FillOrKillLimitBuy => OrderDirection.BUY,
                HuobiOrderType.FillOrKillStopLimitBuy => OrderDirection.BUY,
                HuobiOrderType.IOCBuy => OrderDirection.BUY,
                HuobiOrderType.FillOrKillLimitSell => OrderDirection.SELL,
                HuobiOrderType.FillOrKillStopLimitSell => OrderDirection.SELL,
                HuobiOrderType.IOCSell => OrderDirection.SELL,
                HuobiOrderType.LimitMakerSell => OrderDirection.SELL,
                HuobiOrderType.LimitSell => OrderDirection.SELL,
                HuobiOrderType.MarketSell => OrderDirection.SELL,
                HuobiOrderType.StopLimitSell => OrderDirection.SELL,
                _ => OrderDirection.UNDEFINED,
            };
            FillQuantity = order.FilledAmount;
            Id = order.Id.ToString();
            Limit = order.Price;
            MarketSymbol = order.Symbol;
            Proceeds = order.FilledCashAmount;
            Quantity = order.Amount;
            Status = order.State switch {
                HuobiOrderState.Canceled => OrderStatus.CLOSED,
                HuobiOrderState.Created => OrderStatus.OPEN,
                HuobiOrderState.Filled => OrderStatus.CLOSED,
                HuobiOrderState.PartiallyCanceled => OrderStatus.CLOSED,
                HuobiOrderState.PartiallyFilled => OrderStatus.OPEN,
                HuobiOrderState.PreSubmitted => OrderStatus.OPEN,
                HuobiOrderState.Rejected => OrderStatus.CLOSED,
                HuobiOrderState.Submitted => OrderStatus.OPEN,
            };
            TimeInForce = order.Type switch
            {
                HuobiOrderType.LimitBuy => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.LimitMakerBuy => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.MarketBuy => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.StopLimitBuy => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.FillOrKillLimitBuy => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.FillOrKillStopLimitBuy => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.IOCBuy => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.FillOrKillLimitSell => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.FillOrKillStopLimitSell => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.IOCSell => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.LimitMakerSell => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.LimitSell => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.MarketSell => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.StopLimitSell => OrderTimeInForce.GOOD_TIL_CANCELLED,
                _ => OrderTimeInForce.UNDEFINED,
            };
            Type = order.Type switch
            {
                HuobiOrderType.LimitBuy => OrderType.LIMIT,
                HuobiOrderType.LimitMakerBuy => OrderType.LIMIT,
                HuobiOrderType.MarketBuy => OrderType.MARKET,
                HuobiOrderType.StopLimitBuy => OrderType.LIMIT,
                HuobiOrderType.FillOrKillLimitBuy => OrderType.LIMIT,
                HuobiOrderType.FillOrKillStopLimitBuy => OrderType.LIMIT,
                HuobiOrderType.IOCBuy => OrderType.MARKET,
                HuobiOrderType.FillOrKillLimitSell => OrderType.LIMIT,
                HuobiOrderType.FillOrKillStopLimitSell => OrderType.LIMIT,
                HuobiOrderType.IOCSell => OrderType.MARKET,
                HuobiOrderType.LimitMakerSell => OrderType.LIMIT,
                HuobiOrderType.LimitSell => OrderType.LIMIT,
                HuobiOrderType.MarketSell => OrderType.MARKET,
                HuobiOrderType.StopLimitSell => OrderType.LIMIT,
                _ => OrderType.UNDEFINED,
            };
            UpdatedAt = order.CreatedAt;
        }

        public Order(HuobiOpenOrder order)
        {
            //Ceiling = order.Ceiling;
            ClientOrderId = order.ClientOrderId ?? order.Id.ToString();
            ClosedAt = order.FinishedAt;
            Commission = order.FilledFees;
            CreatedAt = order.CreatedAt;
            Direction = order.Type switch
            {
                HuobiOrderType.LimitBuy => OrderDirection.BUY,
                HuobiOrderType.LimitMakerBuy => OrderDirection.BUY,
                HuobiOrderType.MarketBuy => OrderDirection.BUY,
                HuobiOrderType.StopLimitBuy => OrderDirection.BUY,
                HuobiOrderType.FillOrKillLimitBuy => OrderDirection.BUY,
                HuobiOrderType.FillOrKillStopLimitBuy => OrderDirection.BUY,
                HuobiOrderType.IOCBuy => OrderDirection.BUY,
                HuobiOrderType.FillOrKillLimitSell => OrderDirection.SELL,
                HuobiOrderType.FillOrKillStopLimitSell => OrderDirection.SELL,
                HuobiOrderType.IOCSell => OrderDirection.SELL,
                HuobiOrderType.LimitMakerSell => OrderDirection.SELL,
                HuobiOrderType.LimitSell => OrderDirection.SELL,
                HuobiOrderType.MarketSell => OrderDirection.SELL,
                HuobiOrderType.StopLimitSell => OrderDirection.SELL,
                _ => OrderDirection.UNDEFINED,
            };
            FillQuantity = order.FilledAmount;
            Id = order.Id.ToString();
            Limit = order.Price;
            MarketSymbol = order.Symbol;
            Proceeds = order.FilledCashAmount;
            Quantity = order.Amount;
            Status = order.State switch
            {
                HuobiOrderState.Canceled => OrderStatus.CLOSED,
                HuobiOrderState.Created => OrderStatus.OPEN,
                HuobiOrderState.Filled => OrderStatus.CLOSED,
                HuobiOrderState.PartiallyCanceled => OrderStatus.CLOSED,
                HuobiOrderState.PartiallyFilled => OrderStatus.OPEN,
                HuobiOrderState.PreSubmitted => OrderStatus.OPEN,
                HuobiOrderState.Rejected => OrderStatus.CLOSED,
                HuobiOrderState.Submitted => OrderStatus.OPEN,
            };
            TimeInForce = order.Type switch
            {
                HuobiOrderType.LimitBuy => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.LimitMakerBuy => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.MarketBuy => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.StopLimitBuy => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.FillOrKillLimitBuy => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.FillOrKillStopLimitBuy => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.IOCBuy => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.FillOrKillLimitSell => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.FillOrKillStopLimitSell => OrderTimeInForce.FILL_OR_KILL,
                HuobiOrderType.IOCSell => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.LimitMakerSell => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.LimitSell => OrderTimeInForce.GOOD_TIL_CANCELLED,
                HuobiOrderType.MarketSell => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                HuobiOrderType.StopLimitSell => OrderTimeInForce.GOOD_TIL_CANCELLED,
                _ => OrderTimeInForce.UNDEFINED,
            };
            Type = order.Type switch
            {
                HuobiOrderType.LimitBuy => OrderType.LIMIT,
                HuobiOrderType.LimitMakerBuy => OrderType.LIMIT,
                HuobiOrderType.MarketBuy => OrderType.MARKET,
                HuobiOrderType.StopLimitBuy => OrderType.LIMIT,
                HuobiOrderType.FillOrKillLimitBuy => OrderType.LIMIT,
                HuobiOrderType.FillOrKillStopLimitBuy => OrderType.LIMIT,
                HuobiOrderType.IOCBuy => OrderType.MARKET,
                HuobiOrderType.FillOrKillLimitSell => OrderType.LIMIT,
                HuobiOrderType.FillOrKillStopLimitSell => OrderType.LIMIT,
                HuobiOrderType.IOCSell => OrderType.MARKET,
                HuobiOrderType.LimitMakerSell => OrderType.LIMIT,
                HuobiOrderType.LimitSell => OrderType.LIMIT,
                HuobiOrderType.MarketSell => OrderType.MARKET,
                HuobiOrderType.StopLimitSell => OrderType.LIMIT,
                _ => OrderType.UNDEFINED,
            };
            UpdatedAt = order.CreatedAt;
        }

        public MessageType MessageType => MessageType.OrderData;

        public long Sequence { get; set; }

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
