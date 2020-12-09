using SpreadBot.Infrastructure;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

namespace SpreadBot.Logic
{
    public class Coordinator : IDisposable
    {
        private readonly IComparer<(decimal, decimal)> marketComparer = GetMarketComparer();
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly Guid guid;
        private decimal availableBalanceForBaseMarket;

        private readonly SemaphoreQueue balanceSemaphore = new SemaphoreQueue(1, 1);

        public Coordinator(AppSettings appSettings, DataRepository dataRepository)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.guid = new Guid();

            availableBalanceForBaseMarket = this.dataRepository.BalancesData[appSettings.BaseMarket].Amount;
        }

        public void Start()
        {
            this.dataRepository.SubscribeToMarketsData(guid, EvaluateMarkets);
        }

        public ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>> AllocatedMarketsPerSpreadConfigurationId { get; } = new ConcurrentDictionary<Guid, ConcurrentDictionary<string, bool>>();
        public ConcurrentDictionary<Guid, Bot> AllocatedBotsByGuid { get; } = new ConcurrentDictionary<Guid, Bot>();

        /// <summary>
        /// Evaluates updated markets for new bot-allocation opportunities
        /// </summary>
        /// <param name="marketDeltas">Markets that were updated</param>
        private void EvaluateMarkets(IEnumerable<MarketData> marketDeltas)
        {
            if (AllocatedBotsByGuid.Count >= appSettings.MaxNumberOfBots)
            {
                Logger.Instance.LogMessage($"Max number of bots reached");
                return;
            }

            //Filter only relevant markets
            marketDeltas = marketDeltas.Where(m => m.BaseMarket == appSettings.BaseMarket && m.LastTradeRate >= appSettings.MinimumPrice);

            foreach (var configuration in appSettings.SpreadConfigurations)
            {
                var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfigurationId.GetOrAdd(configuration.Guid, key => new ConcurrentDictionary<string, bool>());

                var marketsToAllocate = marketDeltas.OrderBy(GetMarketOrderKey, marketComparer)
                    .Where(m => EvaluateMarketBasedOnConfiguration(m, configuration));

                foreach (var market in marketsToAllocate)
                {
                    bool alreadyAllocatedBot = !allocatedMarketsForConfiguration.TryAdd(market.Symbol, true);

                    if (alreadyAllocatedBot)
                        Logger.Instance.LogMessage($"Already allocated bot for market {market.Symbol}");

                    if (!CanAllocateBotForConfiguration(configuration) || alreadyAllocatedBot)
                        break;

                    Logger.Instance.LogMessage($"Found market: {market.Symbol}");

                    var bot = new Bot(appSettings, dataRepository, configuration, market, UnallocateBot, new BotStrategiesFactory());
                    AllocatedBotsByGuid[bot.Guid] = bot;

                    balanceSemaphore.Wait();
                    availableBalanceForBaseMarket -= configuration.AllocatedAmountOfBaseCurrency;
                    Logger.Instance.LogMessage($"Granted {configuration.AllocatedAmountOfBaseCurrency}{appSettings.BaseMarket} to bot {bot.Guid}. Total available balance: {availableBalanceForBaseMarket}{appSettings.BaseMarket}");
                    balanceSemaphore.Release();

                    bot.Start();
                }

                if (!CanAllocateBotForConfiguration(configuration))
                    break;
            }
        }

        private (decimal, decimal) GetMarketOrderKey(MarketData marketData)
        {
            return (marketData.QuoteVolume.GetValueOrDefault(0), marketData.SpreadPercentage);
        }

        private bool EvaluateMarketBasedOnConfiguration(MarketData marketData, SpreadConfiguration spreadConfiguration)
        {
            if (marketData.SpreadPercentage < spreadConfiguration.MinimumSpreadPercentage || marketData.QuoteVolume < spreadConfiguration.MinimumQuoteVolume)
                return false;

            if (marketData.PercentChange > spreadConfiguration.MaxPercentChangeFromPreviousDay)
            {
                Logger.Instance.LogMessage($"Market {marketData.Symbol} has enough spread ({marketData.SpreadPercentage}) and volume ({marketData.QuoteVolume}), but is pumping {marketData.PercentChange}%");
                return false;
            }

            return true;
        }

        private bool CanAllocateBotForConfiguration(SpreadConfiguration spreadConfiguration)
        {
            return AllocatedBotsByGuid.Count < appSettings.MaxNumberOfBots
                && availableBalanceForBaseMarket > spreadConfiguration.AllocatedAmountOfBaseCurrency;
        }

        private void UnallocateBot(Bot bot)
        {
            bool removeAllocatedBot = AllocatedBotsByGuid.TryRemove(bot.Guid, out _);
            bool removedAllocatedMarket = AllocatedMarketsPerSpreadConfigurationId[bot.SpreadConfigurationGuid].TryRemove(bot.MarketSymbol, out _);

            if (!removeAllocatedBot)
                Logger.Instance.LogUnexpectedError($"Couldn't remove allocated bot {bot.Guid}");

            if (!removedAllocatedMarket)
                Logger.Instance.LogUnexpectedError($"Couldn't remove allocated market {bot.MarketSymbol}");


            Debug.Assert(removeAllocatedBot, "Bot should have been removed successfully");
            Debug.Assert(removedAllocatedMarket, $"Market {bot.MarketSymbol} had already been deallocated from configuration {bot.SpreadConfigurationGuid}");

            balanceSemaphore.Wait();
            availableBalanceForBaseMarket += bot.Balance;
            Logger.Instance.LogMessage($"Recovered {bot.Balance}{appSettings.BaseMarket} from bot {bot.Guid}. Total available balance: {availableBalanceForBaseMarket}{appSettings.BaseMarket}");
            balanceSemaphore.Release();
        }

        private static IComparer<(decimal, decimal)> GetMarketComparer()
        {
            return Comparer<(decimal, decimal)>.Create((key1, key2) =>
            {
                // Descending order
                var result = key2.Item1.CompareTo(key1.Item1);
                if (result == 0)
                    return key2.Item2.CompareTo(key1.Item2);
                return result;
            });
        }

        public void Dispose()
        {
            this.dataRepository.UnsubscribeToMarketsData(guid);
        }
    }
}
