using Huobi.Net.Objects;
using Huobi.Net.Objects.SocketObjects.V2;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure.Exchanges.Huobi
{
    public static class HuobiTypeConverter
    {
        public static Balance ConvertBalance(HuobiBalance originalBalance)
        {
            var balance = new Balance
            {
                CurrencyAbbreviation = originalBalance.Currency,
                Amount = originalBalance.Balance
            };

            return balance;
        }
        public static Balance ConvertBalance(HuobiAccountUpdate accountUpdate)
        {
            var balance = new Balance
            {
                CurrencyAbbreviation = accountUpdate.Currency,
                Amount = accountUpdate.Available.GetValueOrDefault()
            };

            return balance;
        }

        public static Order ConvertOrder(HuobiOrder originalOrder)
        {
            var order = new Order
            {
                //order.Ceiling = originalOrder.Ceiling;
                ClientOrderId = originalOrder.ClientOrderId ?? originalOrder.Id.ToString(),
                ClosedAt = originalOrder.FinishedAt,
                Commission = originalOrder.FilledFees,
                CreatedAt = originalOrder.CreatedAt,
                Direction = GetDirection(originalOrder.Type),
                FillQuantity = originalOrder.FilledAmount,
                Id = originalOrder.Id.ToString(),
                Limit = originalOrder.Price,
                MarketSymbol = originalOrder.Symbol,
                Proceeds = originalOrder.FilledCashAmount,
                Quantity = originalOrder.Amount,
                Status = GetStatus(originalOrder.State),
                TimeInForce = GetTimeInForce(originalOrder.Type),
                Type = GetType(originalOrder.Type),
                UpdatedAt = originalOrder.CreatedAt
            };

            return order;
        }

        public static Order ConvertOrder(HuobiOpenOrder originalOrder)
        {
            var order = new Order
            {
                //order.Ceiling = originalOrder.Ceiling;
                ClientOrderId = originalOrder.ClientOrderId ?? originalOrder.Id.ToString(),
                ClosedAt = originalOrder.FinishedAt,
                Commission = originalOrder.FilledFees,
                CreatedAt = originalOrder.CreatedAt,
                Direction = GetDirection(originalOrder.Type),
                FillQuantity = originalOrder.FilledAmount,
                Id = originalOrder.Id.ToString(),
                Limit = originalOrder.Price,
                MarketSymbol = originalOrder.Symbol,
                Proceeds = originalOrder.FilledCashAmount,
                Quantity = originalOrder.Amount,
                Status = GetStatus(originalOrder.State),
                TimeInForce = GetTimeInForce(originalOrder.Type),
                Type = GetType(originalOrder.Type),
                UpdatedAt = originalOrder.CreatedAt
            };

            return order;
        }

        private static OrderType GetType(HuobiOrderType originalOrderType)
        {
            return originalOrderType switch
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
        }

        private static OrderTimeInForce GetTimeInForce(HuobiOrderType originalOrderType)
        {
            return originalOrderType switch
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
        }

        private static OrderStatus GetStatus(HuobiOrderState originalOrderState)
        {
            return originalOrderState switch
            {
                HuobiOrderState.Canceled => OrderStatus.CLOSED,
                HuobiOrderState.Created => OrderStatus.OPEN,
                HuobiOrderState.Filled => OrderStatus.CLOSED,
                HuobiOrderState.PartiallyCanceled => OrderStatus.CLOSED,
                HuobiOrderState.PartiallyFilled => OrderStatus.OPEN,
                HuobiOrderState.PreSubmitted => OrderStatus.OPEN,
                HuobiOrderState.Rejected => OrderStatus.CLOSED,
                HuobiOrderState.Submitted => OrderStatus.OPEN,
                _ => OrderStatus.UNDEFINED,
            };
        }

        private static OrderDirection GetDirection(HuobiOrderType originalOrderType)
        {
            return originalOrderType switch
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
        }
    }
}
