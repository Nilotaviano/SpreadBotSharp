using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex;
using SpreadBot.Logic;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SpreadBot
{
    class Program
    {
        static async Task Main()
        {
            var appSettings = GetAppSettings();

            BalanceReporter.Initialize();
            NetProfitRecorder.Initialize();

            var bittrex = new BittrexClient(appSettings.ApiKey, appSettings.ApiSecret);
            await bittrex.Setup();

            var dataRepository = new DataRepository(bittrex, appSettings);
            dataRepository.StartConsumingData();

            var coordinator = new Coordinator(appSettings, dataRepository);
            coordinator.Start();

            Console.ReadLine();
        }

        private static AppSettings GetAppSettings()
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json".ToLocalFilePath(), optional: false, reloadOnChange: true)
                .Build();

            var appSettings = configuration.Get<AppSettings>();

            //TODO: This is NOT working on linux for some reason
            ChangeToken.OnChange(() => configuration.GetReloadToken(), () =>
            {
                appSettings.Reload(configuration.Get<AppSettings>());
                Logger.Instance.LogMessage("App Settings reloaded");
            });


            return appSettings;
        }
    }
}
