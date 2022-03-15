using Kucoin.Net.Objects;
using Kucoin.Net.Objects.Models.Spot;
using Kucoin.Net.Objects.Models.Spot.Socket;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure.Exchanges.KucoinWrapper
{
    public static class KucoinTypeConverter
    {
        public static Balance ConvertBalance(KucoinAccount originalBalance)
        {
            var balance = new Balance
            {
                CurrencyAbbreviation = originalBalance.Asset,
                Amount = originalBalance.Total
            };

            return balance;
        }
        public static Balance ConvertBalance(KucoinBalanceUpdate accountUpdate)
        {
            var balance = new Balance
            {
                CurrencyAbbreviation = accountUpdate.Asset,
                Amount = accountUpdate.Available
            };

            return balance;
        }

        public static Order ConvertOrder(KucoinOrder originalOrder)
        {
            var order = new Order
            {
                //order.Ceiling = originalOrder.Ceiling;
                ClientOrderId = originalOrder.ClientOrderId ?? originalOrder.Id.ToString(),
                //ClosedAt = originalOrder.FinishedAt,
                Commission = originalOrder.Fee,
                CreatedAt = originalOrder.CreateTime,
                Direction = GetDirection(originalOrder.Side),
                FillQuantity = originalOrder.QuoteQuantityFilled.GetValueOrDefault(),
                Id = originalOrder.Id.ToString(),
                Limit = originalOrder.Price.GetValueOrDefault(),
                MarketSymbol = originalOrder.Symbol,
                Proceeds = originalOrder.QuantityFilled,
                Quantity = originalOrder.Quantity.GetValueOrDefault(),
                Status = originalOrder.IsActive.Value ? OrderStatus.OPEN : OrderStatus.CLOSED,
                TimeInForce = GetTimeInForce(originalOrder.TimeInForce),
                Type = GetType(originalOrder.Type),
                UpdatedAt = DateTime.Now
            };

            return order;
        }

        private static OrderType GetType(Kucoin.Net.Enums.OrderType originalOrderType)
        {
            return originalOrderType switch
            {
                Kucoin.Net.Enums.OrderType.Limit => OrderType.LIMIT,
                Kucoin.Net.Enums.OrderType.Market => OrderType.MARKET,
                Kucoin.Net.Enums.OrderType.LimitStop => OrderType.UNDEFINED,
                Kucoin.Net.Enums.OrderType.MarketStop => OrderType.UNDEFINED,
                Kucoin.Net.Enums.OrderType.Stop => OrderType.UNDEFINED,
                _ => OrderType.UNDEFINED,
            };
        }

        private static OrderTimeInForce GetTimeInForce(Kucoin.Net.Enums.TimeInForce? originalOrderType)
        {
            return originalOrderType switch
            {
                Kucoin.Net.Enums.TimeInForce.GoodTillCanceled => OrderTimeInForce.GOOD_TIL_CANCELLED,
                Kucoin.Net.Enums.TimeInForce.GoodTillTime => OrderTimeInForce.GOOD_TIL_CANCELLED,
                Kucoin.Net.Enums.TimeInForce.FillOrKill => OrderTimeInForce.FILL_OR_KILL,
                Kucoin.Net.Enums.TimeInForce.ImmediateOrCancel => OrderTimeInForce.IMMEDIATE_OR_CANCEL,
                _ => OrderTimeInForce.UNDEFINED,
            };
        }

        private static OrderStatus GetStatus(Kucoin.Net.Enums.OrderStatus originalOrderState)
        {
            return originalOrderState switch
            {
                Kucoin.Net.Enums.OrderStatus.Active => OrderStatus.OPEN,
                Kucoin.Net.Enums.OrderStatus.Done => OrderStatus.CLOSED,
                _ => OrderStatus.UNDEFINED,
            };
        }

        private static OrderDirection GetDirection(Kucoin.Net.Enums.OrderSide originalOrderType)
        {
            return originalOrderType switch
            {
                Kucoin.Net.Enums.OrderSide.Buy => OrderDirection.BUY,
                Kucoin.Net.Enums.OrderSide.Sell => OrderDirection.SELL,
                _ => OrderDirection.UNDEFINED,
            };
        }
    }
}
