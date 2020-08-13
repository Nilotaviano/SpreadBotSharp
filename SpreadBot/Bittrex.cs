using RestSharp;
using SpreadBot.Models;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpreadBot
{
    public class Bittrex : IExchange
    {
        private const string apiUrl = "https://api.bittrex.com/v3";
        private const string websocketUrl = "https://socket-v3.bittrex.com/signalr";

        private string ApiKey { get; set; }
        private string ApiSecret { get; set; }

        private SocketClient SocketClient { get; set; }
        private RestClient ApiClient { get; set; }

        private Stopwatch HeartbeatStopwatch { get; set; } //To check if the websocket is still working

        public Bittrex(string apiKey, string apiSecret)
        {
            ApiKey = apiKey;
            ApiSecret = apiSecret;
            SocketClient = new SocketClient(websocketUrl);
            ApiClient = new RestClient(apiUrl);
            HeartbeatStopwatch = new Stopwatch();
        }

        public async Task ConnectWebsocket()
        {
            if (!await SocketClient.Connect())
                throw new Exception("Error connecting to websocket");

            var authResponse = await SocketClient.Authenticate(ApiKey, ApiSecret);

            if (!authResponse.success)
                throw new Exception($"Error authenticating to websocket. Code: {authResponse.errorCode}");

            SocketClient.SetAuthExpiringHandler(ApiKey, ApiSecret);

            var subscribeResponse = await SocketClient.Subscribe(new[] { "balance", "market_summaries", "tickers", "order", "heartbeat" });

            if (!subscribeResponse.Any(r => !r.success))
                throw new Exception(message: $"Error subscribing to data streams. Code: {JsonSerializer.Serialize(subscribeResponse)}");

            HeartbeatStopwatch.Start();
            SocketClient.On("heartbeat", HeartbeatStopwatch.Restart);
        }

        public void OnBalance(Action<BalanceData> callback)
        {
            SocketClient.On("balance", callback);
        }

        public void OnSummaries(Action<MarketSummariesData> callback)
        {
            SocketClient.On("marketsummaries", callback);
        }

        public void OnTickers(Action<TickersData> callback)
        {
            SocketClient.On("tickers", callback);
        }

        public void OnOrder(Action<OrderData> callback)
        {
            SocketClient.On("order", callback);
        }

        private async Task<T> ExecuteRequest<T>(RestRequest request)
        {
            var response = await ApiClient.ExecuteAsync(request);

            if (response.StatusCode == HttpStatusCode.Created)
                return JsonSerializer.Deserialize<T>(response.Content);
            else
            {
                var errorData = JsonSerializer.Deserialize<ErrorData>(response.Content);
                throw new Exception(errorData.Detail);
            }
        }

        public async Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit)
        {
            var request = new RestRequest("/orders", Method.POST, DataFormat.Json);
            request.AddJsonBody(new
            {
                marketSymbol,
                quantity,
                limit,
                direction = "BUY",
                type = "LIMIT",
                timeInForce = "POST_ONLY_GOOD_TIL_CANCELLED" //TODO Nilo: Check if this breaks anything
            });

            return await ExecuteRequest<OrderData>(request);
        }

        public async Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit)
        {
            var request = new RestRequest("/orders", Method.POST, DataFormat.Json);
            request.AddJsonBody(new
            {
                marketSymbol,
                quantity,
                limit,
                direction = "SELL",
                type = "LIMIT",
                timeInForce = "POST_ONLY_GOOD_TIL_CANCELLED" //TODO Nilo: Check if this breaks anything
            });

            return await ExecuteRequest<OrderData>(request);
        }

        public async Task<OrderData> CancelOrder(string orderId)
        {
            var request = new RestRequest($"/orders/{orderId}", Method.DELETE, DataFormat.Json);

            return await ExecuteRequest<OrderData>(request);
        }
    }
}
