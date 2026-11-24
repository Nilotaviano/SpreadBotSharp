using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Ccxt;
using SpreadBot.Logic;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
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

            //Held until exit: a FileSystemWatcher references itself only weakly, so an unreferenced one is collected and stops raising events
            using var watcher = new FileSystemWatcher(Path.GetDirectoryName(appSettingsPath));
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Filter = Path.GetFileName(appSettingsPath);
            watcher.EnableRaisingEvents = true;

            //The exchange watches the markets of these base markets from Setup on; a base market added later needs a restart
            var watchedBaseMarkets = appSettings.SpreadConfigurations.Select(c => c.BaseMarket).Distinct().ToList();

            void reloadAppSettings(object sender, FileSystemEventArgs e)
            {
                try
                {
                    var updatedAppSettings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(appSettingsPath));

                    appSettings.Reload(updatedAppSettings);
                    Logger.Instance.LogMessage("App Settings reloaded");

                    var unwatched = (updatedAppSettings.SpreadConfigurations ?? Enumerable.Empty<SpreadConfiguration>())
                        .Select(c => c.BaseMarket).Distinct().Except(watchedBaseMarkets).ToList();
                    if (unwatched.Count > 0)
                        Logger.Instance.LogError($"Base markets {string.Join(", ", unwatched)} weren't configured at startup, so their markets aren't watched; restart to trade them");
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogUnexpectedError($"Error reloading app settings: {ex}");
                }
            }

            watcher.Changed += reloadAppSettings;

            NetProfitRecorder.Instance.AppSettings = appSettings;

            var exchange = new CcxtExchange(appSettings.ExchangeId, appSettings.ApiKey, appSettings.ApiSecret, appSettings.UseSandbox);
            await exchange.Setup(watchedBaseMarkets);

            var dataRepository = new DataRepository(exchange, appSettings);
            dataRepository.StartConsumingData();

            var coordinatorContext = new FileCoordinatorContext();

            var coordinator = new Coordinator(appSettings, dataRepository, coordinatorContext);
            coordinator.Start();

            Console.ReadLine();

            coordinator.Dispose();
            exchange.Dispose();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Instance.LogUnexpectedError($"Global unhandled exception. Terminating: {e.IsTerminating}. Exception: {e.ExceptionObject}");
        }
    }
}
