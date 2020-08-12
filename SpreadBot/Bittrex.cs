using Microsoft.AspNet.SignalR.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using SpreadBot.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot
{
    public class Bittrex : IExchange
    {
        private const string apiUrl = "https://api.bittrex.com/v3";
        private const string websocketUrl = "https://socket-v3.bittrex.com/signalr";

        private string ApiKey { get; set; }
        private string ApiSecret { get; set; }

        public SocketClient SocketClient { get; set; }
        public Bittrex(string apiKey, string apiSecret)
        {
            ApiKey = apiKey;
            ApiSecret = apiSecret;
            SocketClient = new SocketClient(websocketUrl);
        }

        public async Task ConnectWebsocket()
        {
            if (!await SocketClient.Connect())
                throw new Exception("Error connecting to websocket");

            var authResponse = await SocketClient.Authenticate(ApiKey, ApiSecret);

            if (!authResponse.success)
                throw new Exception($"Error authenticating to websocket. Code: {authResponse.errorCode}");

            SocketClient.SetAuthExpiringHandler(ApiKey, ApiSecret);
        }

        public async void OnBalance(Action<BalanceData> callback)
        {
            var subscribeResponse = await SocketClient.Subscribe(new[] { "balance" });

            if (!subscribeResponse[0].success)
                throw new Exception($"Error subscribing to balance. Code: {subscribeResponse[0].errorCode}");

            SocketClient.On("balance", callback);
        }

        public async void OnSummaries(Action<MarketSummariesData> callback)
        {
            var subscribeResponse = await SocketClient.Subscribe(new[] { "market_summaries" });

            if (!subscribeResponse[0].success)
                throw new Exception($"Error subscribing to market_summaries. Code: {subscribeResponse[0].errorCode}");

            SocketClient.On("marketsummaries", callback);
        }

        public async void OnTickers(Action<TickersData> callback)
        {
            var subscribeResponse = await SocketClient.Subscribe(new[] { "tickers" });

            if (!subscribeResponse[0].success)
                throw new Exception($"Error subscribing to tickers. Code: {subscribeResponse[0].errorCode}");

            SocketClient.On("tickers", callback);
        }
    }
}
