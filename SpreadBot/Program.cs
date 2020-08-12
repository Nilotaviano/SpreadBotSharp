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
            await bittrex.ConnectWebsocket();

            bittrex.OnBalance((x) => Console.WriteLine(JsonSerializer.Serialize(x)));
            bittrex.OnSummaries((x) => Console.WriteLine(JsonSerializer.Serialize(x)));
            bittrex.OnTickers((x) => Console.WriteLine(JsonSerializer.Serialize(x)));

            Console.ReadLine();
        }
    }
}
