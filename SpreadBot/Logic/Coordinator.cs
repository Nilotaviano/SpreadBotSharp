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
        private readonly Guid guid;
        private ConcurrentDictionary<string, decimal> availableBalanceForBaseMarket;
        private Dictionary<string, IGrouping<string, SpreadConfiguration>> configurationsByBaseMarket;

        private readonly SemaphoreQueue balanceSemaphore = new SemaphoreQueue(1, 1);

        public ConcurrentDictionary<SpreadConfiguration, ConcurrentDictionary<string, bool>> AllocatedMarketsPerSpreadConfiguration { get; } = new ConcurrentDictionary<SpreadConfiguration, ConcurrentDictionary<string, bool>>();

        public Coordinator(AppSettings appSettings, DataRepository dataRepository, ICoordinatorContext context)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.context = context;
            this.guid = new Guid();

            availableBalanceForBaseMarket = new ConcurrentDictionary<string, decimal>();

            this.appSettings.Reloaded += AppSettings_Reloaded;

            UpdateAppSettings();
        }

        private void AppSettings_Reloaded(object sender, EventArgs e)
        {
            UpdateAppSettings();
        }

        private void UpdateAppSettings()
        {
            configurationsByBaseMarket?.Clear();

            configurationsByBaseMarket = appSettings.SpreadConfigurations.GroupBy(c => c.BaseMarket).ToDictionary(group => group.Key);

            // TODO This may break when restoring bots for different base markets
            foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
            {
                availableBalanceForBaseMarket.TryAdd(baseMarket, this.dataRepository.BalancesData[baseMarket].Amount);
            }
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
                    var bot = new Bot(dataRepository, botContext, UnallocateBot, botStrategiesFactory);

                    var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfiguration.GetOrAdd(bot.botContext.spreadConfiguration, key => new ConcurrentDictionary<string, bool>());

                    allocatedMarketsForConfiguration.TryAdd(bot.MarketSymbol, true);

                    AllocateBot(bot);

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

                        // TODO keep dust values between executions
                        var existingDust = context.RemoveDustForMarket(market.Symbol);
                        var bot = new Bot(dataRepository, configuration, market, existingDust, UnallocateBot, botStrategiesFactory);

                        AllocateBot(bot);
                    }

                    if (!anyConfigurationAvailable)
                        break;
                }
            }
        }

        private void AllocateBot(Bot bot)
        {
            ExecuteBalanceRelatedAction($"allocating bot {bot.Guid}", () =>
            {
                context.AddBot(bot);
                availableBalanceForBaseMarket.AddOrUpdate(
                    bot.BaseMarket,
                    bot.Balance * -1,
                    (b, oldValue) => oldValue - bot.Balance
                );

                Logger.Instance.LogMessage($"Granted {bot.Balance}{bot.BaseMarket} to bot {bot.Guid}. Total available balance: {availableBalanceForBaseMarket[bot.BaseMarket]}{bot.BaseMarket}");
                bot.Start();
            });
        }

        private bool IsViableMarket(MarketData market)
        {
            return string.IsNullOrWhiteSpace(market.Notice) && market.Status == EMarketStatus.Online;
        }

        private void ReportBalance()
        {
            ExecuteBalanceRelatedAction("reporting balance", () =>
            {
                foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
                {
                    decimal balanceForBaseMarket = context.GetBots().Where(b => b.BaseMarket == baseMarket).Aggregate(availableBalanceForBaseMarket[baseMarket], (value, bot) => value + bot.Balance + (bot.HeldAmount * bot.LastTradeRate));
                    BalanceReporter.Instance.ReportBalance(balanceForBaseMarket, baseMarket);
                }
            });
        }

        private (decimal, decimal) GetMarketOrderKey(MarketData marketData)
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

            if (spreadConfiguration.AvoidTokenizedSecurities && marketData.IsTokenizedSecurity.GetValueOrDefault())
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is a tokenized security");
                return false;
            }

            if (spreadConfiguration.AvoidLeveragedTokens && marketData.IsLeveragedToken)
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is a tokenized security");
                return false;
            }

            return true;
        }

        private bool CanAllocateBotForConfiguration(SpreadConfiguration spreadConfiguration)
        {
            return context.GetBotCount() < appSettings.MaxNumberOfBots
                && availableBalanceForBaseMarket.TryGetValue(spreadConfiguration.BaseMarket, out var balance) 
                && balance > spreadConfiguration.AllocatedAmountOfBaseCurrency;
        }

        private void UnallocateBot(Bot bot)
        {
            ExecuteBalanceRelatedAction($"unallocating bot {bot.Guid}", () =>
            {
                bool removeAllocatedBot = context.RemoveBot(bot.Guid);
                bool removedAllocatedMarket = AllocatedMarketsPerSpreadConfiguration[bot.botContext.spreadConfiguration].TryRemove(bot.MarketSymbol, out _);

                if (!removeAllocatedBot)
                    Logger.Instance.LogUnexpectedError($"Couldn't remove allocated bot {bot.Guid}");

                if (!removedAllocatedMarket)
                    Logger.Instance.LogUnexpectedError($"Couldn't remove allocated market {bot.MarketSymbol}");

                Debug.Assert(removeAllocatedBot, "Bot should have been removed successfully");
                Debug.Assert(removedAllocatedMarket, $"Market {bot.MarketSymbol} had already been deallocated from configuration {bot.botContext.spreadConfiguration.Guid}");

                availableBalanceForBaseMarket.AddOrUpdate(bot.BaseMarket, bot.Balance, (key, oldBalance) => oldBalance + bot.Balance);
                Logger.Instance.LogMessage($"Recovered {bot.Balance}{bot.BaseMarket} from bot {bot.Guid}. Total available balance: {availableBalanceForBaseMarket[bot.BaseMarket]}{bot.BaseMarket}");

                if (bot.HeldAmount > 0)
                    context.AddDustForMarket(bot.MarketSymbol, bot.HeldAmount);
            });
        }

        private static IComparer<(decimal, decimal)> GetMarketComparer()
        {
            return Comparer<(decimal, decimal)>.Create((key1, key2) =>
            {
                return (key2.Item1 * key2.Item2).CompareTo(key1.Item1 * key2.Item2);
                //// Descending order
                //var result = key2.Item1.CompareTo(key1.Item1);
                //if (result == 0)
                //    return key2.Item2.CompareTo(key1.Item2);
                //return result;
            });
        }

        private void ExecuteBalanceRelatedAction(string description, Action action)
        {
            balanceSemaphore.Wait();

            try
            {
                Logger.Instance.LogMessage($"Lock acquired for {description}");

                action();
            } catch (Exception e)
            {
                Logger.Instance.LogError($"Error while {description}. Exception: {e}");
                throw e;
            }
            finally
            {
                balanceSemaphore.Release();
                Logger.Instance.LogMessage($"Lock released after {description}");
            }
        }

        public void Dispose()
        {
            this.dataRepository.UnsubscribeToMarketsData(guid);
        }
    }
}
