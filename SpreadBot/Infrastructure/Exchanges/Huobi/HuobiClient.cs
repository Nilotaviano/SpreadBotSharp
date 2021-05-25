using CryptoExchange.Net.Authentication;
using Huobi.Net;
using Huobi.Net.Objects;
using Newtonsoft.Json;
using RestSharp;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges.Huobi
{
    public class HuobiClientWrapper : IExchange
    {
        private HuobiClient ApiClient { get; set; }
        private HuobiSocketClient SocketClient { get; set; }
        private long AccountId { get; set; }

        public bool IsSetup { get; private set; }

        public decimal FeeRate => 0.002m;

        private readonly ConcurrentBag<Action<BalanceData>> onBalanceCallBacks = new ConcurrentBag<Action<BalanceData>>();
        private readonly ConcurrentBag<Action<MarketSummaryData>> onSummariesCallBacks = new ConcurrentBag<Action<MarketSummaryData>>();
        private readonly ConcurrentBag<Action<TickerData>> onTickersCallBacks = new ConcurrentBag<Action<TickerData>>();
        private readonly ConcurrentBag<Action<OrderData>> onOrderCallBacks = new ConcurrentBag<Action<OrderData>>();

        public HuobiClientWrapper(string apiKey, string apiSecret, string accountId)
        {
            ApiClient = new HuobiClient(new HuobiClientOptions() { ApiCredentials = new ApiCredentials(apiKey, apiSecret) });
            SocketClient = new HuobiSocketClient(new HuobiSocketClientOptions() { ApiCredentials = new ApiCredentials(apiKey, apiSecret) });
            AccountId = long.Parse(accountId);
        }

        public async Task Setup()
        {
            var websocketConnected = await ConnectWebsocket();

            if (!websocketConnected)
                throw new Exception("Websocket failed to connect");

            IsSetup = true;
        }

        public void OnBalance(Action<BalanceData> callback) => onBalanceCallBacks.Add(callback);

        public void OnSummaries(Action<MarketSummaryData> callback) => onSummariesCallBacks.Add(callback);

        public void OnTickers(Action<TickerData> callback) => onTickersCallBacks.Add(callback);

        public void OnOrder(Action<OrderData> callback) => onOrderCallBacks.Add(callback);

        public async Task<CompleteBalanceData> GetBalanceData()
        {
            var response = await ApiClient.GetBalancesAsync(AccountId);

            if (response.Success)
            {
                var balances = response.Data.Where(x => x.Type == HuobiBalanceType.Trade).Select(HuobiTypeConverter.ConvertBalance);

                return new CompleteBalanceData(DateTime.UtcNow.Ticks, balances);
            }
            else
                throw new NotImplementedException();
        }

        //TODO: merge this and GetMarketSummariesData into a single method if possible
        public async Task<TickerData> GetTickersData()
        {
            var response = await ApiClient.GetTickersAsync();

            if (response.Success)
            {
                return new TickerData
                {
                    Sequence = response.Data.Timestamp.Ticks,
                    Markets = response.Data.Ticks.Select(x => new Market()
                    {
                        AskRate = x.Ask,
                        BidRate = x.Bid,
                        LastTradeRate = x.Bid, //TODO
                        Symbol = x.Symbol
                    }).ToArray()
                };
            }
            else
                throw new NotImplementedException();
        }

        //TODO: merge this and GetTickersData into a single method if possible
        public async Task<MarketSummaryData> GetMarketSummariesData()
        {
            var response = await ApiClient.GetTickersAsync();

            if (response.Success)
            {
                return new MarketSummaryData
                {
                    Sequence = response.Data.Timestamp.Ticks,
                    Markets = response.Data.Ticks.Where(x => x.Open > 0).Select(x => new Market()
                    {
                        Symbol = x.Symbol,
                        High = x.High.GetValueOrDefault(),
                        Low = x.Low.GetValueOrDefault(),
                        QuoteVolume = x.Volume.GetValueOrDefault(),
                        Volume = 0, //TODO
                        UpdatedAt = response.Data.Timestamp,
                        PercentChange = x.Open.HasValue ? x.Bid / x.Open.Value - 1 : 0
                    }).ToArray()
                };
            }
            else
                throw new NotImplementedException();
        }

        public async Task<Market[]> GetMarketsData()
        {
            var response = await ApiClient.GetSymbolsAsync();

            if (response.Success)
            {
                return response.Data.Select(x => new Market()
                {
                    Symbol = x.Symbol,
                    Quote = x.QuoteCurrency,
                    Target = x.BaseCurrency,
                    CreatedAt = DateTime.MinValue, //TODO
                    MinTradeSize = x.MinOrderValue,
                    Notice = string.Empty, //TODO we might have to use this
                    Status = x.State == HuobiSymbolState.Online ? EMarketStatus.Online : EMarketStatus.Offline, //ONLINE is the only one that we care about,
                    LimitPrecision = x.PricePrecision,
                    AmountPrecision = x.AmountPrecision,
                    IsTokenizedSecurity = null //TODO we might have to use this
                }).ToArray();
            }
            else
                throw new NotImplementedException();
        }

        private DateTime? nextStartTime = null;
        public async Task<Order[]> GetClosedOrdersData(string startAfterOrderId = null)
        {
            var successful = false;

            var startTime = nextStartTime;
            nextStartTime = DateTime.UtcNow;

            try
            {
                var response = await ApiClient.GetHistoryOrdersAsync(startTime: startTime);

                if (response.Success)
                {
                    var result = response.Data.Orders.Select(HuobiTypeConverter.ConvertOrder).ToArray();
                    successful = true;
                    return result;
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            finally
            {
                if (!successful)
                    nextStartTime = startTime;
            }
        }

        public async Task<Order[]> GetOpenOrdersData()
        {
            var response = await ApiClient.GetOpenOrdersAsync();

            if (response.Success)
            {
                return response.Data.Select(HuobiTypeConverter.ConvertOrder).ToArray();
            }
            else
                throw new NotImplementedException();
        }

        public async Task<Order> GetOrderData(string orderId)
        {
            var response = await ApiClient.GetOrderInfoAsync(long.Parse(orderId));

            if (response.Success)
            {
                return HuobiTypeConverter.ConvertOrder(response.Data);
            }
            else
                throw new NotImplementedException();
        }

        public async Task<Order> BuyLimit(string marketSymbol, decimal quantity, decimal limit, string clientOrderId = null)
        {
            return await ExecuteLimitOrder(OrderDirection.BUY, marketSymbol, quantity, limit, clientOrderId: clientOrderId);
        }

        public async Task<Order> SellLimit(string marketSymbol, decimal quantity, decimal limit, string clientOrderId = null)
        {
            return await ExecuteLimitOrder(OrderDirection.SELL, marketSymbol, quantity, limit, clientOrderId: clientOrderId);
        }

        public async Task<Order> BuyMarket(string marketSymbol, decimal quantity)
        {
            return await ExecuteMarketOrder(OrderDirection.BUY, marketSymbol, quantity);
        }

        public async Task<Order> SellMarket(string marketSymbol, decimal quantity)
        {
            return await ExecuteMarketOrder(OrderDirection.SELL, marketSymbol, quantity);
        }

        public async Task<Order> CancelOrder(string orderId)
        {
            var response = await ApiClient.CancelOrderAsync(long.Parse(orderId));

            //TODO: need to test error cases
            if (response.Success || response.Error.Message == "order-orderstate-error")
            {
                return await GetOrderData(response.Data.ToString());
            }
            else
                throw new NotImplementedException();
        }

        private async Task<bool> ConnectWebsocket()
        {
            bool success = true;

            try
            {
                await SocketClient.SubscribeToAccountUpdatesAsync(huobiBalance =>
                {
                    var balance = new BalanceData()
                    {
                        Sequence = huobiBalance.ChangeTime.GetValueOrDefault().Ticks,
                        Balance = HuobiTypeConverter.ConvertBalance(huobiBalance)
                    };

                    foreach (var callback in onBalanceCallBacks)
                    {
                        callback(balance);
                    }
                });

                //await SocketClient.SubscribeToOrderUpdatesAsync(
                //    onOrderSubmitted: orderUpdate =>
                //    {
                //        var order = new OrderData()
                //        {
                //            Sequence = DateTime.UtcNow.Ticks,
                //            AccountId = orderUpdate.AccountId.ToString(),
                //            Delta = new OrderData.Order()
                //            {
                //                ClientOrderId = orderUpdate.ClientOrderId,
                //                Commission = orderUpdate.fee
                //            }
                //        };
                //        foreach (var callback in onOrderCallBacks)
                //        {
                //            callback(order);
                //        }
                //    },
                //    onOrderCancellation: orderCancelled =>
                //    {
                //        var order = new OrderData()
                //        {

                //        };
                //        foreach (var callback in onOrderCallBacks)
                //        {
                //            callback(order);
                //        }
                //    },
                //    onOrderMatched: matchedOrder =>
                //    {
                //        var order = new OrderData()
                //        {

                //        };
                //        foreach (var callback in onOrderCallBacks)
                //        {
                //            callback(order);
                //        }
                //    });

                var ordersTimer = new System.Threading.Timer(async e =>
                {
                    var closedOrders = await GetClosedOrdersData();

                    foreach (var orderData in closedOrders.Select(x => new OrderData() { Order = x }))
                        foreach (var callback in onOrderCallBacks)
                        {
                            callback(orderData);
                        }
                }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));

                var tickersTimer = new System.Threading.Timer(async e =>
                {
                    var tickersData = await GetTickersData();

                    foreach (var callback in onTickersCallBacks)
                    {
                        callback(tickersData);
                    }
                }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            }
            catch (Exception e)
            {
                success = false;

                Logger.Instance.LogUnexpectedError($"Error connecting to websocket: {e}");
            }

            return success;
        }

        public async Task<Order> ExecuteLimitOrder(OrderDirection direction, string marketSymbol, decimal quantity, decimal limit, bool useCredits = true, string clientOrderId = null)
        {
            var orderType = direction switch
            {
                OrderDirection.BUY => HuobiOrderType.LimitMakerBuy,
                OrderDirection.SELL => HuobiOrderType.LimitMakerSell,
                _ => throw new ArgumentException()
            };

            var response = await ApiClient.PlaceOrderAsync(AccountId, marketSymbol, orderType, quantity, limit, clientOrderId);

            if (response.Success)
            {
                return await GetOrderData(response.Data.ToString());
            }
            else
                throw new NotImplementedException();
        }

        private async Task<Order> ExecuteMarketOrder(OrderDirection direction, string marketSymbol, decimal quantity, bool useCredits = true)
        {
            var orderType = direction switch
            {
                OrderDirection.BUY => HuobiOrderType.MarketBuy,
                OrderDirection.SELL => HuobiOrderType.MarketSell,
                _ => throw new ArgumentException()
            };

            var response = await ApiClient.PlaceOrderAsync(AccountId, marketSymbol, orderType, quantity);

            if (response.Success)
            {
                return await GetOrderData(response.Data.ToString());
            }
            else
                throw new NotImplementedException();
        }


        public ApiErrorType GetErrorType(IRestResponse restResponse)
        {
            //TODO
            throw new NotImplementedException();
            //try
            //{
            //    if (string.IsNullOrWhiteSpace(restResponse.Content))
            //    {
            //        Logger.Instance.LogUnexpectedError($"Unknown API error data: {restResponse.ErrorMessage}. {JsonConvert.SerializeObject(restResponse.ErrorException)}");
            //        return ApiErrorType.UnknownError;
            //    }

                //var errorData = JsonConvert.DeserializeObject<BittrexApiErrorData>(restResponse.Content);

                //return errorData.Code.ToUpperInvariant() switch
                //{
                //    "INSUFFICIENT_FUNDS" => ApiErrorType.InsufficientFunds,
                //    "MIN_TRADE_REQUIREMENT_NOT_MET " => ApiErrorType.DustTrade,
                //    "DUST_TRADE_DISALLOWED" => ApiErrorType.DustTrade,
                //    "DUST_TRADE_DISALLOWED_MIN_VALUE" => ApiErrorType.DustTrade,
                //    "INSUFFICIENT_AWARDS" => ApiErrorType.RetryLater,
                //    "MARKET_OFFLINE" => ApiErrorType.MarketOffline,
                //    "POST_ONLY" => ApiErrorType.RetryLater,
                //    "MAX_ORDERS_ALLOWED" => ApiErrorType.RetryLater,
                //    "ORDER_NOT_OPEN" => ApiErrorType.OrderNotOpen,
                //    "THROTTLED" => ApiErrorType.Throttled,
                //    "CANNOT_ESTIMATE_COMMISSION" => ApiErrorType.CannotEstimateCommission,
                //    "RATE_PRECISION_NOT_ALLOWED" => ApiErrorType.PrecisionNotAllowed,
                //    "MIN_TRADE_REQUIREMENT_NOT_MET" => ApiErrorType.DustTrade,
                //    "CLIENTORDERID_ALREADY_EXISTS" => ApiErrorType.ClientOrderIdAlreadyExists,
                //    _ when restResponse.StatusCode == HttpStatusCode.TooManyRequests => ApiErrorType.Throttled,
                //    _ when restResponse.StatusCode == HttpStatusCode.NotFound => ApiErrorType.MarketOffline,
                //    _ when restResponse.StatusCode == HttpStatusCode.ServiceUnavailable => ApiErrorType.MarketOffline,
                //    _ when restResponse.StatusCode == HttpStatusCode.Unauthorized => ApiErrorType.Unauthorized,
                //    _ when restResponse.StatusCode == HttpStatusCode.Forbidden => ApiErrorType.Unauthorized,
                //    _ => ApiErrorType.UnknownError
                //};
            //}
            //catch (Exception e)
            //{
            //    StringBuilder sb = new StringBuilder();
            //    sb.AppendLine("Error when serializing restResponse. Using ToString instead");
            //    sb.AppendLine("RestResponse:");
            //    sb.AppendLine(restResponse.Content);
            //    sb.AppendLine("RestException:");
            //    sb.AppendLine(restResponse.ErrorException.ToString());
            //    sb.AppendLine("SerializationError:");
            //    sb.AppendLine(JsonConvert.SerializeObject(e));

            //    Logger.Instance.LogUnexpectedError(sb.ToString());

            //    return ApiErrorType.UnknownError;
            //}
        }
    }
}