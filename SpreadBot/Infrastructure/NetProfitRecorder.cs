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

            Task.Run(ConsumePendingData);
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
            {
                List<SpreadConfigurationNetProfit> list = JsonConvert.DeserializeObject<List<SpreadConfigurationNetProfit>>(File.ReadAllText(PROFIT_PER_CONFIGURATION_FILE_PATH));
                netProfitPerSpreadConfiguration = new HashSet<SpreadConfigurationNetProfit>(list);
            }
        }

        private void ConsumePendingData()
        {
            foreach (var log in pendingData.GetConsumingEnumerable())
            {
                try
                {
                    //Calculate SpreadConfigurationNetProfit for current SpreadConfiguration
                    var obj = netProfitPerSpreadConfiguration.SingleOrDefault(x => x.GetHashCode() == log.SpreadConfiguration.GetHashCode());

                    if (obj == null)
                    {
                        obj = new SpreadConfigurationNetProfit(log.SpreadConfiguration);
                        netProfitPerSpreadConfiguration.Add(obj);
                    }

                    obj.Profit += log.Profit;

                    //Update active/inactive configurations
                    //TODO improve complexity
                    foreach (var active in netProfitPerSpreadConfiguration.Where(x => AppSettings.SpreadConfigurations.Any(y => y.GetHashCode() == x.GetHashCode())))
                        active.IsActive = true;

                    foreach(var inactive in netProfitPerSpreadConfiguration.Where(x => !AppSettings.SpreadConfigurations.Any(y => y.GetHashCode() == x.GetHashCode())))
                        inactive.IsActive = false;

                    //Calculate netProfitPerMarket 
                    netProfitPerMarket.TryGetValue(log.Market, out decimal marketProfit);
                    netProfitPerMarket[log.Market] = marketProfit + (log.Profit);

                    //Write to files
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

        private class SpreadConfigurationNetProfit
        {
            public SpreadConfigurationNetProfit() { }
            public SpreadConfigurationNetProfit(SpreadConfiguration spreadConfiguration)
            {
                this.AllocatedAmountOfBaseCurrency = spreadConfiguration.AllocatedAmountOfBaseCurrency;
                this.AvoidTokenizedSecurities = spreadConfiguration.AvoidTokenizedSecurities;
                this.BaseMarket = spreadConfiguration.BaseMarket;
                this.MaxPercentChangeFromPreviousDay = spreadConfiguration.MaxPercentChangeFromPreviousDay;
                this.MinimumNegotiatedAmount = spreadConfiguration.MinimumNegotiatedAmount;
                this.MinimumPrice = spreadConfiguration.MinimumPrice;
                this.MinimumProfitPercentage = spreadConfiguration.MinimumProfitPercentage;
                this.MinimumQuoteVolume = spreadConfiguration.MinimumQuoteVolume;
                this.MinimumSpreadPercentage = spreadConfiguration.MinimumSpreadPercentage;
                this.MinutesForLoss = spreadConfiguration.MinutesForLoss;
                this.SpreadThresholdBeforeCancelingCurrentOrder = spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder;
            }

            public string BaseMarket { get; set; }
            public decimal MaxPercentChangeFromPreviousDay { get; set; }
            public decimal MinimumSpreadPercentage { get; set; }
            public decimal MinimumQuoteVolume { get; set; }
            public decimal MinimumPrice { get; set; }
            public decimal MinimumNegotiatedAmount { get; set; } //Dust limit
            public decimal AllocatedAmountOfBaseCurrency { get; set; }
            public decimal SpreadThresholdBeforeCancelingCurrentOrder { get; set; } = 1.Satoshi(); //default 1 satoshi, but should be set higher, I think
            public int MinutesForLoss { get; set; }
            public decimal MinimumProfitPercentage { get; set; } //Bot will try to sell with at least this amount of profit, until past the MinutesForLoss threshold
            public bool AvoidTokenizedSecurities { get; set; }

            //Shouldn't be used on GetHashCode
            public bool IsActive { get; set; }
            public decimal Profit { get; set; }

            public override int GetHashCode()
            {
                return (
                    BaseMarket,
                    MaxPercentChangeFromPreviousDay,
                    MinimumSpreadPercentage,
                    MinimumQuoteVolume,
                    AllocatedAmountOfBaseCurrency,
                    SpreadThresholdBeforeCancelingCurrentOrder,
                    MinutesForLoss,
                    MinimumProfitPercentage,
                    MinimumPrice,
                    MinimumNegotiatedAmount
                ).GetHashCode();
            }
            public override bool Equals(object obj)
            {
                return Equals(obj as SpreadConfigurationNetProfit);
            }

            public bool Equals(SpreadConfigurationNetProfit obj)
            {
                return obj != null
                    && obj.BaseMarket == this.BaseMarket
                    && obj.MaxPercentChangeFromPreviousDay == this.MaxPercentChangeFromPreviousDay
                    && obj.MinimumSpreadPercentage == this.MinimumSpreadPercentage
                    && obj.MinimumQuoteVolume == this.MinimumQuoteVolume
                    && obj.AllocatedAmountOfBaseCurrency == this.AllocatedAmountOfBaseCurrency
                    && obj.SpreadThresholdBeforeCancelingCurrentOrder == this.SpreadThresholdBeforeCancelingCurrentOrder
                    && obj.MinutesForLoss == this.MinutesForLoss
                    && obj.MinimumProfitPercentage == this.MinimumProfitPercentage
                    && obj.MinimumPrice == this.MinimumPrice
                    && obj.MinimumNegotiatedAmount == this.MinimumNegotiatedAmount;
            }

            public override string ToString()
            {
                return JsonConvert.SerializeObject(this);
            }
        }
    }
}
