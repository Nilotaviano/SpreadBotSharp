using Newtonsoft.Json;
using RestSharp;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex
{
    public class BittrexClient : IExchange
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

        public BittrexClient(string apiKey, string apiSecret)
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

        public void OnBalance(Action<BittrexApiBalanceData> callback)
        {
            SocketClient.On("balance", callback);
        }

        public void OnSummaries(Action<BittrexApiMarketSummariesData> callback)
        {
            SocketClient.On("marketsummaries", callback);
        }

        public void OnTickers(Action<BittrexApiTickersData> callback)
        {
            SocketClient.On("tickers", callback);
        }

        public void OnOrder(Action<BittrexApiOrderData> callback)
        {
            SocketClient.On("order", callback);
        }

        public async Task<CompleteBalanceData> GetBalanceData()
        {
            var request = new RestRequest("/balances", Method.GET, DataFormat.Json);

            var balances = await ExecuteAuthenticatedRequest<BittrexApiBalanceData.Balance[]>(request);

            return new CompleteBalanceData(balances);
        }

        public async Task<BittrexApiTickersData> GetTickersData()
        {
            var request = new RestRequest("/markets/tickers", Method.GET, DataFormat.Json);

            var tickers = await ExecuteAuthenticatedRequest<BittrexApiTickersData.Ticker[]>(request);

            return new BittrexApiTickersData { Sequence = tickers.Sequence, Deltas = tickers.Data };
        }

        public async Task<BittrexApiMarketSummariesData> GetMarketSummariesData()
        {
            var request = new RestRequest("/markets/summaries", Method.GET, DataFormat.Json);

            var marketSummaries = await ExecuteAuthenticatedRequest<BittrexApiMarketSummariesData.MarketSummary[]>(request);

            return new BittrexApiMarketSummariesData { Sequence = marketSummaries.Sequence, Deltas = marketSummaries.Data };
        }

        public async Task<BittrexApiMarketData[]> GetMarketsData()
        {
            var request = new RestRequest("/markets", Method.GET, DataFormat.Json);

            var marketSummaries = await ExecuteAuthenticatedRequest<BittrexApiMarketData[]>(request);

            return marketSummaries.Data;
        }

        public async Task<ApiRestResponse<BittrexApiOrderData.Order[]>> GetClosedOrdersData(string startAfterOrderId)
        {
            var request = new RestRequest("/orders/closed", Method.GET, DataFormat.Json);

            request.AddQueryParameter("pageSize", "200");

            if (startAfterOrderId != null)
                request.AddQueryParameter("previousPageToken", startAfterOrderId);

            return await ExecuteAuthenticatedRequest<BittrexApiOrderData.Order[]>(request);
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
                timeInForce = OrderTimeInForce.POST_ONLY_GOOD_TIL_CANCELLED.ToString()
            });
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var apiOrderData = await ExecuteAuthenticatedRequest<BittrexApiOrderData.Order>(request);

            return new OrderData(apiOrderData.Data);
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
                timeInForce = OrderTimeInForce.POST_ONLY_GOOD_TIL_CANCELLED.ToString()
            });
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var apiOrderData = await ExecuteAuthenticatedRequest<BittrexApiOrderData.Order>(request);

            return new OrderData(apiOrderData.Data);
        }

        public async Task<OrderData> CancelOrder(string orderId)
        {
            var request = new RestRequest($"/orders/{orderId}", Method.DELETE, DataFormat.Json);

            var apiOrderData = await ExecuteAuthenticatedRequest<BittrexApiOrderData.Order>(request);

            return new OrderData(apiOrderData.Data);
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

        private async Task<ApiRestResponse<T>> ExecuteRequest<T>(RestRequest request)
        {
            var response = await ApiClient.ExecuteAsync(request);

            if (response.IsSuccessful)
            {
                T data = JsonConvert.DeserializeObject<T>(response.Content);
                int sequence = GetSequence(response);

                return new ApiRestResponse<T>
                {
                    Data = data,
                    Sequence = sequence
                };
            }
            else
            {
                ApiErrorType errorType = GetErrorType(response);

                if (errorType == ApiErrorType.UnknownError)
                    Logger.LogUnexpectedError($"Unexpected API error: {response.Content}");
                else
                    Logger.LogError($"API error: {response.Content}");

                throw new ApiException(errorType, response.Content);
            }
        }

        private async Task<ApiRestResponse<T>> ExecuteAuthenticatedRequest<T>(RestRequest request)
        {
            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            string method = request.Method.ToString();
            string requestUri = ApiClient.BuildUri(request).ToString();
            string contentHash = (request.Parameters.SingleOrDefault(p => p.Type == ParameterType.RequestBody)?.Value as string ?? string.Empty).Hash();

            request.AddHeader("Api-Key", ApiKey);
            request.AddHeader("Api-Timestamp", timestamp);
            request.AddHeader("Api-Content-Hash", contentHash);
            request.AddHeader("Api-Signature", $"{timestamp}{requestUri}{method}{contentHash}".Sign(ApiSecret));

            return await ExecuteRequest<T>(request);
        }

        private static int GetSequence(IRestResponse response)
        {
            string sequenceStr = response.Headers.SingleOrDefault(p => p.Name.Equals("Sequence"))?.Value as string;
            int sequence = !string.IsNullOrEmpty(sequenceStr) ? int.Parse(sequenceStr) : 0;
            return sequence;
        }

        /*
         * 400 - Bad Request	The request was malformed, often due to a missing or invalid parameter. See the error code and response data for more details.
         * 401 - Unauthorized	The request failed to authenticate (example: a valid api key was not included in your request header)
         * 403 - Forbidden	The provided api key is not authorized to perform the requested operation (example: attempting to trade with an api key not authorized to make trades)
         * 404 - Not Found	The requested resource does not exist.
         * 409 - Conflict	The request parameters were valid but the request failed due to an operational error. (example: INSUFFICIENT_FUNDS)
         * 429 - Too Many Requests	Too many requests hit the API too quickly. Please make sure to implement exponential backoff with your requests.
         * 501 - Not Implemented	The service requested has not yet been implemented.
         * 503 - Service Unavailable	The request parameters were valid but the request failed because the resource is temporarily unavailable (example: CURRENCY_OFFLINE)
         */
        public static ApiErrorType GetErrorType(IRestResponse restResponse)
        {
            try
            {
                var errorData = JsonConvert.DeserializeObject<BittrexApiErrorData>(restResponse.Content);

                return errorData.Code.ToUpperInvariant() switch
                {
                    "INSUFFICIENT_FUNDS" => ApiErrorType.InsufficientFunds,
                    "MIN_TRADE_REQUIREMENT_NOT_MET " => ApiErrorType.DustTrade,
                    "DUST_TRADE_DISALLOWED" => ApiErrorType.DustTrade,
                    "DUST_TRADE_DISALLOWED_MIN_VALUE" => ApiErrorType.DustTrade,
                    _ when restResponse.StatusCode == HttpStatusCode.TooManyRequests => ApiErrorType.Throttled,
                    _ when restResponse.StatusCode == HttpStatusCode.NotFound => ApiErrorType.MarketOffline, //TODO: Confirm
                    _ when restResponse.StatusCode == HttpStatusCode.ServiceUnavailable => ApiErrorType.MarketOffline, //TODO: Confirm
                    _ when restResponse.StatusCode == HttpStatusCode.Unauthorized => ApiErrorType.Unauthorized,
                    _ when restResponse.StatusCode == HttpStatusCode.Forbidden => ApiErrorType.Unauthorized,
                    _ => ApiErrorType.UnknownError
                };
            }
            catch (Exception e)
            {
                Logger.LogUnexpectedError($"Error parsing API error data: {JsonConvert.SerializeObject(e)}");

                return ApiErrorType.UnknownError;
            }
        }
    }
}