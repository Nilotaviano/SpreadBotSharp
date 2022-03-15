using CryptoExchange.Net.Authentication;
using Kucoin.Net.Clients;
using Kucoin.Net.Enums;
using Kucoin.Net.Objects;
using RestSharp;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Exchanges.KucoinWrapper
{
    public class KucoinClientWrapper : IExchange
    {
        private KucoinClient ApiClient { get; set; }
        private KucoinSocketClient SocketClient { get; set; }

        public bool IsSetup { get; private set; }

        public decimal FeeRate => 0.002m;

        private readonly ConcurrentBag<Action<BalanceData>> onBalanceCallBacks = new ConcurrentBag<Action<BalanceData>>();
        private readonly ConcurrentBag<Action<MarketSummaryData>> onSummariesCallBacks = new ConcurrentBag<Action<MarketSummaryData>>();
        private readonly ConcurrentBag<Action<TickerData>> onTickersCallBacks = new ConcurrentBag<Action<TickerData>>();
        private readonly ConcurrentBag<Action<OrderData>> onOrderCallBacks = new ConcurrentBag<Action<OrderData>>();

        public KucoinClientWrapper(string apiKey, string apiSecret, string apiPassPhrase)
        {
            ApiClient = new KucoinClient(new KucoinClientOptions() { ApiCredentials = new KucoinApiCredentials(apiKey, apiSecret, apiPassPhrase) });
            SocketClient = new KucoinSocketClient(new KucoinSocketClientOptions() { ApiCredentials = new KucoinApiCredentials(apiKey, apiSecret, apiPassPhrase) });
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
            var response = await ApiClient.SpotApi.Account.GetAccountsAsync();

            if (response.Success)
            {
                var balances = response.Data.Where(x => x.Type == AccountType.Trade).Select(KucoinTypeConverter.ConvertBalance);

                return new CompleteBalanceData(DateTime.UtcNow.Ticks, balances);
            }
            else
                throw new NotImplementedException();
        }

        //TODO: merge this and GetMarketSummariesData into a single method if possible
        public async Task<TickerData> GetTickersData()
        {
            var response = await ApiClient.SpotApi.ExchangeData.GetTickersAsync();

            if (response.Success)
            {
                return new TickerData
                {
                    Sequence = response.Data.Timestamp.Ticks,
                    Markets = response.Data.Data.Select(x => new Market()
                    {
                        AskRate = x.BestAskPrice,
                        BidRate = x.BestBidPrice,
                        LastTradeRate = x.LastPrice,
                        Symbol = x.Symbol,
                        High = x.HighPrice.GetValueOrDefault(),
                        Low = x.LowPrice.GetValueOrDefault(),
                        QuoteVolume = x.QuoteVolume.GetValueOrDefault(),
                        Volume = x.Volume,
                        UpdatedAt = response.Data.Timestamp,
                        PercentChange = x.ChangePercentage * 100,
                    }).ToArray()
                };
            }
            else
                throw new NotImplementedException();
        }

        //Not needed, as all data is present on GetTickersData above
        public Task<MarketSummaryData> GetMarketSummariesData()
        {
            return Task.FromResult(new MarketSummaryData());
        }

        public async Task<Market[]> GetMarketsData()
        {
            var response = await ApiClient.SpotApi.ExchangeData.GetSymbolsAsync();

            if (response.Success)
            {
                return response.Data.Select(x => new Market()
                {
                    Symbol = x.Symbol,
                    Quote = x.QuoteAsset,
                    Target = x.BaseAsset,
                    CreatedAt = DateTime.MinValue, //TODO
                    MinTradeSize = x.BaseMinQuantity,
                    Notice = string.Empty, //TODO we might have to use this
                    Status = x.EnableTrading ? EMarketStatus.Online : EMarketStatus.Offline, //ONLINE is the only one that we care about,
                    LimitPrecision = Convert.ToInt32(Math.Abs(Math.Log10(Convert.ToDouble(x.PriceIncrement)))),
                    AmountPrecision = Convert.ToInt32(Math.Abs(Math.Log10(Convert.ToDouble(x.BaseIncrement)))),
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
                var response = await ApiClient.SpotApi.Trading.GetOrdersAsync(startTime: startTime, status: Kucoin.Net.Enums.OrderStatus.Done, pageSize: 100);

                if (response.Success)
                {
                    var result = response.Data.Items.Select(KucoinTypeConverter.ConvertOrder).ToArray();
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
            var response = await ApiClient.SpotApi.Trading.GetOrdersAsync(status: Kucoin.Net.Enums.OrderStatus.Active, pageSize: 100);

            if (response.Success)
            {
                return response.Data.Items.Select(KucoinTypeConverter.ConvertOrder).ToArray();
            }
            else
                throw new NotImplementedException();
        }

        public async Task<Order> GetOrderData(string orderId)
        {
            var response = await ApiClient.SpotApi.Trading.GetOrderAsync(orderId);

            if (response.Success)
            {
                return KucoinTypeConverter.ConvertOrder(response.Data);
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
            var response = await ApiClient.SpotApi.Trading.CancelOrderAsync(orderId);

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
                await SocketClient.SpotStreams.SubscribeToBalanceUpdatesAsync(kucoinBalance =>
                {
                    var balance = new BalanceData()
                    {
                        Sequence = kucoinBalance.Data.Timestamp.Ticks,
                        Balance = KucoinTypeConverter.ConvertBalance(kucoinBalance.Data)
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
            var orderSide = direction switch
            {
                OrderDirection.BUY => OrderSide.Buy,
                OrderDirection.SELL => OrderSide.Sell,
                _ => throw new ArgumentException()
            };

            var response = await ApiClient.SpotApi.Trading.PlaceOrderAsync(marketSymbol, orderSide, NewOrderType.Limit, quantity, limit, postOnly: true, selfTradePrevention: SelfTradePrevention.CancelNewest, clientOrderId: clientOrderId);

            if (response.Success)
            {
                return await GetOrderData(response.Data.ToString());
            }
            else
                throw new NotImplementedException();
        }

        private async Task<Order> ExecuteMarketOrder(OrderDirection direction, string marketSymbol, decimal quantity, bool useCredits = true)
        {
            var orderSide = direction switch
            {
                OrderDirection.BUY => OrderSide.Buy,
                OrderDirection.SELL => OrderSide.Sell,
                _ => throw new ArgumentException()
            };

            var response = await ApiClient.SpotApi.Trading.PlaceOrderAsync(marketSymbol, orderSide, NewOrderType.Market, quantity, selfTradePrevention: SelfTradePrevention.CancelNewest);

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