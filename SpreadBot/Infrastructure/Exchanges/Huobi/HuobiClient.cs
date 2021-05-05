using CryptoExchange.Net.Authentication;
using Huobi.Net;
using Huobi.Net.Objects;
using Newtonsoft.Json;
using RestSharp;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Timers;

namespace SpreadBot.Infrastructure.Exchanges.Huobi
{
    public class HuobiClientWrapper : IExchange
    {
        private HuobiClient ApiClient { get; set; }
        private HuobiSocketClient SocketClient { get; set; }
        private long AccountId { get; set; }

        public bool IsSetup { get; private set; }

        public decimal FeeRate => 0.002m;

        private readonly ConcurrentBag<Action<BittrexApiBalanceData>> onBalanceCallBacks = new ConcurrentBag<Action<BittrexApiBalanceData>>();
        private readonly ConcurrentBag<Action<BittrexApiMarketSummariesData>> onSummariesCallBacks = new ConcurrentBag<Action<BittrexApiMarketSummariesData>>();
        private readonly ConcurrentBag<Action<BittrexApiTickersData>> onTickersCallBacks = new ConcurrentBag<Action<BittrexApiTickersData>>();
        private readonly ConcurrentBag<Action<BittrexApiOrderData>> onOrderCallBacks = new ConcurrentBag<Action<BittrexApiOrderData>>();

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

        public void OnBalance(Action<BittrexApiBalanceData> callback) => onBalanceCallBacks.Add(callback);

        public void OnSummaries(Action<BittrexApiMarketSummariesData> callback) => onSummariesCallBacks.Add(callback);

        public void OnTickers(Action<BittrexApiTickersData> callback) => onTickersCallBacks.Add(callback);

        public void OnOrder(Action<BittrexApiOrderData> callback) => onOrderCallBacks.Add(callback);

        public async Task<CompleteBalanceData> GetBalanceData()
        {
            var response = await ApiClient.GetBalancesAsync(AccountId);

            if (response.Success)
            {
                var balances = response.Data.Where(x => x.Type == HuobiBalanceType.Trade).Select(x => new BalanceData(x));

                return new CompleteBalanceData(DateTime.UtcNow.Ticks, balances);
            }
            else
                throw new NotImplementedException();
        }

        //TODO: merge this and GetMarketSummariesData into a single method if possible
        public async Task<BittrexApiTickersData> GetTickersData()
        {
            var response = await ApiClient.GetTickersAsync();

            if (response.Success)
            {
                return new BittrexApiTickersData
                {
                    Sequence = response.Data.Timestamp.Ticks,
                    Deltas = response.Data.Ticks.Select(x => new BittrexApiTickersData.Ticker()
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
        public async Task<BittrexApiMarketSummariesData> GetMarketSummariesData()
        {
            var response = await ApiClient.GetTickersAsync();

            if (response.Success)
            {
                return new BittrexApiMarketSummariesData
                {
                    Sequence = response.Data.Timestamp.Ticks,
                    Deltas = response.Data.Ticks.Select(x => new BittrexApiMarketSummariesData.MarketSummary()
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

        public async Task<BittrexApiMarketData[]> GetMarketsData()
        {
            var response = await ApiClient.GetSymbolsAsync();

            if (response.Success)
            {
                return response.Data.Select(x => new BittrexApiMarketData()
                {
                    Symbol = x.Symbol,
                    BaseCurrencySymbol = string.Empty, //unused
                    QuoteCurrencySymbol = string.Empty, //unused
                    CreatedAt = DateTime.MinValue, //TODO
                    MinTradeSize = x.MinOrderValue,
                    Notice = string.Empty,
                    Status = x.State.ToString().ToUpper(), //ONLINE is the only one that we care about,
                    Precision = x.PricePrecision, //TODO: We probably have to add support for AmountPrecision too 
                    Tags = null //TODO we might have to use this
                }).ToArray();
            }
            else
                throw new NotImplementedException();
        }

        public async Task<OrderData[]> GetClosedOrdersData(string startAfterOrderId)
        {
            var response = await ApiClient.GetHistoryOrdersAsync();

            if (response.Success)
            {
                return response.Data.Orders.Select(x => new OrderData(x)).ToArray();
            }
            else
                throw new NotImplementedException();
        }

        public async Task<OrderData[]> GetOpenOrdersData()
        {
            var response = await ApiClient.GetOpenOrdersAsync();

            if (response.Success)
            {
                return response.Data.Select(x => new OrderData(x)).ToArray();
            }
            else
                throw new NotImplementedException();
        }

        public async Task<OrderData> GetOrderData(string orderId)
        {
            var response = await ApiClient.GetOrderInfoAsync(long.Parse(orderId));

            if (response.Success)
            {
                return new OrderData(response.Data);
            }
            else
                throw new NotImplementedException();
        }

        public async Task<OrderData> BuyLimit(string marketSymbol, decimal quantity, decimal limit, string clientOrderId = null)
        {
            return await ExecuteLimitOrder(OrderDirection.BUY, marketSymbol, quantity, limit, clientOrderId: clientOrderId);
        }

        public async Task<OrderData> SellLimit(string marketSymbol, decimal quantity, decimal limit, string clientOrderId = null)
        {
            return await ExecuteLimitOrder(OrderDirection.SELL, marketSymbol, quantity, limit, clientOrderId: clientOrderId);
        }

        public async Task<OrderData> BuyMarket(string marketSymbol, decimal quantity)
        {
            return await ExecuteMarketOrder(OrderDirection.BUY, marketSymbol, quantity);
        }

        public async Task<OrderData> SellMarket(string marketSymbol, decimal quantity)
        {
            return await ExecuteMarketOrder(OrderDirection.SELL, marketSymbol, quantity);
        }

        public async Task<OrderData> CancelOrder(string orderId)
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
                    var balance = new BittrexApiBalanceData()
                    {
                        AccountId = huobiBalance.AccountId.ToString(),
                        Sequence = huobiBalance.ChangeTime.GetValueOrDefault().Ticks,
                        Delta = new BittrexApiBalanceData.Balance()
                        {
                            Available = huobiBalance.Available.GetValueOrDefault(),
                            CurrencySymbol = huobiBalance.Currency,
                            Total = huobiBalance.Balance.GetValueOrDefault(),
                            UpdatedAt = huobiBalance.ChangeTime.GetValueOrDefault()
                        }
                    };

                    foreach (var callback in onBalanceCallBacks)
                    {
                        callback(balance);
                    }
                });

                //await SocketClient.SubscribeToOrderUpdatesAsync(
                //    onOrderSubmitted: orderUpdate =>
                //    {
                //        var order = new BittrexApiOrderData()
                //        {
                //            Sequence = DateTime.UtcNow.Ticks,
                //            AccountId = orderUpdate.AccountId.ToString(),
                //            Delta = new BittrexApiOrderData.Order()
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
                //        var order = new BittrexApiOrderData()
                //        {

                //        };
                //        foreach (var callback in onOrderCallBacks)
                //        {
                //            callback(order);
                //        }
                //    },
                //    onOrderMatched: matchedOrder =>
                //    {
                //        var order = new BittrexApiOrderData()
                //        {

                //        };
                //        foreach (var callback in onOrderCallBacks)
                //        {
                //            callback(order);
                //        }
                //    });

                var ordersTimer = new System.Threading.Timer(async e =>
                {
                    var openOrdersData = await GetOpenOrdersData();

                    foreach (var callback in onOrderCallBacks)
                    {
                        callback(openOrdersData);
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

        public async Task<OrderData> ExecuteLimitOrder(OrderDirection direction, string marketSymbol, decimal quantity, decimal limit, bool useCredits = true, string clientOrderId = null)
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

        private async Task<OrderData> ExecuteMarketOrder(OrderDirection direction, string marketSymbol, decimal quantity, bool useCredits = true)
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
            try
            {
                if (string.IsNullOrWhiteSpace(restResponse.Content))
                {
                    Logger.Instance.LogUnexpectedError($"Unknown API error data: {restResponse.ErrorMessage}. {JsonConvert.SerializeObject(restResponse.ErrorException)}");
                    return ApiErrorType.UnknownError;
                }

                var errorData = JsonConvert.DeserializeObject<BittrexApiErrorData>(restResponse.Content);

                return errorData.Code.ToUpperInvariant() switch
                {
                    "INSUFFICIENT_FUNDS" => ApiErrorType.InsufficientFunds,
                    "MIN_TRADE_REQUIREMENT_NOT_MET " => ApiErrorType.DustTrade,
                    "DUST_TRADE_DISALLOWED" => ApiErrorType.DustTrade,
                    "DUST_TRADE_DISALLOWED_MIN_VALUE" => ApiErrorType.DustTrade,
                    "INSUFFICIENT_AWARDS" => ApiErrorType.RetryLater,
                    "MARKET_OFFLINE" => ApiErrorType.MarketOffline,
                    "POST_ONLY" => ApiErrorType.RetryLater,
                    "MAX_ORDERS_ALLOWED" => ApiErrorType.RetryLater,
                    "ORDER_NOT_OPEN" => ApiErrorType.OrderNotOpen,
                    "THROTTLED" => ApiErrorType.Throttled,
                    "CANNOT_ESTIMATE_COMMISSION" => ApiErrorType.CannotEstimateCommission,
                    "RATE_PRECISION_NOT_ALLOWED" => ApiErrorType.PrecisionNotAllowed,
                    "MIN_TRADE_REQUIREMENT_NOT_MET" => ApiErrorType.DustTrade,
                    "CLIENTORDERID_ALREADY_EXISTS" => ApiErrorType.ClientOrderIdAlreadyExists,
                    _ when restResponse.StatusCode == HttpStatusCode.TooManyRequests => ApiErrorType.Throttled,
                    _ when restResponse.StatusCode == HttpStatusCode.NotFound => ApiErrorType.MarketOffline,
                    _ when restResponse.StatusCode == HttpStatusCode.ServiceUnavailable => ApiErrorType.MarketOffline,
                    _ when restResponse.StatusCode == HttpStatusCode.Unauthorized => ApiErrorType.Unauthorized,
                    _ when restResponse.StatusCode == HttpStatusCode.Forbidden => ApiErrorType.Unauthorized,
                    _ => ApiErrorType.UnknownError
                };
            }
            catch (Exception e)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Error when serializing restResponse. Using ToString instead");
                sb.AppendLine("RestResponse:");
                sb.AppendLine(restResponse.Content);
                sb.AppendLine("RestException:");
                sb.AppendLine(restResponse.ErrorException.ToString());
                sb.AppendLine("SerializationError:");
                sb.AppendLine(JsonConvert.SerializeObject(e));

                Logger.Instance.LogUnexpectedError(sb.ToString());

                return ApiErrorType.UnknownError;
            }
        }
    }
}