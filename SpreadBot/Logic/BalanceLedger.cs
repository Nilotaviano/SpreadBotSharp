using SpreadBot.Infrastructure;
using System;
using System.Linq;

namespace SpreadBot.Logic
{
    /// <summary>
    /// Answers how much of the exchange's balances is free for new work versus claimed by running bots.
    /// The exchange's balances are the source of truth; bots' own Balance/HeldAmount say which part of them is theirs.
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
            //Funds of bots with an open buy order are locked, so the exchange already excludes them from Available
            var heldByBots = context.GetBots()
                .Where(b => b.BaseMarket == baseMarket && b.botContext.BotState != BotState.BuyOrderActive)
                .Sum(b => b.Balance);

            return Available(baseMarket) - heldByBots;
        }

        /// <summary>
        /// Coins of <paramref name="currency"/> available on the exchange that belong to running bots other than
        /// <paramref name="excluding"/>. Coins locked in a bot's open sell order are not part of Available, so those bots are skipped.
        /// </summary>
        public decimal CoinsHeldByOtherBots(string currency, Bot excluding = null)
        {
            return context.GetBots()
                .Where(b => b != excluding && b.botContext.LatestMarketData.Target == currency && b.botContext.BotState != BotState.SellOrderActive)
                .Sum(b => b.HeldAmount);
        }

        /// <summary>
        /// Coins of <paramref name="currency"/> available on the exchange that no running bot claims.
        /// </summary>
        public decimal UnclaimedCoins(string currency)
        {
            return Math.Max(Available(currency) - CoinsHeldByOtherBots(currency), 0);
        }

        /// <summary>
        /// Everything in <paramref name="baseMarket"/> terms: the exchange's total base balance plus the coins bots on that base hold, at their last trade rate.
        /// </summary>
        public decimal TotalValue(string baseMarket)
        {
            var total = dataRepository.BalancesData.TryGetValue(baseMarket, out var balance) ? balance.Total : 0;

            return context.GetBots()
                .Where(b => b.BaseMarket == baseMarket)
                .Aggregate(total, (value, bot) => value + bot.HeldAmount * bot.LastTradeRate);
        }

        private decimal Available(string currency)
        {
            return dataRepository.BalancesData.TryGetValue(currency, out var balance) ? balance.Amount : 0;
        }
    }
}
