using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex;
using SpreadBot.Logic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SpreadBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string settingsFileName = "appsettings.json";

            if (args != null && args.Length > 0)
            {
                settingsFileName = args[0];
            }

            Console.WriteLine($"Using config file {settingsFileName}");

            string appSettingsPath = settingsFileName.ToLocalFilePath();
            Console.WriteLine(appSettingsPath);
            var appSettings = new ConfigurationBuilder()
                .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true)
                .Build()
                .Get<AppSettings>();

            var watcher = new FileSystemWatcher(Path.GetDirectoryName(appSettingsPath));
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Filter = Path.GetFileName(appSettingsPath);
            watcher.EnableRaisingEvents = true;

            void reloadAppSettings(object sender, FileSystemEventArgs e)
            {
                try
                {
                    var updatedAppSettings = new ConfigurationBuilder()
                    .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true)
                    .Build()
                    .Get<AppSettings>();

                    appSettings.Reload(updatedAppSettings);
                    Logger.Instance.LogMessage("App Settings reloaded");
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogUnexpectedError($"Error reloading app settings: {ex}");
                }
            }

            Task.Run(() => watcher.Changed += reloadAppSettings);

            var bittrex = new BittrexClient(appSettings.ApiKey, appSettings.ApiSecret);
            await bittrex.Setup();

            var dataRepository = new DataRepository(bittrex, appSettings);
            dataRepository.StartConsumingData();

            var coordinatorContext = new InMemoryCoordinatorContext();

            var coordinator = new Coordinator(appSettings, dataRepository, coordinatorContext);
            coordinator.Start();

            Console.ReadLine();
        }

        private static void TestHashCode()
        {
            SpreadConfiguration spreadConfiguration = new SpreadConfiguration();
            spreadConfiguration.BaseMarket = "BTH";
            spreadConfiguration.AllocatedAmountOfBaseCurrency = 1000;
            spreadConfiguration.MaxPercentChangeFromPreviousDay = 10;
            spreadConfiguration.MinimumProfitPercentage = 1000;
            spreadConfiguration.MinimumQuoteVolume = 345;
            spreadConfiguration.MinimumSpreadPercentage = 435;
            spreadConfiguration.MinutesForLoss = 654;
            spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder = 231;

            var dict = new Dictionary<SpreadConfiguration, int>();
            dict[spreadConfiguration] = 1;

            if (File.Exists("test.json"))
            {
                string serializedConfiguration = File.ReadAllText("test.json");
                
                var sameConfiguration = JsonConvert.DeserializeObject<SpreadConfiguration>(serializedConfiguration);
                dict[sameConfiguration] += 1;

                var differentConfiguration = JsonConvert.DeserializeObject<SpreadConfiguration>(serializedConfiguration);
                differentConfiguration.MinimumProfitPercentage = 2;
                dict[differentConfiguration] = 1;

                Console.WriteLine($"Dict count: Should be 2, it's: {dict.Count}");
                Console.WriteLine($"Value for existing configuration: Should be 2, it's: {dict[spreadConfiguration]}");
                Console.WriteLine($"Value for different configuration: Should be 1, it's: {dict[differentConfiguration]}");
            } else
            {
                File.WriteAllText("test.json", JsonConvert.SerializeObject(spreadConfiguration));
            }
            Console.ReadLine();
        }
    }
}
