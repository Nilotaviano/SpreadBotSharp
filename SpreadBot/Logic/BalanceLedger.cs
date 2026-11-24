using SpreadBot.Infrastructure;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Linq;

namespace SpreadBot.Logic
{
    /// <summary>
    /// Answers how much of the exchange's balances is free for new work versus claimed by running bots.
    /// The exchange's balances are the source of truth; bots' own Balance/HeldAmount and open order say which part of them is theirs.
    /// A bot only books a fill when its order closes, so an open order's partial fills are counted here explicitly.
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
        /// Base currency available on the exchange that no running bot holds.
        /// </summary>
        public decimal UnallocatedBaseBalance(string baseMarket)
        {
            var heldByBots = context.GetBots()
                .Where(b => b.BaseMarket == baseMarket)
                .Sum(AvailableBaseHeldBy);

            return Available(baseMarket) - heldByBots;
        }

        /// <summary>
        /// Coins of <paramref name="currency"/> available on the exchange that belong to running bots other than <paramref name="excluding"/>.
        /// </summary>
        public decimal CoinsHeldByOtherBots(string currency, Bot excluding = null)
        {
            return context.GetBots()
                .Where(b => b != excluding && b.botContext.LatestMarketData.Target == currency)
                .Sum(AvailableCoinsHeldBy);
        }

        /// <summary>
        /// Coins of <paramref name="currency"/> available on the exchange that no running bot claims.
        /// </summary>
        public decimal UnclaimedCoins(string currency)
        {
            return Math.Max(Available(currency) - CoinsHeldByOtherBots(currency), 0);
        }

        /// <summary>
        /// Everything in <paramref name="baseMarket"/> terms: the exchange's total base balance plus the coins bots on that base own, at their last trade rate.
        /// </summary>
        public decimal TotalValue(string baseMarket)
        {
            var total = dataRepository.BalancesData.TryGetValue(baseMarket, out var balance) ? balance.Total : 0;

            return context.GetBots()
                .Where(b => b.BaseMarket == baseMarket)
                .Aggregate(total, (value, bot) => value + CoinsOwnedBy(bot) * bot.LastTradeRate);
        }

        /// <summary>
        /// The part of the bot's base currency that the exchange counts as Available.
        /// </summary>
        private static decimal AvailableBaseHeldBy(Bot bot)
        {
            var order = bot.botContext.CurrentOrderData;

            //Locked in the buy order or already spent on its partial fills: neither is in Available
            if (IsOpen(order, OrderDirection.BUY))
                return 0;

            //Proceeds of the sell's partial fills are already in Available
            if (IsOpen(order, OrderDirection.SELL))
                return bot.Balance + order.Proceeds - order.Commission;

            return bot.Balance;
        }

        /// <summary>
        /// The part of the bot's coins that the exchange counts as Available.
        /// </summary>
        private static decimal AvailableCoinsHeldBy(Bot bot)
        {
            var order = bot.botContext.CurrentOrderData;

            //Unsold coins are locked in the sell order; sold ones are gone
            if (IsOpen(order, OrderDirection.SELL))
                return 0;

            //Coins from the buy's partial fills are already in Available
            if (IsOpen(order, OrderDirection.BUY))
                return bot.HeldAmount + order.FillQuantity;

            return bot.HeldAmount;
        }

        private static decimal CoinsOwnedBy(Bot bot)
        {
            var order = bot.botContext.CurrentOrderData;

            if (IsOpen(order, OrderDirection.SELL))
                return bot.HeldAmount - order.FillQuantity;

            if (IsOpen(order, OrderDirection.BUY))
                return bot.HeldAmount + order.FillQuantity;

            return bot.HeldAmount;
        }

        private static bool IsOpen(OrderData order, OrderDirection direction)
        {
            return order != null && order.Status == OrderStatus.OPEN && order.Direction == direction;
        }

        private decimal Available(string currency)
        {
            return dataRepository.BalancesData.TryGetValue(currency, out var balance) ? balance.Amount : 0;
        }
    }
}
