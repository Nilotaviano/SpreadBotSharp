using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpreadBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var bittrex = new Bittrex(Environment.GetEnvironmentVariable("apikey"), Environment.GetEnvironmentVariable("apisecret"));
            await bittrex.Setup();

            var dataRepository = new DataRepository(bittrex);
            dataRepository.SubscribeToCurrencyBalance("ETH", new Guid(), (balanceData) => Console.WriteLine(JsonSerializer.Serialize(balanceData)));
            dataRepository.SubscribeToMarketData("ETH-BTC", new Guid(), (marketData) => Console.WriteLine(JsonSerializer.Serialize(marketData)));
            dataRepository.StartConsumingData();

            Console.ReadLine();
        }
    }
}
