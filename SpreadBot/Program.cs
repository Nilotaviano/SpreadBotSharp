using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using SpreadBot.Dashboard;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Ccxt;
using SpreadBot.Infrastructure.Persistence;
using SpreadBot.Logic;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot
{
    class Program
    {
        static async Task<int> Main(string[] args)
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
            var appSettings = AppSettings.FromConfiguration(new ConfigurationBuilder()
                .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: true)
                .Build());

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

                    //Reload refuses configurations without a base market, so each one here has one
                    var unwatched = updatedAppSettings.SpreadConfigurations.Select(c => c.BaseMarket).Distinct().Except(watchedBaseMarkets).ToList();
                    if (unwatched.Count > 0)
                        Logger.Instance.LogError($"Base markets {string.Join(", ", unwatched)} weren't configured at startup, so their markets aren't watched; restart to trade them");
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogUnexpectedError($"Error reloading app settings: {ex}");
                }
            }

            watcher.Changed += reloadAppSettings;

            var exchange = new CcxtExchange(appSettings.ExchangeId, appSettings.ApiKey, appSettings.ApiSecret, appSettings.UseSandbox);
            await exchange.Setup(watchedBaseMarkets);

            var dataRepository = new DataRepository(exchange, appSettings);
            dataRepository.StartConsumingData();

            var databasePath = Path.Combine(Path.GetDirectoryName(appSettingsPath), appSettings.DatabasePath);
            using var coordinatorContext = SqliteCoordinatorContext.Open(databasePath);

            var coordinator = new Coordinator(appSettings, dataRepository, coordinatorContext);
            coordinator.Start();

            var dashboard = await StartDashboard(appSettings, Path.GetDirectoryName(appSettingsPath), dataRepository, coordinator, coordinatorContext);

            //Enter stops the bot; so does a halt, once the bots have cancelled their orders
            var stopRequested = Task.Run(Console.ReadLine);
            var halted = await Task.WhenAny(stopRequested, coordinator.Halted) == coordinator.Halted;

            //First, so its controls can't act on a coordinator and exchange being taken down
            if (dashboard != null)
            {
                if (halted)
                    await dashboard.AnnounceHalt(coordinator.HaltReason);
                await dashboard.DisposeAsync();
            }

            coordinator.Dispose();
            exchange.Dispose();
            //A bot still halting holds its semaphore, which the save waits for; the last periodic save stands then
            if (!Task.Run(coordinatorContext.Save).Wait(TimeSpan.FromSeconds(30)))
                Logger.Instance.LogUnexpectedError("Timed out saving the session on exit");
            Logger.Instance.LogMessage(halted ? "Exiting after a halt" : "Exiting");
            Logger.Instance.Flush(TimeSpan.FromSeconds(5));

            //Nonzero, so whatever supervises the process sees that it stopped on an error
            return halted ? 1 : 0;
        }

        //Off unless enabled; the bot runs on without it if it can't start
        private static async Task<DashboardHost> StartDashboard(AppSettings appSettings, string settingsDirectory, DataRepository dataRepository, Coordinator coordinator, SqliteCoordinatorContext coordinatorContext)
        {
            if (appSettings.Dashboard?.Enabled != true)
                return null;

            try
            {
                var secrets = DashboardSecrets.LoadOrCreate(settingsDirectory);
                var dashboard = DashboardHost.Create(appSettings.Dashboard, secrets, appSettings, dataRepository, coordinator, coordinatorContext, coordinatorContext.Database);
                await dashboard.StartAsync();
                Logger.Instance.LogMessage($"Dashboard on {appSettings.Dashboard.Url}; its access token is in {Path.Combine(settingsDirectory, DashboardSecrets.FileName)}");
                return dashboard;
            }
            catch (Exception e)
            {
                Logger.Instance.LogUnexpectedError($"The dashboard didn't start; the bot runs without it: {e}");
                return null;
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Logger.Instance.LogUnexpectedError($"Global unhandled exception. Terminating: {e.IsTerminating}. Exception: {e.ExceptionObject}");
        }
    }
}
