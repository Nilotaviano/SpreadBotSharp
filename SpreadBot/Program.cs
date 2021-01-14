﻿using Microsoft.Extensions.Configuration;
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

            var coordinator = new Coordinator(appSettings, dataRepository);
            coordinator.Start();

            Console.ReadLine();
        }
    }
}
