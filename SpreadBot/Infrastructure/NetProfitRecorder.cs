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
        private BlockingCollection<NetProfit> pendingData;

        private Dictionary<SpreadConfiguration, decimal> netProfitPerSpreadConfiguration;
        private Dictionary<string, decimal> netProfitPerMarket;

        private static NetProfitRecorder instance;

        private NetProfitRecorder()
        {
            pendingData = new BlockingCollection<NetProfit>();

            netProfitPerSpreadConfiguration = new Dictionary<SpreadConfiguration, decimal>();
            netProfitPerMarket = new Dictionary<string, decimal>();

            Task.Run(ConsumePendingData);
        }

        public static NetProfitRecorder Instance
        {
            get
            {
                if (instance == null)
                    instance = new NetProfitRecorder();

                return instance;
            }
        }

        public void RecordProfit(SpreadConfiguration spreadConfiguration, Bot bot)
        {
            decimal profit = bot.Balance - spreadConfiguration.AllocatedAmountOfBaseCurrency;

            pendingData.Add(new NetProfit() { SpreadConfiguration = spreadConfiguration, Profit = profit, Market = bot.MarketSymbol });
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

                    File.WriteAllText("profitPerSpreadConfiguration.json".ToLocalFilePath(),
                        JsonConvert.SerializeObject(netProfitPerSpreadConfiguration, Formatting.Indented));

                    File.WriteAllText("profitPerMarket.json".ToLocalFilePath(),
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
