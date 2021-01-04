using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace SpreadBot.Logic
{
    public class Bot
    {
        private readonly Action<Bot> unallocateBotCallback;
        private readonly SemaphoreQueue semaphore = new SemaphoreQueue(1, 1);
        private readonly Dictionary<BotState, IBotStateStrategy> botStateStrategyDictionary;

        private readonly BotContext botContext;

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory)
        {
            botContext = new BotContext(appSettings, dataRepository, dataRepository.Exchange, spreadConfiguration, marketData, BotState.Buying);
            this.unallocateBotCallback = unallocateBotCallback;
            botStateStrategyDictionary = botStrategiesFactory.GetStrategiesDictionary();
        }

        public Guid Guid => botContext.Guid;
        public Guid SpreadConfigurationGuid => botContext.spreadConfiguration.Guid;
        public string MarketSymbol => botContext.latestMarketData.Symbol;
        public decimal Balance => botContext.Balance;

        private void SetCurrentOrderData(OrderData value)
        {
            if (botContext.currentOrderData?.Id != value?.Id)
            {
                if (botContext.currentOrderData != null)
                    botContext.dataRepository.UnsubscribeToOrderData(botContext.currentOrderData.Id, Guid);

                if (value?.Status == OrderStatus.OPEN)
                    botContext.dataRepository.SubscribeToOrderData(value.Id, Guid, ProcessMessage);
            }
            if (value?.Status == OrderStatus.CLOSED)
                botContext.dataRepository.UnsubscribeToOrderData(value.Id, Guid);


            botContext.currentOrderData = value;
        }

        public void Start()
        {
            LogMessage($"started on {MarketSymbol}");
            //This will trigger a call to ProcessMessage
            botContext.dataRepository.SubscribeToMarketData(MarketSymbol, Guid, ProcessMessage);
        }

        //Doesn't return Task because this shouldn't be awaited
        private async void ProcessMessage(IMessage message)
        {
            message.ThrowIfArgumentIsNull(nameof(message));

            LogMessage($"processing message{Environment.NewLine}: {JsonConvert.SerializeObject(message)}");

            if (botContext.botState == BotState.FinishedWork)
            {
                Logger.Instance.LogError("Bot is still running after FinishWork was called");
            }
            else
            {
                try
                {
                    await semaphore.WaitAsync();

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
                    //TODO: Clean up/refactor
                    switch (e.ApiErrorType)
                    {
                        case ApiErrorType.DustTrade when botContext.botState == BotState.Bought:
                            //The bot is trying to sell too little of a coin, so we switch to Buy state to accumulate more
                            botContext.botState = BotState.Buying;
                            break;
                        case ApiErrorType.InsufficientFunds:
                            //Too many bots running?
                            FinishWork();
                            break;
                        case ApiErrorType.MarketOffline when botContext.botState == BotState.Buying:
                            FinishWork();
                            break;
                        case ApiErrorType.OrderNotOpen:
                            //Bot tried to cancel an order that has just been executed (I'm assuming)
                            //Closed order data will be received soon, so no need to do anything here
                        case ApiErrorType.MarketOffline:
                        case ApiErrorType.Throttled:
                            //Do nothing, try again on the next cycle
                            Logger.Instance.LogError($"{e.ApiErrorType}: {e}");
                            break;
                        default:
                            //TODO: Log all of the bot's state/properties/fields
                            Logger.Instance.LogUnexpectedError($"{e.ApiErrorType}: {e}");
                            break;
                    };
                }
                catch (Exception e)
                {
                    Logger.Instance.LogUnexpectedError($"Bot {Guid}: Unexpected exception: {e}{Environment.NewLine}Context: {JsonConvert.SerializeObject(botContext)}");
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        private async Task ProcessMarketData(MarketData marketData)
        {
            botContext.latestMarketData = marketData;

            await botStateStrategyDictionary[botContext.botState].ProcessMarketData(botContext, ExecuteOrderFunction, FinishWork);
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
            if (orderData.Id != botContext.currentOrderData?.Id)
                return;

            switch (orderData?.Status)
            {
                case OrderStatus.OPEN:
                    botContext.botState = (orderData?.Direction) switch
                    {
                        OrderDirection.BUY => BotState.BuyOrderActive,
                        OrderDirection.SELL => BotState.SellOrderActive,
                        _ => throw new ArgumentException(),
                    };
                    break;
                case OrderStatus.CLOSED:
                    SetCurrentOrderData(null);

                    switch (orderData?.Direction)
                    {
                        case OrderDirection.BUY:
                            botContext.HeldAmount += orderData.FillQuantity;
                            botContext.Balance -= orderData.Proceeds + orderData.Commission;
                            botContext.boughtPrice = orderData.Limit;
                            botContext.buyStopwatch.Restart();
                            break;
                        case OrderDirection.SELL:
                            botContext.HeldAmount -= orderData.FillQuantity;
                            botContext.Balance += orderData.Proceeds - orderData.Commission;
                            break;
                        default:
                            throw new ArgumentException();
                    }

                    if (botContext.HeldAmount * botContext.latestMarketData.AskRate > botContext.appSettings.MinimumNegotiatedAmount)
                    {
                        botContext.botState = BotState.Bought;
                        await ProcessMarketData(botContext.latestMarketData); //So that we immediatelly set a sell order
                    }
                    else if (botContext.Balance > botContext.appSettings.MinimumNegotiatedAmount)
                        botContext.botState = BotState.Buying;
                    else
                        FinishWork(); //Can't buy or sell, so stop

                    break;
                default:
                    throw new ArgumentException();
            }
        }

        private void FinishWork()
        {
            botContext.botState = BotState.FinishedWork;
            botContext.dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            SetCurrentOrderData(null);
            semaphore.Clear();
            unallocateBotCallback(this);
            NetProfitRecorder.Instance.RecordProfit(botContext.spreadConfiguration, this);
            LogMessage($"finished on {MarketSymbol}");
        }

        private void LogMessage(string message)
        {
            Logger.Instance.LogMessage($"Bot {Guid}: {message}");
        }
    }

    public class BotContext
    {
        public Guid Guid { get; private set; }
        
        public readonly AppSettings appSettings;
        public readonly DataRepository dataRepository;
        public readonly IExchange exchange;
        public readonly SpreadConfiguration spreadConfiguration;
        public MarketData latestMarketData;
        public BotState botState;
        public readonly Stopwatch buyStopwatch = new Stopwatch();
        public OrderData currentOrderData = null;
        public decimal Balance { get; set; } //Initial balance + profit/loss
        public decimal boughtPrice = 0;
        public decimal HeldAmount { get; set; } = 0; //Amount held of the market currency

        public BotContext(AppSettings appSettings, DataRepository dataRepository, IExchange exchange, SpreadConfiguration spreadConfiguration, MarketData marketData, BotState buy)
        {
            Guid = Guid.NewGuid();
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.exchange = exchange;
            this.spreadConfiguration = spreadConfiguration;
            this.latestMarketData = marketData;
            this.botState = buy;
            this.Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
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
