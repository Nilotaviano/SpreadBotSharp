using SpreadBot.Infrastructure;
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

            this.dataRepository.SubscribeToMarketsData(guid, EvaluateMarkets);
            availableBalanceForBaseMarket = this.dataRepository.BalancesData[appSettings.BaseMarket].Amount;
        }

        public ConcurrentDictionary<Guid, HashSet<string>> AllocatedMarketsPerSpreadConfigurationId { get; } = new ConcurrentDictionary<Guid, HashSet<string>>();
        public ConcurrentDictionary<Guid, Bot> AllocatedBotsByGuid { get; } = new ConcurrentDictionary<Guid, Bot>();

        /// <summary>
        /// Evaluates updated markets for new bot-allocation opportunities
        /// </summary>
        /// <param name="marketDeltas">Markets that were updated</param>
        private void EvaluateMarkets(IEnumerable<MarketData> marketDeltas)
        {
            if (AllocatedBotsByGuid.Count >= appSettings.MaxNumberOfBots)
                return;

            //Filter only relevant markets
            marketDeltas = marketDeltas.Where(m => m.BaseMarket == appSettings.BaseMarket && m.LastTradeRate >= appSettings.MinimumPrice);

            foreach (var configuration in appSettings.SpreadConfigurations)
            {
                var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfigurationId.GetOrAdd(configuration.Guid, new HashSet<string>());

                var marketsToAllocate = marketDeltas.Where(m => !allocatedMarketsForConfiguration.Contains(m.Symbol) && EvaluateMarketBasedOnConfiguration(m, configuration));

                foreach (var market in marketsToAllocate)
                {
                    if (!CanAllocateBotForConfiguration(configuration))
                        break;

                    var bot = new Bot(appSettings, dataRepository, configuration, market, UnallocateBot);
                    AllocatedBotsByGuid[bot.Guid] = bot;
                    allocatedMarketsForConfiguration.Add(market.Symbol);

                    balanceSemaphore.Wait();
                    availableBalanceForBaseMarket -= configuration.AllocatedAmountOfBaseCurrency;
                    balanceSemaphore.Release();

                    bot.Start();
                }

                if (!CanAllocateBotForConfiguration(configuration))
                    break;
            }
        }

        private bool EvaluateMarketBasedOnConfiguration(MarketData marketData, SpreadConfiguration spreadConfiguration)
        {
            return marketData.PercentChange <= spreadConfiguration.MaxPercentChangeFromPreviousDay
                && marketData.QuoteVolume >= spreadConfiguration.MinimumQuoteVolume
                && marketData.SpreadPercentage >= spreadConfiguration.MinimumSpreadPercentage;
        }

        private bool CanAllocateBotForConfiguration(SpreadConfiguration spreadConfiguration)
        {
            return AllocatedBotsByGuid.Count < appSettings.MaxNumberOfBots
                && availableBalanceForBaseMarket > spreadConfiguration.AllocatedAmountOfBaseCurrency;
        }

        private void UnallocateBot(Bot bot)
        {
            Debug.Assert(AllocatedBotsByGuid.TryRemove(bot.Guid, out _), "Bot should have been removed successfully");
            Debug.Assert(AllocatedMarketsPerSpreadConfigurationId[bot.SpreadConfigurationGuid].Remove(bot.MarketSymbol));

            balanceSemaphore.Wait();
            availableBalanceForBaseMarket += bot.Balance;
            balanceSemaphore.Release();
        }

        public void Dispose()
        {
            this.dataRepository.UnsubscribeToMarketsData(guid);
        }
    }
}
