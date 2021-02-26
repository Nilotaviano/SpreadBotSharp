using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic
{
    public class Bot
    {
        public readonly BotContext botContext;

        private readonly Action<Bot> unallocateBotCallback;
        private readonly SemaphoreQueue semaphore = new SemaphoreQueue(1, 1);

        private readonly DataRepository dataRepository;
        private IExchange exchange => dataRepository.Exchange;

        private readonly IBotStrategy botStrategy;

        public Bot(DataRepository dataRepository, BotContext context, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory)
        {
            this.dataRepository = dataRepository;
            this.botContext = context;
            this.unallocateBotCallback = unallocateBotCallback;
            botStrategy = botStrategiesFactory.GetStrategy();
        }

        public Bot(DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, decimal existingDust, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory)
            : this(dataRepository, new BotContext(spreadConfiguration, marketData, BotState.Buying, existingDust), unallocateBotCallback, botStrategiesFactory)
        { }

        public Guid Guid => botContext.Guid;
        public string BaseMarket => botContext.spreadConfiguration.BaseMarket;
        public string MarketSymbol => botContext.LatestMarketData.Symbol;
        public decimal Balance => botContext.Balance;
        public decimal HeldAmount => botContext.HeldAmount;
        public decimal LastTradeRate => botContext.LatestMarketData.LastTradeRate.GetValueOrDefault();

        private void SetCurrentOrderData(OrderData value)
        {
            if (botContext.CurrentOrderData?.Id != value?.Id)
            {
                if (botContext.CurrentOrderData != null)
                    dataRepository.UnsubscribeToOrderData(botContext.CurrentOrderData.ClientOrderId, Guid);

                if (value?.Status == OrderStatus.OPEN)
                    dataRepository.SubscribeToOrderData(value.ClientOrderId, Guid, ProcessMessage);
            }
            if (value?.Status == OrderStatus.CLOSED)
                dataRepository.UnsubscribeToOrderData(value.ClientOrderId, Guid);


            botContext.CurrentOrderData = value;
        }

        public async void Start()
        {
            LogMessage($"started on {MarketSymbol}");

            if (botContext.CurrentOrderData != null)
            {
                if (!dataRepository.OrdersData.TryGetValue(botContext.CurrentOrderData.ClientOrderId, out var updatedOrder))
                {
                    try
                    {
                        updatedOrder = await dataRepository.Exchange.GetOrderData(botContext.CurrentOrderData.ClientOrderId);
                    } catch (Exception e)
                    {
                        LogError(e.ToString());
                    }
                }
                    
                await ProcessOrderData(updatedOrder);
            }

            //This will trigger a call to ProcessMessage
            dataRepository.SubscribeToMarketData(MarketSymbol, Guid, ProcessMessage);
        }

        //Doesn't return Task because this shouldn't be awaited
        private async void ProcessMessage(IMessage message)
        {
            message.ThrowIfArgumentIsNull(nameof(message));

            LogMessage($"processing message{Environment.NewLine}: {JsonConvert.SerializeObject(message)}");

            if (botContext.BotState == BotState.FinishedWork)
            {
                LogError("Bot is still running after FinishWork was called");
            }
            else
            {
                await semaphore.WaitAsync();

                try
                {
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
                        case ApiErrorType.MarketOffline:
                        case ApiErrorType.Throttled:
                            //Do nothing, try again on the next cycle
                            LogError($"{e.ApiErrorType}: {e}");
                            break;
                        default:
                            //TODO: Log all of the bot's state/properties/fields
                            LogUnexpectedError($"{e.ApiErrorType}: {e}");
                            break;
                    };
                }
                catch (Exception e)
                {
                    LogUnexpectedError($"Bot {Guid}: Unexpected exception: {e}{Environment.NewLine}Context: {JsonConvert.SerializeObject(botContext)}");
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        private async Task ProcessMarketData(MarketData marketData)
        {
            botContext.LatestMarketData = marketData;

            await botStrategy.ProcessMarketData(dataRepository, botContext, ExecuteOrderFunction, FinishWork);
        }

        private async Task ExecuteOrderFunction(Func<Task<OrderData>> func)
        {
            var orderData = await func();
            SetCurrentOrderData(orderData);
            await ProcessOrderData(orderData);
        }

        //TODO: Refactor/clean this method
        private async Task ProcessOrderData(OrderData orderData)
        {
            if (orderData.Id != botContext.CurrentOrderData?.Id)
                return;

            switch (orderData?.Status)
            {
                case OrderStatus.OPEN:
                    botContext.BotState = (orderData?.Direction) switch
                    {
                        OrderDirection.BUY => BotState.BuyOrderActive,
                        OrderDirection.SELL => BotState.SellOrderActive,
                        _ => throw new ArgumentException(),
                    };
                    break;
                case OrderStatus.CLOSED:
                    SetCurrentOrderData(null);

                    UpdateContext(orderData);

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

                Logger.Instance.LogUnexpectedError(errorMessage);
            }

            switch (orderData?.Direction)
            {
                case OrderDirection.BUY:
                    botContext.HeldAmount += orderData.FillQuantity;
                    botContext.Balance -= orderData.Proceeds + orderData.Commission;
                    botContext.BoughtPrice = orderData.Limit;
                    botContext.buyStopwatch.Restart();
                    break;
                case OrderDirection.SELL:
                    botContext.HeldAmount -= orderData.FillQuantity;
                    // TODO: This may break when recovering a bot after the order was closed because the balance was already integrated with total balance
                    botContext.Balance += orderData.Proceeds - orderData.Commission;
                    break;
                default:
                    throw new ArgumentException();
            }
        }

        private async Task FinishWork()
        {
            botContext.BotState = BotState.FinishedWork;
            dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            SetCurrentOrderData(null);

            semaphore.Clear();

            if (HeldAmount > 0)
                await CleanDust();

            unallocateBotCallback(this);
            NetProfitRecorder.Instance.RecordProfit(botContext.spreadConfiguration, this);
            LogMessage($"finished on {MarketSymbol}");
        }

        private async Task CleanDust(bool retry = true)
        {
            //Check if dust is worth at least the fee it would cost to clean
            bool dustIsWorthCleaning = HeldAmount * LastTradeRate > 2 * exchange.FeeRate * botContext.spreadConfiguration.MinimumNegotiatedAmount;

            if (!dustIsWorthCleaning)
                return;

            OrderData sellOrder = null;
            try
            {
                sellOrder = await exchange.SellMarket(MarketSymbol, HeldAmount.CeilToPrecision(botContext.LatestMarketData.Precision));
            }
            catch (ApiException e) when (e.ApiErrorType == ApiErrorType.RetryLater && retry) //Try just once more. TODO: Investigate if any more ApiErrorTypes should go here
            {
                await CleanDust(false);
            }
            catch (ApiException e) when (e.ApiErrorType == ApiErrorType.DustTrade)
            {
                try
                {
                    OrderData buyOrder = null;

                    decimal buyAmount = (botContext.spreadConfiguration.MinimumNegotiatedAmount / LastTradeRate).CeilToPrecision(botContext.LatestMarketData.Precision);
                    buyOrder = await exchange.BuyMarket(MarketSymbol, buyAmount);

                    UpdateContext(buyOrder);

                    sellOrder = await exchange.SellMarket(MarketSymbol, HeldAmount.CeilToPrecision(botContext.LatestMarketData.Precision));
                }
                catch (Exception ex)
                {
                    LogUnexpectedError($"Unexpected error on CleanDust:{ex}{Environment.NewLine}Context: {JsonConvert.SerializeObject(botContext, Formatting.Indented)}");
                }
            }
            catch (Exception e)
            {
                LogUnexpectedError($"Unexpected error on CleanDust:{e}{Environment.NewLine}Context: {JsonConvert.SerializeObject(botContext, Formatting.Indented)}");
            }

            UpdateContext(sellOrder);
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
