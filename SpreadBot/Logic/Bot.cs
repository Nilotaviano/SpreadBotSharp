using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

namespace SpreadBot.Logic
{
    public class Bot
    {
        public readonly BotContext botContext;

        private readonly Action<Bot> unallocateBotCallback;

        private readonly DataRepository dataRepository;
        private readonly BalanceLedger balanceLedger;
        private IExchange exchange => dataRepository.Exchange;

        private readonly IBotStrategy botStrategy;

        private readonly Timer processingMessageTimeExceededTimer;

        private const int MaxConsecutiveInsufficientFundsRejections = 3;
        private int consecutiveInsufficientFundsRejections;

        private volatile bool orderRequestInFlight;

        //Only read and written under BotContext.Semaphore
        private MarketData lastProcessedMarketData;

        private static readonly TimeSpan UnknownOrderLookupInterval = TimeSpan.FromSeconds(5);
        private DateTime nextUnknownOrderLookup;

        public Bot(DataRepository dataRepository, BotContext context, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory, BalanceLedger balanceLedger)
        {
            this.dataRepository = dataRepository;
            this.balanceLedger = balanceLedger;
            this.botContext = context;
            this.unallocateBotCallback = unallocateBotCallback;
            botStrategy = botStrategiesFactory.GetStrategy();

            processingMessageTimeExceededTimer = new Timer(TimeSpan.FromMinutes(1).TotalMilliseconds);
            processingMessageTimeExceededTimer.Elapsed += ProcessingMessageTimeExceededTimer_Elapsed;
            processingMessageTimeExceededTimer.AutoReset = false;
        }

        public Bot(DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, decimal existingDust, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory, BalanceLedger balanceLedger)
            : this(dataRepository, new BotContext(spreadConfiguration, marketData, BotState.Buying, existingDust), unallocateBotCallback, botStrategiesFactory, balanceLedger)
        { }

        public Guid Guid => botContext.Guid;
        public string BaseMarket => botContext.spreadConfiguration.BaseMarket;
        public string MarketSymbol => botContext.LatestMarketData.Symbol;
        public decimal Balance => botContext.Balance;
        public decimal HeldAmount => botContext.HeldAmount;
        public decimal LastTradeRate => botContext.LatestMarketData.LastTradeRate.GetValueOrDefault();

        /// <summary>
        /// An order request (place, cancel, lookup) was sent and the order's state hasn't been applied since: it is still out, or it
        /// failed without saying whether it was carried out. Either way the exchange may already have moved the order's funds.
        /// </summary>
        public bool OrderRequestInFlight => orderRequestInFlight;

        private void SetCurrentOrderData(OrderData value)
        {
            if (OrderIdentity(botContext.CurrentOrderData) != OrderIdentity(value))
            {
                if (botContext.CurrentOrderData != null)
                    dataRepository.UnsubscribeToOrderData(botContext.CurrentOrderData.ClientOrderId, Guid);

                if (value?.Status == OrderStatus.OPEN || value?.Status == OrderStatus.UNKNOWN)
                {
                    dataRepository.SubscribeToOrderData(value, Guid, ProcessMessage);
                }
            }
            if (value?.Status == OrderStatus.CLOSED)
            {
                dataRepository.UnsubscribeToOrderData(value.ClientOrderId, Guid);
            }


            botContext.CurrentOrderData = value;
            orderRequestInFlight = false;
        }

        //An order whose placement is unknown has no id until the exchange reports it, so its client order id identifies it
        private static string OrderIdentity(OrderData order) => order?.Id ?? order?.ClientOrderId;

        public async void Start()
        {
            try
            {
                await StartAsync();
            }
            catch (Exception e)
            {
                LogUnexpectedError($"Unhandled exception while starting: {e}");
            }
        }

        private async Task StartAsync()
        {
            LogMessage($"started on {MarketSymbol}");

            //A context restored in a sell state may have no fill time; start the loss timer now so the sell floor can still drop
            if ((botContext.BotState == BotState.Bought || botContext.BotState == BotState.SellOrderActive) && botContext.BuyFilledAt == null)
                botContext.BuyFilledAt = DateTime.UtcNow;

            if (botContext.CurrentOrderData != null)
            {
                var clientOrderId = botContext.CurrentOrderData.ClientOrderId;
                var orderId = botContext.CurrentOrderData.Id;
                var isCached = dataRepository.OrdersData.ContainsKey(clientOrderId);

                //Replays the cached order state, if any, and keeps delivering updates until the order closes
                dataRepository.SubscribeToOrderData(botContext.CurrentOrderData, Guid, ProcessMessage);

                //An order whose placement is unknown has no id to fetch; ProcessMarketData looks it up by client order id
                if (!isCached && botContext.CurrentOrderData.Status != OrderStatus.UNKNOWN)
                {
                    try
                    {
                        var updatedOrder = await dataRepository.Exchange.GetOrder(orderId, MarketSymbol);

                        if (updatedOrder != null)
                            ProcessMessage(updatedOrder);
                    }
                    catch (Exception e)
                    {
                        LogError($"Could not fetch order {clientOrderId} on start, waiting for the next resync: {e}");
                    }
                }
            }

            //This will trigger a call to ProcessMessage
            dataRepository.SubscribeToMarketData(MarketSymbol, Guid, ProcessMessage);
        }

        //Doesn't return Task because this shouldn't be awaited
        private async void ProcessMessage(IMessage message)
        {
            try
            {
                await ProcessMessageAsync(message);
            }
            catch (Exception e)
            {
                LogUnexpectedError($"Unhandled exception while processing a message: {e}");
            }
        }

        private async Task ProcessMessageAsync(IMessage message)
        {
            message.ThrowIfArgumentIsNull(nameof(message));

            LogMessage($"processing message{Environment.NewLine}: {JsonConvert.SerializeObject(message)}");

            if (botContext.BotState == BotState.FinishedWork)
            {
                LogMessage("Ignoring a message that arrived after the bot finished");
            }
            else
            {
                await botContext.Semaphore.WaitAsync();

                try
                {
                    //FinishWork may have run while this message waited for the semaphore
                    if (botContext.BotState == BotState.FinishedWork)
                        return;

                    if (message is MarketData marketData)
                    {
                        //Every update replaces the market's cache entry before reaching the bot, so one that is no longer the entry was
                        //overtaken while it waited (e.g. behind an order request): acting on it would cancel or place on stale prices,
                        //and the entry's own message is still to come. The entry may also arrive twice (e.g. a subscription's replay)
                        if ((dataRepository.MarketsData.TryGetValue(marketData.Symbol, out var current) && !ReferenceEquals(marketData, current))
                            || ReferenceEquals(marketData, lastProcessedMarketData))
                            return;

                        lastProcessedMarketData = marketData;
                    }

                    processingMessageTimeExceededTimer.Start();

                    switch (message.MessageType)
                    {
                        case MessageType.MarketData:
                            await ProcessMarketData(message as MarketData);
                            break;
                        case MessageType.OrderData:
                            await ProcessOrderData(message as OrderData);
                            break;
                        default:
                            throw new ArgumentException();
                    }
                }
                catch (ApiException e)
                {
                    LogError($"ApiException {e.ApiErrorType} context:{Environment.NewLine}{e}{Environment.NewLine}BotContext:{JsonConvert.SerializeObject(botContext, Formatting.Indented)}Message:{JsonConvert.SerializeObject(message, Formatting.Indented)}");

                    try
                    {
                        await HandleApiException(e);
                    }
                    catch (Exception handlingException)
                    {
                        LogUnexpectedError($"Error while handling {e.ApiErrorType}: {handlingException}");
                    }
                }
                catch (Exception e)
                {
                    LogUnexpectedError($"Bot {Guid}: Unexpected exception: {e}{Environment.NewLine}Context: {JsonConvert.SerializeObject(botContext)}");
                }
                finally
                {
                    botContext.Semaphore.Release();

                    if (botContext.BotState != BotState.FinishedWork) //otherwise processingMessageTimeExceededTimer would already be disposed
                        processingMessageTimeExceededTimer.Stop();
                }
            }
        }

        private async Task HandleApiException(ApiException e)
        {
            //TODO: Clean up/refactor
            switch (e.ApiErrorType)
            {
                case ApiErrorType.DustTrade when botContext.BotState == BotState.Bought:
                    //The bot is trying to sell too little of a coin, so we switch to Buy state to accumulate more
                    botContext.BotState = BotState.Buying;
                    break;
                case ApiErrorType.DustTrade when botContext.BotState == BotState.Buying:
                    await FinishWork();
                    break;
                case ApiErrorType.InsufficientFunds when botContext.BotState == BotState.Bought:
                    await ReconcileHeldAmountWithExchange();
                    break;
                case ApiErrorType.InsufficientFunds:
                    //Too many bots running?
                    await FinishWork();
                    break;
                case ApiErrorType.MarketOffline when botContext.BotState == BotState.Buying:
                    await FinishWork();
                    break;
                case ApiErrorType.OrderNotOpen:
                    //Bot tried to cancel an order that has just been executed (I'm assuming)
                    //Closed order data will be received soon, so no need to do anything here
                    //Nevermind the above, the WS message may be lost
                    var currentOrderId = botContext.CurrentOrderData?.Id;
                    if (currentOrderId != null)
                    {
                        try
                        {
                            await ExecuteOrderFunction(async () => await dataRepository.Exchange.GetOrder(currentOrderId, MarketSymbol));
                        }
                        //e.g. its trades don't show its fees yet; the next cancel or resync fetches it again
                        catch (ApiException lookupError) when (lookupError.ApiErrorType is ApiErrorType.RetryLater or ApiErrorType.Throttled or ApiErrorType.MarketOffline)
                        {
                            LogError($"Fetching order {currentOrderId}, which is no longer open, failed: {lookupError.ApiErrorType} {lookupError.Message}");
                        }
                    }
                    else
                        LogMessage("ORDER_NOT_OPEN with no current order: it was already closed and processed");
                    break;
                case ApiErrorType.MarketOffline:
                case ApiErrorType.Throttled:
                case ApiErrorType.RetryLater:
                    //Do nothing, try again on the next cycle
                    LogError($"{e.ApiErrorType}: {e}");
                    break;
                default:
                    //TODO: Log all of the bot's state/properties/fields
                    LogUnexpectedError($"{e.ApiErrorType}: {e}");
                    break;
            };
        }

        /// <summary>
        /// A sell was rejected for insufficient funds: compare HeldAmount with the exchange. Within the tolerance
        /// (fees or rounding the fill didn't report), sell what the exchange holds. Beyond it, retry: the coins may
        /// not be settled yet (e.g. a sell placed right after a cancel). After repeated rejections, finish, selling
        /// only what the exchange shows; the rest stays held, so it is recorded as dust instead of written off.
        /// </summary>
        private async Task ReconcileHeldAmountWithExchange()
        {
            const decimal tolerance = 0.02m;

            consecutiveInsufficientFundsRejections++;
            var currency = botContext.LatestMarketData.Target;

            if (consecutiveInsufficientFundsRejections >= MaxConsecutiveInsufficientFundsRejections)
            {
                LogUnexpectedError($"Sell rejected for insufficient funds {consecutiveInsufficientFundsRejections} times in a row while holding {HeldAmount}{currency}; finishing");

                decimal onExchange;
                try
                {
                    onExchange = await GetUnclaimedCoinsFromExchange(currency);
                }
                catch (Exception e)
                {
                    //Finishing market-sells the held coins, which may include other bots' coins until it's checked
                    LogError($"Could not check the exchange balance before finishing; retrying on the next rejection: {e}");
                    return;
                }

                await FinishWork(maxCoinsToSell: onExchange);
                return;
            }

            var unclaimed = await GetUnclaimedCoinsFromExchange(currency);

            if (unclaimed >= HeldAmount)
            {
                LogError($"Sell rejected for insufficient funds, but the exchange shows {unclaimed}{currency} unclaimed for {HeldAmount}{currency} held; retrying");
            }
            else if (unclaimed >= HeldAmount * (1 - tolerance))
            {
                LogError($"Exchange shows {unclaimed}{currency} unclaimed but the bot holds {HeldAmount}{currency}; selling the unclaimed amount");
                botContext.HeldAmount = unclaimed;
            }
            else
            {
                LogError($"Exchange shows {unclaimed}{currency} unclaimed but the bot holds {HeldAmount}{currency}, beyond the {tolerance:P0} tolerance; retrying");
            }
        }

        /// <summary>
        /// Coins available on the exchange (fresh from REST) minus those other bots hold, so this bot never claims their coins.
        /// </summary>
        private async Task<decimal> GetUnclaimedCoinsFromExchange(string currency)
        {
            var balances = await exchange.GetBalances();
            var available = balances.FirstOrDefault(b => b.CurrencyAbbreviation == currency)?.Amount ?? 0;

            return Math.Max(available - balanceLedger.ClaimedByOtherBots(currency, this), 0);
        }

        private async Task ProcessMarketData(MarketData marketData)
        {
            botContext.LatestMarketData = marketData;

            //The strategy would place another order while this one may be live
            if (botContext.CurrentOrderData?.Status == OrderStatus.UNKNOWN)
            {
                await ResolveUnknownOrder();
                return;
            }

            await botStrategy.ProcessMarketData(dataRepository, botContext, ExecuteOrderFunction, FinishWork);
        }

        /// <summary>
        /// Looks up the current order, whose placement is unknown, by its client order id (at most once per
        /// <see cref="UnknownOrderLookupInterval"/>). Found, it becomes a normal current order. Still not found
        /// <see cref="IExchange.OrderRequestLifetime"/> after it was sent, the exchange can no longer accept it, so it's dropped
        /// and the strategy places again. Stream updates and resyncs that report it adopt it in <see cref="ProcessOrderData"/>.
        /// </summary>
        private async Task ResolveUnknownOrder()
        {
            var unknown = botContext.CurrentOrderData;
            var now = DateTime.UtcNow;
            var expired = now - unknown.CreatedAt >= exchange.OrderRequestLifetime;

            if (now < nextUnknownOrderLookup)
                return;

            nextUnknownOrderLookup = now + UnknownOrderLookupInterval;

            OrderData found;
            var notFound = false;
            try
            {
                found = await exchange.GetOrderByClientId(unknown.ClientOrderId, unknown.MarketSymbol);
            }
            catch (ApiException e) when (e.ApiErrorType == ApiErrorType.OrderNotOpen)
            {
                //The stream may have reported it while the lookup ran: that update waits behind this message, and dropping the order would discard it
                notFound = !dataRepository.OrdersData.TryGetValue(unknown.ClientOrderId, out found);
            }
            catch (ApiException e)
            {
                //Without the order's state, holding is the only safe choice; HandleApiException reacts to failed placements and could finish the bot
                if (expired)
                    LogUnexpectedError($"Order {unknown.ClientOrderId}'s placement is still unknown {(now - unknown.CreatedAt).TotalSeconds:0}s after it was sent and looking it up fails: {e.ApiErrorType} {e.Message}");
                else
                    LogError($"Looking up order {unknown.ClientOrderId}, whose placement is unknown, failed: {e.ApiErrorType} {e.Message}");
                return;
            }

            if (notFound)
            {
                if (!expired)
                {
                    LogMessage($"Order {unknown.ClientOrderId}, whose placement is unknown, is not on the exchange yet");
                    return;
                }

                LogError($"Order {unknown.ClientOrderId} is still not on the exchange {exchange.OrderRequestLifetime.TotalSeconds}s after it was sent, so it was never placed");
                SetCurrentOrderData(null);
                return;
            }

            if (!ShowsOrderState(found))
            {
                var unsettled = $"Looking up order {unknown.ClientOrderId}, whose placement is unknown, gave no id or status (id: {found?.Id}, status: {found?.Status}); still waiting";
                if (expired)
                    LogUnexpectedError(unsettled);
                else
                    LogError(unsettled);
                return;
            }

            LogMessage($"Order {unknown.ClientOrderId}, whose placement was unknown, is on the exchange as {found.Status}");
            await ExecuteOrderFunction(() => Task.FromResult(found));
        }

        //Only such a report settles an order whose placement is unknown: adopting anything less would let the strategy place another order
        private static bool ShowsOrderState(OrderData order) =>
            order?.Id != null && (order.Status == OrderStatus.OPEN || order.Status == OrderStatus.CLOSED);

        private async Task ExecuteOrderFunction(Func<Task<OrderData>> func)
        {
            //Cleared once the request's answer is applied, or the order's state is next applied (SetCurrentOrderData). A request
            //that fails may still have been carried out (e.g. a cancel that timed out), so a failure leaves it set
            orderRequestInFlight = true;

            OrderData orderData;
            try
            {
                orderData = await func();
            }
            catch (OrderStateUnknownException e)
            {
                //It may be live: as the current order it blocks placing another one until it's found or given up on
                LogError($"{e.Message}; holding off until order {e.Order.ClientOrderId} is found or given up on");
                SetCurrentOrderData(e.Order);
                return;
            }

            if (orderData == null)
            {
                LogUnexpectedError("Exchange returned no order data; keeping the current order");
                return;
            }

            consecutiveInsufficientFundsRejections = 0;

            //A new order becomes the current one; updates to the current order follow ProcessOrderData's rules like stream updates
            if (orderData.Id != botContext.CurrentOrderData?.Id)
                SetCurrentOrderData(orderData);

            await ProcessOrderData(orderData);
            orderRequestInFlight = false;
        }

        //TODO: Refactor/clean this method
        private async Task ProcessOrderData(OrderData orderData)
        {
            //The exchange reports the order whose placement was unknown: it was accepted
            if (botContext.CurrentOrderData?.Status == OrderStatus.UNKNOWN && ShowsOrderState(orderData) && orderData.ClientOrderId == botContext.CurrentOrderData.ClientOrderId)
                SetCurrentOrderData(orderData);

            if (orderData.Id == null || orderData.Id != botContext.CurrentOrderData?.Id)
                return;

            switch (orderData.Status)
            {
                case OrderStatus.OPEN:
                    //Keep partial fills current, BalanceLedger counts them before the order closes; a late update with fewer fills is stale
                    if (orderData.FillQuantity >= botContext.CurrentOrderData.FillQuantity)
                        SetCurrentOrderData(orderData);
                    botContext.BotState = (orderData?.Direction) switch
                    {
                        OrderDirection.BUY => BotState.BuyOrderActive,
                        OrderDirection.SELL => BotState.SellOrderActive,
                        _ => throw new ArgumentException(),
                    };
                    break;
                case OrderStatus.CLOSED:
                    //Book the fill before dropping the order, so BalanceLedger never sees neither
                    try
                    {
                        UpdateContext(orderData);
                    }
                    catch (Exception e)
                    {
                        //Without its fill booked the bot's state is unknown; stop it rather than trade on it
                        LogUnexpectedError($"Could not book closed order {orderData.Id}; finishing: {e}");
                        SetCurrentOrderData(null);
                        await FinishWork();
                        return;
                    }

                    SetCurrentOrderData(null);

                    if (botContext.HeldAmount * botContext.LatestMarketData.AskRate > botContext.spreadConfiguration.MinimumNegotiatedAmount)
                    {
                        botContext.BotState = BotState.Bought;
                        await ProcessMarketData(botContext.LatestMarketData); //So that we immediatelly set a sell order
                    }
                    else if (botContext.Balance > botContext.spreadConfiguration.MinimumNegotiatedAmount)
                        botContext.BotState = BotState.Buying;
                    else
                        await FinishWork(); //Can't buy or sell, so stop

                    break;
                default:
                    throw new ArgumentException();
            }
        }

        private void UpdateContext(OrderData orderData)
        {
            if (orderData == null || orderData.Status != OrderStatus.CLOSED)
                return;

            if (orderData.FillQuantity != 0 ^ (orderData.Proceeds + orderData.Commission) != 0)
            {
                string errorMessage = $"Either fill quantity or proceeds is 0 while the other is not. " +
                    $"Fill: {orderData.FillQuantity}. Proceeds: {orderData.Proceeds + orderData.Commission}";

                LogUnexpectedError(errorMessage);
            }

            switch (orderData?.Direction)
            {
                case OrderDirection.BUY:
                    botContext.HeldAmount += orderData.FillQuantity - orderData.CoinCommission;
                    botContext.Balance -= orderData.Proceeds + orderData.Commission;
                    //A buy cancelled without fills bought nothing: the price and the loss timer stay those of the coins already held
                    if (orderData.FillQuantity > 0)
                    {
                        botContext.BoughtPrice = orderData.Limit;
                        botContext.BuyFilledAt = DateTime.UtcNow;
                    }
                    break;
                case OrderDirection.SELL:
                    botContext.HeldAmount -= orderData.FillQuantity + orderData.CoinCommission;
                    // TODO: This may break when recovering a bot after the order was closed because the balance was already integrated with total balance
                    botContext.Balance += orderData.Proceeds - orderData.Commission;
                    break;
                default:
                    throw new ArgumentException();
            }
        }

        private Task FinishWork() => FinishWork(maxCoinsToSell: null);

        /// <param name="maxCoinsToSell">Market-sells at most this many of the held coins; the rest stay held and the Coordinator records them as dust</param>
        private async Task FinishWork(decimal? maxCoinsToSell)
        {
            if (botContext.BotState == BotState.FinishedWork)
                return;

            botContext.BotState = BotState.FinishedWork;
            processingMessageTimeExceededTimer.Dispose();
            dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            SetCurrentOrderData(null);

            //Each step runs even if an earlier one fails, so the allocation is released and profit recorded
            await RunFinishStep("cleaning dust", async () =>
            {
                var coinsToSell = Math.Min(HeldAmount, maxCoinsToSell ?? HeldAmount);
                if (coinsToSell > 0)
                    await CleanDust(coinsToSell);
            });
            await RunFinishStep("unallocating", () =>
            {
                unallocateBotCallback(this);
                return Task.CompletedTask;
            });
            await RunFinishStep("recording profit", () =>
            {
                NetProfitRecorder.Instance.RecordProfit(botContext.spreadConfiguration, this);
                return Task.CompletedTask;
            });

            LogMessage($"finished on {MarketSymbol}");
        }

        private async Task RunFinishStep(string description, Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception e)
            {
                LogUnexpectedError($"Error while {description} on finish: {e}");
            }
        }

        private async Task CleanDust(decimal coinsToSell, bool retry = true)
        {
            //Check if dust is worth at least the fee it would cost to clean
            bool dustIsWorthCleaning = coinsToSell * LastTradeRate > 2 * exchange.FeeRate * botContext.spreadConfiguration.MinimumNegotiatedAmount;

            if (!dustIsWorthCleaning)
                return;

            OrderData sellOrder = null;
            try
            {
                sellOrder = await exchange.SellMarket(MarketSymbol, coinsToSell);
            }
            catch (ApiException e) when (e.ApiErrorType == ApiErrorType.RetryLater && retry) //Try just once more. TODO: Investigate if any more ApiErrorTypes should go here
            {
                await CleanDust(coinsToSell, false);
            }
            catch (Exception e)
            {
                LogUnexpectedError($"Unexpected error on CleanDust:{e}{Environment.NewLine}Context: {JsonConvert.SerializeObject(botContext, Formatting.Indented)}");
            }

            UpdateContext(sellOrder);
        }

        private void ProcessingMessageTimeExceededTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            LogUnexpectedError("Processing message time exceeded");
        }

        private void LogMessage(string message)
        {
            Logger.Instance.LogMessage($"Bot {Guid}: {message}");
        }

        private void LogError(string message)
        {
            Logger.Instance.LogError($"Bot {Guid}: {message}");
        }

        private void LogUnexpectedError(string message)
        {
            Logger.Instance.LogUnexpectedError($"Bot {Guid}: {message}");
        }
    }

    public enum BotState
    {
        Buying,
        BuyOrderActive,
        Bought,
        SellOrderActive,
        FinishedWork
    }
}
