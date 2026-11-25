using SpreadBot.Infrastructure;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot.Logic
{
    public class Coordinator : IDisposable
    {
        private readonly BotStrategiesFactory botStrategiesFactory;
        private readonly IComparer<(decimal, decimal)> marketComparer = GetMarketComparer();
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly ICoordinatorContext context;
        private readonly BalanceLedger balanceLedger;
        private readonly MarketFilters marketFilters;
        private readonly Guid guid;
        private Dictionary<string, IGrouping<string, SpreadConfiguration>> configurationsByBaseMarket;

        //Allocation decisions read the bots and balances, so allocating and unallocating must not interleave
        private readonly object allocationLock = new object();

        //How long a halt waits for the bots to cancel their orders
        internal static readonly TimeSpan HaltTimeout = TimeSpan.FromSeconds(60);
        //Under allocationLock
        private bool halted;
        private string haltReason;
        private bool allocationPaused;
        private DateTime haltRetryUntil;
        private readonly List<Task> haltingBots = new List<Task>();
        //Set once Start's restore is over, so a halt knows every bot it must wait for
        private readonly TaskCompletionSource restoreDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource haltCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentDictionary<SpreadConfiguration, ConcurrentDictionary<string, bool>> AllocatedMarketsPerSpreadConfiguration { get; } = new ConcurrentDictionary<SpreadConfiguration, ConcurrentDictionary<string, bool>>();

        public Coordinator(AppSettings appSettings, DataRepository dataRepository, ICoordinatorContext context)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.context = context;
            this.balanceLedger = new BalanceLedger(dataRepository, context);
            this.marketFilters = new MarketFilters(appSettings);
            this.botStrategiesFactory = new BotStrategiesFactory(marketFilters);
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

        /// <summary>
        /// Completes once a halt has stopped the bots (or <see cref="HaltTimeout"/> passed).
        /// </summary>
        public Task Halted => haltCompleted.Task;

        public bool IsHalted
        {
            get { lock (allocationLock) return halted; }
        }

        public string HaltReason
        {
            get { lock (allocationLock) return haltReason; }
        }

        public bool IsAllocationPaused
        {
            get { lock (allocationLock) return allocationPaused; }
        }

        public IEnumerable<SpreadConfiguration> Configurations
        {
            get { lock (allocationLock) return configurationsByBaseMarket.Values.SelectMany(group => group).ToList(); }
        }

        /// <summary>
        /// While paused no new bot is allocated; running bots carry on.
        /// </summary>
        public void SetAllocationPaused(bool paused)
        {
            lock (allocationLock)
                allocationPaused = paused;

            Logger.Instance.LogMessage(paused ? "Allocation paused" : "Allocation resumed");
        }

        /// <summary>
        /// Asks one bot to stop: see <see cref="Bot.RequestFinish"/>. False if no such bot is allocated.
        /// </summary>
        public bool FinishBot(Guid botId)
        {
            var bot = context.GetBots().FirstOrDefault(b => b.Guid == botId);
            if (bot == null)
                return false;

            Logger.Instance.LogMessage($"Finishing bot {botId} on {bot.MarketSymbol} on request");
            _ = Task.Run(bot.RequestFinish);
            return true;
        }

        /// <summary>
        /// Why <paramref name="market"/> can't get a bot of <paramref name="configuration"/>, or null if it can (balance and bot slots aside).
        /// </summary>
        public string WhyNotAllocatable(MarketData market, SpreadConfiguration configuration) =>
            marketFilters.Rejection(market, configuration);

        public void Start()
        {
            //First, so restoring bots can already halt
            dataRepository.Exchange.OnFatalError(error => Halt($"{error.ApiErrorType}: {error.Message}"));

            try
            {
                if (appSettings.TryToRestoreSession)
                    RestorePreviousSession();
            }
            finally
            {
                restoreDone.TrySetResult();
            }

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
                    //Saved while finishing: a restored bot would ignore every message and never unallocate, so do its unallocation here
                    if (botContext.BotState == BotState.FinishedWork)
                    {
                        if (botContext.HeldAmount > 0)
                            context.AddDustForMarket(botContext.LatestMarketData.Symbol, botContext.HeldAmount);

                        context.RemoveBot(botContext.Guid);
                        Logger.Instance.LogError($"A bot on {botContext.LatestMarketData.Symbol} was saved while finishing; not restoring it. Its {botContext.HeldAmount}{botContext.LatestMarketData.Target} are recorded as dust and its profit as of that save");
                        continue;
                    }

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
            if (halted || allocationPaused || context.GetBotCount() >= appSettings.MaxNumberOfBots)
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
                    if (marketFilters.ViabilityRejection(market) != null)
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

                        //Bots of two configurations on one market would outbid each other
                        if (BotCountOnMarket(market.Symbol) >= appSettings.MaxNumberOfBotsPerMarket)
                        {
                            Logger.Instance.LogMessage($"Market {market.Symbol} already has the most bots allowed per market ({appSettings.MaxNumberOfBotsPerMarket})");
                            continue;
                        }

                        var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfiguration.GetOrAdd(configuration, key => new ConcurrentDictionary<string, bool>());

                        if (!allocatedMarketsForConfiguration.TryAdd(market.Symbol, true))
                        {
                            Logger.Instance.LogMessage($"Already allocated bot for market {market.Symbol}");
                            continue;
                        }

                        Logger.Instance.LogMessage($"Found market: {market.Symbol}");

                        Bot bot = null;
                        var existingDust = 0m;
                        try
                        {
                            existingDust = GetInheritableDust(market);
                            bot = new Bot(dataRepository, configuration, market, existingDust, UnallocateBot, botStrategiesFactory, balanceLedger);

                            AllocateBot(bot);
                            allocatedBots.Add(bot);
                        }
                        catch
                        {
                            //Stored before the failure, the bot holds its allocation, so it must run like the others (the caller starts it)
                            if (bot != null && context.GetBots().Contains(bot))
                                allocatedBots.Add(bot);
                            else
                            {
                                //Left reserved without a bot, the market would count against the cap for every configuration until a restart
                                allocatedMarketsForConfiguration.TryRemove(market.Symbol, out _);
                                if (existingDust > 0)
                                    context.AddDustForMarket(market.Symbol, existingDust);
                            }
                            throw;
                        }
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

                //Only a restore allocates after a halt: the bot is kept, so it's saved for the next session, but it doesn't trade
                if (halted)
                    haltingBots.Add(Task.Run(() => bot.Halt(haltRetryUntil)));
            }
        }

        /// <summary>
        /// Stops trading for good: no bot is allocated from now on, and every bot stops and cancels its open order. The bots keep their
        /// state and allocation, so the saved session resumes them. <see cref="Halted"/> completes once they're done.
        /// </summary>
        public void Halt(string reason)
        {
            lock (allocationLock)
            {
                if (halted)
                    return;

                halted = true;
                haltReason = reason;
                //A cancel is up to three requests (cancel, fetch, the fill's trades) of up to 10 s each; one started by then answers within HaltTimeout
                haltRetryUntil = DateTime.UtcNow + HaltTimeout - TimeSpan.FromSeconds(30);
                //Off the caller's thread: it may be a bot's failing request, holding the semaphore that bot's halt waits for
                haltingBots.AddRange(context.GetBots().ToList().Select(bot => Task.Run(() => bot.Halt(haltRetryUntil))));
            }

            Logger.Instance.LogUnexpectedError($"Halting all trading: {reason}");

            _ = HaltBots();
        }

        private async Task HaltBots()
        {
            var deadline = DateTime.UtcNow + HaltTimeout;
            try
            {
                //A restore in progress can still allocate (and halt) bots; after it, nothing allocates
                if (!await WaitUntil(restoreDone.Task, deadline))
                {
                    Logger.Instance.LogUnexpectedError($"The session restore was still running {HaltTimeout.TotalSeconds}s after the halt; stopping without the bots it allocates");
                    return;
                }

                while (true)
                {
                    Task[] halting;
                    lock (allocationLock)
                        halting = haltingBots.ToArray();

                    if (!await WaitUntil(Task.WhenAll(halting), deadline))
                    {
                        Logger.Instance.LogUnexpectedError($"Bots still halting after {HaltTimeout.TotalSeconds}s; stopping without them");
                        return;
                    }

                    lock (allocationLock)
                        if (haltingBots.Count == halting.Length)
                            return;
                }
            }
            catch (Exception e)
            {
                Logger.Instance.LogUnexpectedError($"Error while halting the bots: {e}");
            }
            finally
            {
                haltCompleted.TrySetResult();
            }
        }

        private static async Task<bool> WaitUntil(Task task, DateTime deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero && await Task.WhenAny(task, Task.Delay(remaining)) == task)
            {
                await task;
                return true;
            }

            return task.IsCompleted;
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
            var rejection = marketFilters.Rejection(marketData, spreadConfiguration);
            if (rejection == null)
                return true;

            //Most markets are below the minimum price; logging each would drown the rest
            if (!(marketData.LastTradeRate < spreadConfiguration.MinimumPrice))
                Logger.Instance.LogMessage($"Market {marketData.Symbol}: {rejection}");

            return false;
        }

        private int BotCountOnMarket(string symbol) =>
            AllocatedMarketsPerSpreadConfiguration.Values.Count(markets => markets.ContainsKey(symbol));

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

            //Coins recorded as dust on the coin's other markets are theirs to inherit
            var dustOfOtherMarkets = context.GetDustPerMarket()
                .Where(dust => dust.Key != market.Symbol && MarketData.TargetOf(dust.Key) == market.Target)
                .Sum(dust => dust.Value);
            var inheritable = Math.Max(balanceLedger.UnclaimedCoins(market.Target) - dustOfOtherMarkets, 0);

            if (recordedDust <= inheritable)
                return recordedDust;

            //Kept recorded, coins that aren't on the exchange would be inherited again and again and take other markets' real dust
            Logger.Instance.LogUnexpectedError($"Recorded dust {recordedDust}{market.Target} for {market.Symbol} exceeds the {inheritable}{market.Target} unclaimed on the exchange; inheriting only that and dropping the rest");
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
