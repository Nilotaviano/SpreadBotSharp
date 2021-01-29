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

        public ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> AllocatedMarketsPerSpreadConfigurationId { get; } = new ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>>();

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

            foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
            {
                availableBalanceForBaseMarket.TryAdd(baseMarket, this.dataRepository.BalancesData[baseMarket].Amount);
            }
        }

        public void Start()
        {
            // StartCurrentBots();
            this.dataRepository.SubscribeToMarketsData(guid, EvaluateMarkets);
            foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
                this.dataRepository.SubscribeToCurrencyBalance(baseMarket, guid, (bd) => ReportBalance());
        }

        private async void StartCurrentBots()
        {
            foreach (var bot in await context.Initialize(dataRepository, UnallocateBot, botStrategiesFactory))
            {
                var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfigurationId.GetOrAdd(bot.SpreadConfigurationGuid, key => new ConcurrentDictionary<string, bool>());

                allocatedMarketsForConfiguration.TryAdd(bot.MarketSymbol, true);

                AllocateBot(bot);

                Logger.Instance.LogMessage($"Allocated existing bot for market {bot.MarketSymbol}");
            }
        }

        /// <summary>
        /// Evaluates updated markets for new bot-allocation opportunities
        /// </summary>
        /// <param name="marketDeltas">Markets that were updated</param>
        private async void EvaluateMarkets(IEnumerable<MarketData> marketDeltas)
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
                            Console.WriteLine($"Not enough balance/bots for market {market.Symbol}");
                            continue;
                        }

                        anyConfigurationAvailable = true;

                        if (!EvaluateMarketBasedOnConfiguration(market, configuration))
                            continue;

                        var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfigurationId.GetOrAdd(configuration.Guid, key => new ConcurrentDictionary<string, bool>());

                        if (!allocatedMarketsForConfiguration.TryAdd(market.Symbol, true))
                        {
                            Logger.Instance.LogMessage($"Already allocated bot for market {market.Symbol}");
                            continue;
                        }

                        Logger.Instance.LogMessage($"Found market: {market.Symbol}");

                        // TODO keep dust values between executions
                        var existingDust = await context.RemoveDustForMarket(market.Symbol);
                        var bot = new Bot(dataRepository, configuration, market, existingDust, UnallocateBot, botStrategiesFactory);
                        await context.AddBot(bot);

                        AllocateBot(bot);
                    }

                    if (!anyConfigurationAvailable)
                        break;
                }
            }
        }

        private void AllocateBot(Bot bot)
        {
            balanceSemaphore.Wait();
            availableBalanceForBaseMarket.AddOrUpdate(
                bot.BaseMarket,
                bot.Balance * -1,
                (b, oldValue) => oldValue - bot.Balance
            );

            Logger.Instance.LogMessage($"Granted {bot.Balance}{bot.BaseMarket} to bot {bot.Guid}. Total available balance: {availableBalanceForBaseMarket[bot.BaseMarket]}{bot.BaseMarket}");
            bot.Start();
            balanceSemaphore.Release();
        }

        private bool IsViableMarket(MarketData market)
        {
            return string.IsNullOrWhiteSpace(market.Notice) && market.Status == EMarketStatus.Online;
        }

        private void ReportBalance()
        {
            balanceSemaphore.Wait();
            
            foreach (var baseMarket in configurationsByBaseMarket.Keys.ToList())
                BalanceReporter.Instance.ReportBalance(availableBalanceForBaseMarket[baseMarket], context.GetBots().Where(b => b.BaseMarket == baseMarket), baseMarket);

            balanceSemaphore.Release();
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
                Console.WriteLine($"Market {marketData.Symbol} has not enough volume ({marketData.QuoteVolume}) or spread ({marketData.SpreadPercentage})");
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
                && availableBalanceForBaseMarket.TryGetValue(spreadConfiguration.BaseMarket, out var balance) 
                && balance > spreadConfiguration.AllocatedAmountOfBaseCurrency;
        }

        private async void UnallocateBot(Bot bot)
        {
            balanceSemaphore.Wait();
            bool removeAllocatedBot = await context.RemoveBot(bot.Guid);
            bool removedAllocatedMarket = AllocatedMarketsPerSpreadConfigurationId[bot.SpreadConfigurationGuid].TryRemove(bot.MarketSymbol, out _);

            if (!removeAllocatedBot)
                Logger.Instance.LogUnexpectedError($"Couldn't remove allocated bot {bot.Guid}");

            if (!removedAllocatedMarket)
                Logger.Instance.LogUnexpectedError($"Couldn't remove allocated market {bot.MarketSymbol}");

            Debug.Assert(removeAllocatedBot, "Bot should have been removed successfully");
            Debug.Assert(removedAllocatedMarket, $"Market {bot.MarketSymbol} had already been deallocated from configuration {bot.SpreadConfigurationGuid}");

            availableBalanceForBaseMarket.AddOrUpdate(bot.BaseMarket, bot.Balance, (key, oldBalance) => oldBalance + bot.Balance);
            Logger.Instance.LogMessage($"Recovered {bot.Balance}{bot.BaseMarket} from bot {bot.Guid}. Total available balance: {availableBalanceForBaseMarket[bot.BaseMarket]}{bot.BaseMarket}");
            balanceSemaphore.Release();

            if (bot.HeldAmount > 0)
                await context.AddDustForMarket(bot.MarketSymbol, bot.HeldAmount);
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

        public void Dispose()
        {
            this.dataRepository.UnsubscribeToMarketsData(guid);
        }
    }
}
