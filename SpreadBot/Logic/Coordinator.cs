using SpreadBot.Infrastructure;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SpreadBot.Logic
{
    public class Coordinator : IDisposable
    {
        private readonly BotStrategiesFactory botStrategiesFactory = new BotStrategiesFactory();
        private readonly IComparer<(decimal, decimal)> marketComparer = GetMarketComparer();
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly ICoordinatorContext context;
        private readonly BalanceLedger balanceLedger;
        private readonly Guid guid;
        private Dictionary<string, IGrouping<string, SpreadConfiguration>> configurationsByBaseMarket;

        //Allocation decisions read the bots and balances, so allocating and unallocating must not interleave
        private readonly object allocationLock = new object();

        public ConcurrentDictionary<SpreadConfiguration, ConcurrentDictionary<string, bool>> AllocatedMarketsPerSpreadConfiguration { get; } = new ConcurrentDictionary<SpreadConfiguration, ConcurrentDictionary<string, bool>>();

        public Coordinator(AppSettings appSettings, DataRepository dataRepository, ICoordinatorContext context)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.context = context;
            this.balanceLedger = new BalanceLedger(dataRepository, context);
            this.guid = Guid.NewGuid();

            this.appSettings.Reloaded += AppSettings_Reloaded;

            UpdateAppSettings();
        }

        private void AppSettings_Reloaded(object sender, EventArgs e)
        {
            UpdateAppSettings();
        }

        private void UpdateAppSettings()
        {
            //A reload runs on the file watcher's thread while evaluation and balance reporting read the grouping: swap in a new one, never mutate it
            var regrouped = appSettings.SpreadConfigurations.GroupBy(c => c.BaseMarket).ToDictionary(group => group.Key);

            lock (allocationLock)
                configurationsByBaseMarket = regrouped;
        }

        public void Start()
        {
            if (appSettings.TryToRestoreSession)
                RestorePreviousSession();

            this.dataRepository.SubscribeToMarketsData(guid, EvaluateMarkets);
            foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
                this.dataRepository.SubscribeToCurrencyBalance(baseMarket, guid, (bd) => ReportBalance());
        }

        private void RestorePreviousSession()
        {
            var previousSession = context.GetPreviousSessionContext();

            if (previousSession == null)
            {
                Logger.Instance.LogMessage("No previous session data.");
                return;
            }

            context.AddDustForMarkets(previousSession.DustPerMarket);

            if (previousSession.BotContexts != null)
            {
                foreach (var botContext in previousSession.BotContexts)
                {
                    var bot = new Bot(dataRepository, botContext, UnallocateBot, botStrategiesFactory, balanceLedger);

                    var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfiguration.GetOrAdd(bot.botContext.spreadConfiguration, key => new ConcurrentDictionary<string, bool>());

                    allocatedMarketsForConfiguration.TryAdd(bot.MarketSymbol, true);

                    AllocateBot(bot);
                    bot.Start();

                    Logger.Instance.LogMessage($"Allocated existing bot for market {bot.MarketSymbol}");
                }
            }
        }

        /// <summary>
        /// Evaluates updated markets for new bot-allocation opportunities
        /// </summary>
        /// <param name="marketDeltas">Markets that were updated</param>
        private void EvaluateMarkets(IEnumerable<MarketData> marketDeltas)
        {
            var allocatedBots = new List<Bot>();

            try
            {
                //Summaries and tickers are consumed on separate threads; evaluating concurrently could grant the same balance twice
                lock (allocationLock)
                    EvaluateMarketsSerialized(marketDeltas, allocatedBots);
            }
            finally
            {
                //Started outside the lock so nothing a bot does on start can block allocation, and even if a later
                //market failed: an allocated bot that never starts would hold its balance and market forever
                foreach (var bot in allocatedBots)
                    bot.Start();
            }
        }

        private void EvaluateMarketsSerialized(IEnumerable<MarketData> marketDeltas, List<Bot> allocatedBots)
        {
            if (context.GetBotCount() >= appSettings.MaxNumberOfBots)
                return;

            var marketDeltasByBaseMarket = marketDeltas.GroupBy(m => m.BaseMarket);

            foreach (var marketDeltaGroup in marketDeltasByBaseMarket)
            {
                var baseMarket = marketDeltaGroup.Key;

                if (!configurationsByBaseMarket.TryGetValue(baseMarket, out var marketConfigurations))
                    continue;

                //Filter only relevant markets
                var orderedMarkets = marketDeltaGroup.OrderBy(GetMarketOrderKey, marketComparer);
                var anyConfigurationAvailable = false;

                foreach (var market in orderedMarkets)
                {
                    if (!IsViableMarket(market))
                        continue;

                    // Optimization for stop looking for markets if is not possible to allocate any configuration
                    anyConfigurationAvailable = false;

                    foreach (var configuration in marketConfigurations)
                    {
                        if (!CanAllocateBotForConfiguration(configuration))
                        {
                            Logger.Instance.LogMessage($"Not enough balance/bots for market {market.Symbol}");
                            continue;
                        }

                        anyConfigurationAvailable = true;

                        if (!EvaluateMarketBasedOnConfiguration(market, configuration))
                            continue;

                        var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfiguration.GetOrAdd(configuration, key => new ConcurrentDictionary<string, bool>());

                        if (!allocatedMarketsForConfiguration.TryAdd(market.Symbol, true))
                        {
                            Logger.Instance.LogMessage($"Already allocated bot for market {market.Symbol}");
                            continue;
                        }

                        Logger.Instance.LogMessage($"Found market: {market.Symbol}");

                        var existingDust = GetInheritableDust(market);
                        var bot = new Bot(dataRepository, configuration, market, existingDust, UnallocateBot, botStrategiesFactory, balanceLedger);

                        AllocateBot(bot);
                        allocatedBots.Add(bot);
                    }

                    if (!anyConfigurationAvailable)
                        break;
                }
            }
        }

        private void AllocateBot(Bot bot)
        {
            lock (allocationLock)
            {
                context.AddBot(bot);
                Logger.Instance.LogMessage($"Granted {bot.Balance}{bot.BaseMarket} to bot {bot.Guid}. Unallocated balance: {balanceLedger.UnallocatedBaseBalance(bot.BaseMarket)}{bot.BaseMarket}");
            }
        }

        private bool IsViableMarket(MarketData market)
        {
            return string.IsNullOrWhiteSpace(market.Notice) && market.Status == EMarketStatus.Online;
        }

        private void ReportBalance()
        {
            foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
                BalanceReporter.Instance.ReportBalance(balanceLedger.TotalValue(baseMarket), baseMarket);
        }

        internal static (decimal, decimal) GetMarketOrderKey(MarketData marketData)
        {
            return (marketData.QuoteVolume.GetValueOrDefault(0), marketData.SpreadPercentage);
        }

        private bool EvaluateMarketBasedOnConfiguration(MarketData marketData, SpreadConfiguration spreadConfiguration)
        {
            if (marketData.LastTradeRate < spreadConfiguration.MinimumPrice)
                return false;

            if (marketData.SpreadPercentage < spreadConfiguration.MinimumSpreadPercentage || marketData.QuoteVolume < spreadConfiguration.MinimumQuoteVolume)
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has not enough volume ({marketData.QuoteVolume}) or spread ({marketData.SpreadPercentage})");
                return false;
            }

            if (marketData.PercentChange > spreadConfiguration.MaxPercentChangeFromPreviousDay)
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is pumping {marketData.PercentChange}%");
                return false;
            }

            if(spreadConfiguration.AvoidTokenizedSecurities && marketData.IsTokenizedSecurity.GetValueOrDefault())
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is a tokenized security");
                return false;
            }

            return true;
        }

        private bool CanAllocateBotForConfiguration(SpreadConfiguration spreadConfiguration)
        {
            return context.GetBotCount() < appSettings.MaxNumberOfBots
                && balanceLedger.UnallocatedBaseBalance(spreadConfiguration.BaseMarket) > spreadConfiguration.AllocatedAmountOfBaseCurrency;
        }

        /// <summary>
        /// Recorded dust for the market, capped at the coins actually available on the exchange. A bot that finished
        /// after a failed sell records holdings the exchange may not have, and inheriting them would make every
        /// later sell on that market fail with insufficient funds.
        /// </summary>
        private decimal GetInheritableDust(MarketData market)
        {
            var recordedDust = context.RemoveDustForMarket(market.Symbol);
            var inheritable = balanceLedger.UnclaimedCoins(market.Target);

            if (recordedDust <= inheritable)
                return recordedDust;

            Logger.Instance.LogError($"Recorded dust {recordedDust}{market.Target} for {market.Symbol} exceeds the {inheritable}{market.Target} unclaimed on the exchange; inheriting only that and keeping the rest recorded");
            context.AddDustForMarket(market.Symbol, recordedDust - inheritable);
            return inheritable;
        }

        private void UnallocateBot(Bot bot)
        {
            lock (allocationLock)
            {
                bool removeAllocatedBot = context.RemoveBot(bot.Guid);
                bool removedAllocatedMarket = AllocatedMarketsPerSpreadConfiguration[bot.botContext.spreadConfiguration].TryRemove(bot.MarketSymbol, out _);

                if (!removeAllocatedBot)
                    Logger.Instance.LogUnexpectedError($"Couldn't remove allocated bot {bot.Guid}");

                if (!removedAllocatedMarket)
                    Logger.Instance.LogUnexpectedError($"Couldn't remove allocated market {bot.MarketSymbol}");

                Debug.Assert(removeAllocatedBot, "Bot should have been removed successfully");
                Debug.Assert(removedAllocatedMarket, $"Market {bot.MarketSymbol} had already been deallocated from configuration {bot.botContext.spreadConfiguration.GetHashCode()}");

                Logger.Instance.LogMessage($"Recovered {bot.Balance}{bot.BaseMarket} from bot {bot.Guid}");

                if (bot.HeldAmount > 0)
                    context.AddDustForMarket(bot.MarketSymbol, bot.HeldAmount);
            }
        }

        internal static IComparer<(decimal, decimal)> GetMarketComparer()
        {
            return Comparer<(decimal, decimal)>.Create((key1, key2) =>
            {
                return (key2.Item1 * key2.Item2).CompareTo(key1.Item1 * key1.Item2);
                //// Descending order
                //var result = key2.Item1.CompareTo(key1.Item1);
                //if (result == 0)
                //    return key2.Item2.CompareTo(key1.Item2);
                //return result;
            });
        }

        public void Dispose()
        {
            this.dataRepository.UnsubscribeToMarketsData(guid);
        }
    }
}
