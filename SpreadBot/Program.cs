using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex;
using SpreadBot.Logic;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpreadBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var bittrex = new BittrexClient(Environment.GetEnvironmentVariable("apikey"), Environment.GetEnvironmentVariable("apisecret"));
            await bittrex.Setup();

            var dataRepository = new DataRepository(bittrex);
            dataRepository.StartConsumingData();

            var appSettings = new AppSettings()
            {
                ApiKey = Environment.GetEnvironmentVariable("apikey"), 
                ApiSecret = Environment.GetEnvironmentVariable("apisecret"),
                BaseMarket = "ETH",
                MaxNumberOfBots = 1,
                MinimumNegotiatedAmount = 50000.Satoshi(),
                MinimumPrice = 1000.Satoshi(),
                SpreadConfigurations = new []
                {
                    new SpreadConfiguration()
                    {
                        MaxPercentChangeFromPreviousDay = 40,
                        AllocatedAmountOfBaseCurrency = 0.1m,
                        MinimumQuoteVolume = 10,
                        MinimumSpreadPercentage = 1,
                        MinutesForLoss = 20,
                        MinimumProfitPercentage = 1,
                        SpreadThresholdBeforeCancelingCurrentOrder = 20.Satoshi()
                    }
                }
            };

            var coordinator = new Coordinator(appSettings, dataRepository);
            coordinator.Start();

            Console.ReadLine();
        }
    }
}
