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

        private BlockingCollection<NetProfitMessage> pendingData;

        private HashSet<SpreadConfigurationNetProfit> netProfitPerSpreadConfiguration;
        private HashSet<MarketNetProfit> netProfitPerMarket;

        private NetProfitRecorder()
        {
            pendingData = new BlockingCollection<NetProfitMessage>();
            InitializeProfitCache();

            Task.Run(ConsumePendingData);
        }

        public static NetProfitRecorder Instance { get; } = new NetProfitRecorder();

        public AppSettings AppSettings { get; set; }

        public void RecordProfit(SpreadConfiguration spreadConfiguration, Bot bot)
        {
            decimal profit = bot.Balance - spreadConfiguration.AllocatedAmountOfBaseCurrency;

            pendingData.Add(new NetProfitMessage() { SpreadConfiguration = spreadConfiguration, Profit = profit, Market = bot.MarketSymbol });
        }

        private void InitializeProfitCache()
        {
            if (!File.Exists(PROFIT_PER_MARKET_FILE_PATH))
                netProfitPerMarket = new HashSet<MarketNetProfit>();
            else
            {
                List<MarketNetProfit> list = JsonConvert.DeserializeObject<List<MarketNetProfit>>(File.ReadAllText(PROFIT_PER_MARKET_FILE_PATH));
                netProfitPerMarket = new HashSet<MarketNetProfit>(list);
            }

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
                    var spreadConfigProfit = netProfitPerSpreadConfiguration.SingleOrDefault(x => x.Id == log.SpreadConfiguration.Id);

                    if (spreadConfigProfit == null)
                    {
                        spreadConfigProfit = new SpreadConfigurationNetProfit(log.SpreadConfiguration);
                        netProfitPerSpreadConfiguration.Add(spreadConfigProfit);
                    }

                    spreadConfigProfit.Profit += log.Profit;

                    //Update active/inactive configurations
                    //TODO improve complexity
                    foreach (SpreadConfigurationNetProfit active in netProfitPerSpreadConfiguration.Intersect(AppSettings.SpreadConfigurations))
                        active.IsActive = true;

                    foreach (SpreadConfigurationNetProfit inactive in netProfitPerSpreadConfiguration.Except(AppSettings.SpreadConfigurations))
                        inactive.IsActive = false;

                    //Calculate netProfitPerMarket 
                    var marketNetProfit = netProfitPerMarket.SingleOrDefault(x => x.Market == log.Market);

                    if (marketNetProfit == null)
                    {
                        marketNetProfit = new MarketNetProfit() { Market = log.Market };
                        netProfitPerMarket.Add(marketNetProfit);
                    }

                    marketNetProfit.Profit += log.Profit;

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

        private class NetProfitMessage
        {
            public SpreadConfiguration SpreadConfiguration { get; set; }
            public decimal Profit { get; set; }
            public string Market { get; set; }
        }

        private class SpreadConfigurationNetProfit : SpreadConfiguration
        {
            public SpreadConfigurationNetProfit() { }
            public SpreadConfigurationNetProfit(SpreadConfiguration spreadConfiguration)
            {
                this.Id = spreadConfiguration.Id;
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

            public bool IsActive { get; set; }

            private decimal profit;

            public decimal Profit
            {
                get => profit;
                set
                {
                    this.profit = value;

                    if (this.profit < this.MinProfit)
                        this.MinProfit = this.profit;

                    if (this.profit > this.MaxProfit)
                        this.MaxProfit = this.profit;
                }
            }

            public decimal MaxProfit { get; set; }

            public decimal MinProfit { get; set; }

            public override bool Equals(object obj)
            {
                return Equals(obj as SpreadConfigurationNetProfit);
            }

            public bool Equals(SpreadConfigurationNetProfit obj)
            {
                return obj?.Id == this.Id;
            }

            public override int GetHashCode()
            {
                return Convert.ToInt32(this.Id, 16);
            }

            public override string ToString()
            {
                return JsonConvert.SerializeObject(this);
            }
        }

        private class MarketNetProfit
        {
            public string Market { get; set; }

            private decimal profit;

            public decimal Profit
            {
                get => profit;
                set
                {
                    this.profit = value;

                    if (this.profit < this.MinProfit)
                        this.MinProfit = this.profit;

                    if (this.profit > this.MaxProfit)
                        this.MaxProfit = this.profit;
                }
            }

            public decimal MaxProfit { get; set; }

            public decimal MinProfit { get; set; }
        }
    }
}
