using Newtonsoft.Json;
using RestSharp;
using SpreadBot.Models;
using SpreadBot.Models.API;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using static SpreadBot.Models.API.ApiBalanceData;

namespace SpreadBot.Infrastructure.Exchanges
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

        public bool IsSetup { get; private set; }

        public decimal FeeRate => 0.002m;

        public Bittrex(string apiKey, string apiSecret)
        {
            ApiKey = apiKey;
            ApiSecret = apiSecret;
            SocketClient = new SocketClient(websocketUrl);
            ApiClient = new RestClient(apiUrl);
            HeartbeatStopwatch = new Stopwatch();
        }

        public async Task Setup()
        {
            await ConnectWebsocket(SocketClient);

            IsSetup = true;
        }

        public void OnBalance(Action<ApiBalanceData> callback)
        {
            SocketClient.On("balance", callback);
        }

        public void OnSummaries(Action<ApiMarketSummariesData> callback)
        {
            SocketClient.On("marketsummaries", callback);
        }

        public void OnTickers(Action<ApiTickersData> callback)
        {
            SocketClient.On("tickers", callback);
        }

        public void OnOrder(Action<ApiOrderData> callback)
        {
            SocketClient.On("order", callback);
        }

        public async Task<IEnumerable<BalanceData>> GetBalanceData()
        {
            var request = new RestRequest("/balances", Method.GET, DataFormat.Json);

            var balances = await ExecuteAuthenticatedRequest<Balance[]>(request);

            return balances.Select(balance => new BalanceData(balance));
        }

        public async Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit)
        {
            var request = new RestRequest("/orders", Method.POST, DataFormat.Json);
            var body = JsonConvert.SerializeObject(new
            {
                marketSymbol,
                quantity,
                limit,
                direction = OrderDirection.BUY.ToString(),
                type = OrderType.LIMIT.ToString(),
                timeInForce = OrderTimeInForce.POST_ONLY_GOOD_TIL_CANCELLED.ToString() //TODO Nilo: Check if this breaks anything
            });
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var apiOrderData = await ExecuteAuthenticatedRequest<ApiOrderData>(request);

            return new OrderData(apiOrderData);
        }

        public async Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit)
        {
            var request = new RestRequest("/orders", Method.POST, DataFormat.Json);
            var body = JsonConvert.SerializeObject(new
            {
                marketSymbol,
                quantity,
                limit,
                direction = OrderDirection.SELL.ToString(),
                type = OrderType.LIMIT.ToString(),
                timeInForce = OrderTimeInForce.POST_ONLY_GOOD_TIL_CANCELLED.ToString() //TODO Nilo: Check if this breaks anything
            });
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var apiOrderData = await ExecuteAuthenticatedRequest<ApiOrderData>(request);

            return new OrderData(apiOrderData);
        }

        public async Task<OrderData> CancelOrder(string orderId)
        {
            var request = new RestRequest($"/orders/{orderId}", Method.DELETE, DataFormat.Json);

            var apiOrderData = await ExecuteAuthenticatedRequest<ApiOrderData>(request);

            return new OrderData(apiOrderData);
        }

        private async Task ConnectWebsocket(SocketClient socketClient)
        {
            if (!await socketClient.Connect())
                throw new Exception("Error connecting to websocket");

            var authResponse = await socketClient.Authenticate(ApiKey, ApiSecret);

            if (!authResponse.Success)
                throw new Exception($"Error authenticating to websocket. Code: {authResponse.ErrorCode}");

            socketClient.SetAuthExpiringHandler(ApiKey, ApiSecret);

            var subscribeResponse = await socketClient.Subscribe(new[] { "balance", "market_summaries", "tickers", "order", "heartbeat" });

            if (subscribeResponse.Any(r => !r.Success))
                throw new Exception(message: $"Error subscribing to data streams. Code: {JsonConvert.SerializeObject(subscribeResponse)}");

            HeartbeatStopwatch.Start();
            socketClient.On("heartbeat", HeartbeatStopwatch.Restart);
        }

        private async Task<T> ExecuteRequest<T>(RestRequest request)
        {
            var response = await ApiClient.ExecuteAsync(request);

            if (response.StatusCode == HttpStatusCode.Created)
                return JsonConvert.DeserializeObject<T>(response.Content);
            else
            {
                var errorData = JsonConvert.DeserializeObject<ApiErrorData>(response.Content);
                throw new ExchangeRequestException(errorData);
            }
        }

        private async Task<T> ExecuteAuthenticatedRequest<T>(RestRequest request)
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string method = request.Method.ToString();
            string requestUri = ApiClient.BuildUri(request).ToString();
            string contentHash = (request.Parameters.SingleOrDefault(p => p.Type == ParameterType.RequestBody)?.Value as string ?? string.Empty).Hash();

            request.AddHeader("Api-Key", ApiKey);
            request.AddHeader("Api-Timestamp", timestamp);
            request.AddHeader("Api-Content-Hash", contentHash);
            request.AddHeader("Api-Signature", $"{timestamp}{requestUri}{method}{contentHash}".Sign(ApiSecret));

            var response = await ApiClient.ExecuteAsync(request);

            if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created)
                return JsonConvert.DeserializeObject<T>(response.Content);
            else
            {
                var errorData = JsonConvert.DeserializeObject<ApiErrorData>(response.Content);
                throw new ExchangeRequestException(errorData);
            }
        }
    }
}
