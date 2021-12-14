using Newtonsoft.Json;
using SpreadBot.Logic;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;

namespace SpreadBot.Infrastructure
{
    public class BalanceReporter
    {
        private BlockingCollection<BalanceReportData> pendingBalanceReportData;

        private ConcurrentDictionary<string, decimal> lastReportedBalances = new ConcurrentDictionary<string, decimal>();

        private TelegramSettings telegramSettings;

        private BalanceReporter()
        {
            string telegramSettingsPath = "telegramSettings.json".ToLocalFilePath();
            if (File.Exists(telegramSettingsPath))
            {
                telegramSettings = JsonConvert.DeserializeObject<TelegramSettings>(File.ReadAllText(telegramSettingsPath));

                pendingBalanceReportData = new BlockingCollection<BalanceReportData>();
                Task.Run(ConsumePendingData);
            }
        }

        public static BalanceReporter Instance { get; } = new BalanceReporter();

        public void ReportBalance(decimal balanceForBaseMarket, string baseMarket)
        {
            pendingBalanceReportData?.Add(new BalanceReportData() { BalanceForBaseMarket = balanceForBaseMarket, BaseMarket = baseMarket });
        }

        private void ConsumePendingData()
        {
            foreach (var data in pendingBalanceReportData.GetConsumingEnumerable())
            {
                try
                {
                    var lastReportedBalance = lastReportedBalances.GetOrAdd(data.BaseMarket, 0);

                    if (Math.Abs(data.BalanceForBaseMarket - lastReportedBalance) >= telegramSettings.ChangeThresholdPerMarket[data.BaseMarket])
                    {
                        var emoji = data.BalanceForBaseMarket > lastReportedBalance ? "🚀" : "📉";

                        if (lastReportedBalance == 0)
                            emoji = string.Empty;

                        TelegramBot.Instance.SendTextMessageAsync($"{data.BalanceForBaseMarket} {data.BaseMarket} {emoji}").Wait();
                        lastReportedBalances.AddOrUpdate(data.BaseMarket, data.BalanceForBaseMarket, (key, bal) => data.BalanceForBaseMarket);
                    }
                }
                catch (Exception e)
                {
                    Logger.Instance.LogUnexpectedError($"Error while consuming balance report data: {e}");
                }
            }
        }

        private class BalanceReportData
        {
            public decimal BalanceForBaseMarket { get; set; }
            public string BaseMarket { get; set; }
        }
    }
}
