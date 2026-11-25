using Newtonsoft.Json;
using SpreadBot.Logic;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    public class NetProfitRecorder
    {
        private readonly string PROFIT_PER_MARKET_FILE_PATH = "profitPerMarket.json".ToLocalFilePath();
        private readonly string PROFIT_PER_CONFIGURATION_FILE_PATH = "profitPerSpreadConfiguration.json".ToLocalFilePath();

        private BlockingCollection<NetProfit> pendingData;

        private HashSet<SpreadConfigurationNetProfit> netProfitPerSpreadConfiguration;
        private Dictionary<string, decimal> netProfitPerMarket;

        private NetProfitRecorder()
        {
            pendingData = new BlockingCollection<NetProfit>();
            InitializeProfitCache();

            Task.Factory.StartNew(ConsumePendingData, System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public static NetProfitRecorder Instance { get; } = new NetProfitRecorder();

        public AppSettings AppSettings { get; set; }

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
                netProfitPerMarket = JsonConvert.DeserializeObject<Dictionary<string, decimal>>(File.ReadAllText(PROFIT_PER_MARKET_FILE_PATH));

            if (!File.Exists(PROFIT_PER_CONFIGURATION_FILE_PATH))
                netProfitPerSpreadConfiguration = new HashSet<SpreadConfigurationNetProfit>();
            else
                netProfitPerSpreadConfiguration = ParseConfigurationProfits(File.ReadAllText(PROFIT_PER_CONFIGURATION_FILE_PATH));
        }

        internal static HashSet<SpreadConfigurationNetProfit> ParseConfigurationProfits(string json)
        {
            //A file written when configurations compared other fields can hold several records of what is now one configuration
            return JsonConvert.DeserializeObject<List<SpreadConfigurationNetProfit>>(json)
                .GroupBy(record => record)
                .Select(records =>
                {
                    var merged = records.First();
                    merged.Profit = records.Sum(record => record.Profit);
                    merged.IsActive = records.Any(record => record.IsActive);
                    return merged;
                })
                .ToHashSet();
        }

        private void ConsumePendingData()
        {
            foreach (var log in pendingData.GetConsumingEnumerable())
            {
                try
                {
                    //Calculate SpreadConfigurationNetProfit for current SpreadConfiguration
                    var obj = netProfitPerSpreadConfiguration.SingleOrDefault(x => x.Equals(log.SpreadConfiguration));

                    if (obj == null)
                    {
                        obj = SpreadConfigurationNetProfit.For(log.SpreadConfiguration);
                        netProfitPerSpreadConfiguration.Add(obj);
                    }

                    obj.Profit += log.Profit;

                    //Update active/inactive configurations
                    //TODO improve complexity
                    foreach (var active in netProfitPerSpreadConfiguration.Where(x => AppSettings.SpreadConfigurations.Any(y => y.Equals(x))))
                        active.IsActive = true;

                    foreach(var inactive in netProfitPerSpreadConfiguration.Where(x => !AppSettings.SpreadConfigurations.Any(y => y.Equals(x))))
                        inactive.IsActive = false;

                    //Calculate netProfitPerMarket 
                    netProfitPerMarket.TryGetValue(log.Market, out decimal marketProfit);
                    netProfitPerMarket[log.Market] = marketProfit + (log.Profit);

                    //Write to files
                    Extensions.WriteAllTextAtomic(PROFIT_PER_CONFIGURATION_FILE_PATH,
                        JsonConvert.SerializeObject(netProfitPerSpreadConfiguration, Formatting.Indented));

                    Extensions.WriteAllTextAtomic(PROFIT_PER_MARKET_FILE_PATH,
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

        //Inherits the configuration's fields and equality, so a record matches its configuration and the file keeps one flat object per record
        internal class SpreadConfigurationNetProfit : SpreadConfiguration
        {
            public static SpreadConfigurationNetProfit For(SpreadConfiguration spreadConfiguration) =>
                JsonConvert.DeserializeObject<SpreadConfigurationNetProfit>(JsonConvert.SerializeObject(spreadConfiguration));

            //Not part of the configuration's equality
            public bool IsActive { get; set; }
            public decimal Profit { get; set; }
        }
    }
}
