using SpreadBot.Infrastructure;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Linq;

namespace SpreadBot.Logic
{
    /// <summary>
    /// Answers how much of the exchange's balances is free for new work versus claimed by running bots.
    /// The exchange's balances are the source of truth; bots' own Balance/HeldAmount and order say which part of them is theirs.
    /// A currency can be claimed both as a base market (bots' funds) and as a traded coin (bots' coins), e.g. BTC with BTC and USDT bases.
    /// Balances can run ahead of a bot's own view of its order, so claims follow the newest order state known and
    /// count funds an in-flight request may already have unlocked (a cancel frees them before the bot sees the answer).
    /// </summary>
    public class BalanceLedger
    {
        private readonly DataRepository dataRepository;
        private readonly ICoordinatorContext context;

        public BalanceLedger(DataRepository dataRepository, ICoordinatorContext context)
        {
            this.dataRepository = dataRepository;
            this.context = context;
        }

        /// <summary>
        /// Base currency available on the exchange that no running bot claims.
        /// </summary>
        public decimal UnallocatedBaseBalance(string baseMarket)
        {
            return Available(baseMarket) - ClaimedByOtherBots(baseMarket);
        }

        /// <summary>
        /// The part of the available <paramref name="currency"/> that running bots other than <paramref name="excluding"/> claim,
        /// as base funds or as coins.
        /// </summary>
        public decimal ClaimedByOtherBots(string currency, Bot excluding = null)
        {
            return context.GetBots()
                .Where(b => b != excluding)
                .Sum(b => AvailableClaimedBy(b, currency));
        }

        /// <summary>
        /// Coins of <paramref name="currency"/> available on the exchange that no running bot claims.
        /// </summary>
        public decimal UnclaimedCoins(string currency)
        {
            return Math.Max(Available(currency) - ClaimedByOtherBots(currency), 0);
        }

        /// <summary>
        /// Everything in <paramref name="baseMarket"/> terms: the exchange's total base balance plus the coins bots on that base own, at their last trade rate.
        /// Coins of <paramref name="baseMarket"/> bought by bots on another base count toward that base instead.
        /// </summary>
        public decimal TotalValue(string baseMarket)
        {
            var total = dataRepository.BalancesData.TryGetValue(baseMarket, out var balance) ? balance.Total : 0;

            foreach (var bot in context.GetBots())
            {
                var order = LatestOrder(bot);

                if (bot.botContext.LatestMarketData.Target == baseMarket)
                    total -= CoinsOwnedBy(bot, order);

                if (bot.BaseMarket == baseMarket)
                    total += CoinsOwnedBy(bot, order) * bot.LastTradeRate;
            }

            return total;
        }

        private decimal AvailableClaimedBy(Bot bot, string currency)
        {
            var order = LatestOrder(bot);
            var claimed = 0m;

            if (bot.BaseMarket == currency)
                claimed += AvailableBaseHeldBy(bot, order);

            if (bot.botContext.LatestMarketData.Target == currency)
                claimed += AvailableCoinsHeldBy(bot, order);

            return claimed;
        }

        /// <summary>
        /// The part of the bot's base currency that the exchange counts as Available.
        /// </summary>
        private static decimal AvailableBaseHeldBy(Bot bot, OrderData order)
        {
            //Spent on the buy's fills: gone. The rest is locked in the order, unless the order is closed or a request may have just unlocked it
            if (Is(order, OrderDirection.BUY))
                return MayBeUnlocked(bot, order) ? bot.Balance - order.Proceeds - order.Commission : 0;

            //Proceeds of the sell's fills are already in Available
            if (Is(order, OrderDirection.SELL))
                return bot.Balance + order.Proceeds - order.Commission;

            //A sell whose placement is unknown may already have filled: claim the most it can bring in
            if (IsUnknown(order, OrderDirection.SELL))
                return bot.Balance + order.Quantity * order.Limit;

            return bot.Balance;
        }

        /// <summary>
        /// The part of the bot's coins that the exchange counts as Available.
        /// </summary>
        private static decimal AvailableCoinsHeldBy(Bot bot, OrderData order)
        {
            //Sold coins and fees taken in the coin are gone. The rest is locked in the order, unless the order is closed or a request may have just unlocked it
            if (Is(order, OrderDirection.SELL))
                return MayBeUnlocked(bot, order) ? bot.HeldAmount - order.FillQuantity - order.CoinCommission : 0;

            //Coins from the buy's fills, net of fees taken in the coin, are already in Available
            if (Is(order, OrderDirection.BUY))
                return bot.HeldAmount + order.FillQuantity - order.CoinCommission;

            //A buy whose placement is unknown may already have filled: claim the most it can bring in
            if (IsUnknown(order, OrderDirection.BUY))
                return bot.HeldAmount + order.Quantity;

            return bot.HeldAmount;
        }

        private static decimal CoinsOwnedBy(Bot bot, OrderData order)
        {
            if (Is(order, OrderDirection.SELL))
                return bot.HeldAmount - order.FillQuantity - order.CoinCommission;

            if (Is(order, OrderDirection.BUY))
                return bot.HeldAmount + order.FillQuantity - order.CoinCommission;

            return bot.HeldAmount;
        }

        /// <summary>
        /// The bot's order as the exchange last reported it, if that is newer than the bot's own copy: the order stream
        /// updates the repository before the bot processes the update, and balances may already reflect it.
        /// An order the exchange reports closed counts as booked, though the bot hasn't booked it yet.
        /// </summary>
        private OrderData LatestOrder(Bot bot)
        {
            var order = bot.botContext.CurrentOrderData;

            if (order?.ClientOrderId != null
                && dataRepository.OrdersData.TryGetValue(order.ClientOrderId, out var reported)
                && reported.Id == order.Id
                && (reported.Status == OrderStatus.CLOSED && order.Status != OrderStatus.CLOSED || reported.FillQuantity > order.FillQuantity))
                return reported;

            return order;
        }

        private static bool MayBeUnlocked(Bot bot, OrderData order)
        {
            return order.Status == OrderStatus.CLOSED || bot.OrderRequestInFlight;
        }

        private static bool Is(OrderData order, OrderDirection direction)
        {
            return order != null && (order.Status == OrderStatus.OPEN || order.Status == OrderStatus.CLOSED) && order.Direction == direction;
        }

        private static bool IsUnknown(OrderData order, OrderDirection direction)
        {
            return order != null && order.Status == OrderStatus.UNKNOWN && order.Direction == direction;
        }

        private decimal Available(string currency)
        {
            return dataRepository.BalancesData.TryGetValue(currency, out var balance) ? balance.Amount : 0;
        }
    }
}
