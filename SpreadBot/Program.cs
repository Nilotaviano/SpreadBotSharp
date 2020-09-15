using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models;
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

            Console.ReadLine();
        }
    }
}
