using Newtonsoft.Json;
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

        private static NetProfitRecorder instance;

        private NetProfitRecorder()
        {
            pendingData = new BlockingCollection<NetProfit>();

            netProfitPerSpreadConfiguration = new Dictionary<SpreadConfiguration, decimal>();

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

        public void RecordProfit(SpreadConfiguration spreadConfiguration, decimal finalBalance)
        {
            decimal profit = finalBalance - spreadConfiguration.AllocatedAmountOfBaseCurrency;

            pendingData.Add(new NetProfit() { SpreadConfiguration = spreadConfiguration, Profit = profit });
        }

        private void ConsumePendingData()
        {
            foreach (var log in pendingData.GetConsumingEnumerable())
            {
                netProfitPerSpreadConfiguration.TryGetValue(log.SpreadConfiguration, out decimal spreadConfigurationProfit);
                netProfitPerSpreadConfiguration[log.SpreadConfiguration] = spreadConfigurationProfit + (log.Profit);

                string jsonNetProfit = JsonConvert.SerializeObject(netProfitPerSpreadConfiguration, Formatting.Indented);
                Logger.Instance.LogMessage($"Recorded profit:{Environment.NewLine}{jsonNetProfit}");

                File.WriteAllText(Path.Combine(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, "..", "netprofit.json"), jsonNetProfit);
            }
        }

        private class NetProfit
        {
            public SpreadConfiguration SpreadConfiguration { get; set; }
            public decimal Profit { get; set; }
        }
    }
}
