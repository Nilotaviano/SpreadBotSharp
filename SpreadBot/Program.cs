using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex;
using SpreadBot.Infrastructure.Exchanges.Huobi;
using SpreadBot.Logic;
using System;
using System.IO;
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

            var watcher = new FileSystemWatcher(Path.GetDirectoryName(appSettingsPath));
            watcher.NotifyFilter = NotifyFilters.LastWrite;
            watcher.Filter = Path.GetFileName(appSettingsPath);
            watcher.EnableRaisingEvents = true;

            void reloadAppSettings(object sender, FileSystemEventArgs e)
            {
                try
                {
                    var updatedAppSettings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(appSettingsPath));

                    appSettings.Reload(updatedAppSettings);
                    Logger.Instance.LogMessage("App Settings reloaded");
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogUnexpectedError($"Error reloading app settings: {ex}");
                }
            }

            Task.Run(() => watcher.Changed += reloadAppSettings);

            NetProfitRecorder.Instance.AppSettings = appSettings;

            //var bittrex = new BittrexClient(appSettings.ApiKey, appSettings.ApiSecret);
            //await bittrex.Setup();

            var huobi = new HuobiClientWrapper(appSettings.ApiKey, appSettings.ApiSecret, appSettings.AccountId);
            await huobi.Setup();

            var dataRepository = new DataRepository(huobi, appSettings);
            dataRepository.StartConsumingData();

            var coordinatorContext = new FileCoordinatorContext();

            var coordinator = new Coordinator(appSettings, dataRepository, coordinatorContext);
            coordinator.Start();

            Console.ReadLine();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Instance.LogUnexpectedError($"Global unhandled exception. Terminating: {e.IsTerminating}. Exception: {e.ExceptionObject}");
        }
    }
}
