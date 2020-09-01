using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace SpreadBot.Logic
{
    public class Coordinator
    {
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private decimal availableBalanceForBaseMarket;

        public Coordinator(AppSettings appSettings, DataRepository dataRepository)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;

            this.dataRepository.SubscribeToMarketsData(EvaluateMarkets);
            availableBalanceForBaseMarket = this.dataRepository.BalancesData[appSettings.BaseMarket].CurrencyAbbreviation;
        }


        public ConcurrentDictionary<int, HashSet<string>> AllocatedMarketsPerSpreadConfigurationId { get; } = new ConcurrentDictionary<int, HashSet<string>>();
        public ConcurrentBag<Bot> AllocatedBots { get; } = new ConcurrentBag<Bot>();

        /// <summary>
        /// Evaluates updated markets for new bot-allocation opportunities
        /// </summary>
        /// <param name="marketDeltas">Markets that were updated</param>
        private void EvaluateMarkets(IEnumerable<MarketData> marketDeltas)
        {
            if (AllocatedBots.Count >= appSettings.MaxNumberOfBots)
                return;

            //Filter only relevant markets
            marketDeltas = marketDeltas.Where(m => m.BaseMarket == appSettings.BaseMarket && m.LastTradeRate >= appSettings.MinimumPrice);

            foreach (var configuration in appSettings.SpreadConfigurations)
            {
                var allocatedMarketsForConfiguration = AllocatedMarketsPerSpreadConfigurationId.GetOrAdd(configuration.Id, new HashSet<string>());

                //TODO Verify if AsParallel would speed this up
                var marketsToAllocate = marketDeltas.Where(m => !allocatedMarketsForConfiguration.Contains(m.Symbol) && EvaluateMarketBasedOnConfiguration(m, configuration));

                foreach (var market in marketsToAllocate)
                {
                    if (!CanAllocateBotForConfiguration(configuration))
                        break;

                    allocatedMarketsForConfiguration.Add(market.Symbol);
                    AllocatedBots.Add(new Bot(appSettings, dataRepository, configuration, market));

                    //TODO: This is not atomic, so we might end up running into issues if unallocating a bot is done in parallel (or any other operation that changes availableBalanceForBaseMarket)
                    availableBalanceForBaseMarket -= configuration.AllocatedAmountOfBaseCurrency;
                }

                if (!CanAllocateBotForConfiguration(configuration))
                    break;
            }
        }

        private bool EvaluateMarketBasedOnConfiguration(MarketData marketData, SpreadConfiguration spreadConfiguration)
        {
            return marketData.PercentChange <= spreadConfiguration.MaxPercentChangeFromPreviousDay
                && marketData.QuoteVolume >= spreadConfiguration.MinimumQuoteVolume
                && marketData.Spread >= spreadConfiguration.MinimumSpread;
        }

        private bool CanAllocateBotForConfiguration(SpreadConfiguration spreadConfiguration)
        {
            return AllocatedBots.Count < appSettings.MaxNumberOfBots
                && availableBalanceForBaseMarket > spreadConfiguration.AllocatedAmountOfBaseCurrency;
        }
    }
}
