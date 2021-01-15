using Newtonsoft.Json;
using SpreadBot.Logic;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    public class NetProfitRecorder
    {
        private readonly string PROFIT_PER_MARKET_FILE_PATH = "profitPerMarket.json".ToLocalFilePath();
        private readonly string PROFIT_PER_CONFIGURATION_FILE_PATH = "profitPerSpreadConfiguration.json".ToLocalFilePath();

        private BlockingCollection<NetProfit> pendingData;

        private Dictionary<SpreadConfiguration, decimal> netProfitPerSpreadConfiguration;
        private Dictionary<string, decimal> netProfitPerMarket;

        private NetProfitRecorder()
        {
            pendingData = new BlockingCollection<NetProfit>();
            InitializeProfitCache();

            Task.Run(ConsumePendingData);
        }

        public static NetProfitRecorder Instance { get; } = new NetProfitRecorder();

        public void RecordProfit(SpreadConfiguration spreadConfiguration, Bot bot)
        {
            decimal profit = bot.Balance - spreadConfiguration.AllocatedAmountOfBaseCurrency;

            pendingData.Add(new NetProfit() { SpreadConfiguration = spreadConfiguration, Profit = profit, Market = bot.MarketSymbol });
        }

        private void InitializeProfitCache()
        {
            if (!File.Exists(PROFIT_PER_MARKET_FILE_PATH))
                netProfitPerMarket = new Dictionary<string, decimal>();
            else
            {
                netProfitPerMarket = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(File.ReadAllText(PROFIT_PER_MARKET_FILE_PATH));
            }

            if (!File.Exists(PROFIT_PER_CONFIGURATION_FILE_PATH))
            {
                netProfitPerSpreadConfiguration = new Dictionary<SpreadConfiguration, decimal>();
            }
            else
            {
                var tempDic = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(File.ReadAllText(PROFIT_PER_CONFIGURATION_FILE_PATH));
                netProfitPerSpreadConfiguration = new Dictionary<SpreadConfiguration, decimal>(tempDic.Count);
                foreach (var entry in tempDic)
                {
                    netProfitPerSpreadConfiguration.Add(JsonConvert.DeserializeObject<SpreadConfiguration>(entry.Key), entry.Value);
                }
            }
        }

        private void ConsumePendingData()
        {
            foreach (var log in pendingData.GetConsumingEnumerable())
            {
                try
                {
                    netProfitPerSpreadConfiguration.TryGetValue(log.SpreadConfiguration, out decimal spreadConfigurationProfit);
                    netProfitPerSpreadConfiguration[log.SpreadConfiguration] = spreadConfigurationProfit + (log.Profit);

                    netProfitPerMarket.TryGetValue(log.Market, out decimal marketProfit);
                    netProfitPerMarket[log.Market] = marketProfit + (log.Profit);

                    File.WriteAllText(PROFIT_PER_CONFIGURATION_FILE_PATH,
                        JsonConvert.SerializeObject(netProfitPerSpreadConfiguration, Formatting.Indented));

                    File.WriteAllText(PROFIT_PER_MARKET_FILE_PATH,
                        JsonConvert.SerializeObject(netProfitPerMarket, Formatting.Indented));
                }
                catch (Exception e)
                {
                    Logger.Instance.LogUnexpectedError($"Error while consuming profit data: {e}");
                }
            }
        }

        private class NetProfit
        {
            public SpreadConfiguration SpreadConfiguration { get; set; }
            public decimal Profit { get; set; }
            public string Market { get; set; }
        }
    }
}
