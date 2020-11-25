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
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly IExchange exchange;
        private readonly SpreadConfiguration spreadConfiguration;
        private MarketData latestMarketData;
        private readonly Action<Bot> unallocateBotCallback;
        private readonly Stopwatch buyStopwatch = new Stopwatch();

        private readonly SemaphoreQueue semaphore = new SemaphoreQueue(1, 1);

        private OrderData currentOrderData = null;

        private BotState botState;

        private readonly Dictionary<BotState, IBotStateStrategy> botStateStrategyDictionary;

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.exchange = dataRepository.Exchange;
            this.spreadConfiguration = spreadConfiguration;
            this.latestMarketData = marketData;
            this.unallocateBotCallback = unallocateBotCallback;
            Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
            Guid = Guid.NewGuid();
            botState = BotState.Buy;
            botStateStrategyDictionary = botStrategiesFactory.GetStrategiesDictionary();
        }

        public Guid Guid { get; private set; }
        public Guid SpreadConfigurationGuid => spreadConfiguration.Guid;
        public string MarketSymbol => latestMarketData.Symbol;
        public decimal Balance { get; private set; } //Initial balance + profit/loss
        public decimal HeldAmount { get; private set; } //Amount held of the market currency

        private void SetCurrentOrderData(OrderData value)
        {
            if (currentOrderData?.Id != value?.Id)
            {
                if (currentOrderData != null)
                    dataRepository.UnsubscribeToOrderData(currentOrderData.Id, Guid);

                if (value?.Status == OrderStatus.OPEN)
                    dataRepository.SubscribeToOrderData(value.Id, Guid, ProcessMessage);
            }
            if (value?.Status == OrderStatus.CLOSED)
                dataRepository.UnsubscribeToOrderData(value.Id, Guid);


            currentOrderData = value;
        }

        public void Start()
        {
            //This will trigger a call to ProcessMessage
            dataRepository.SubscribeToMarketData(MarketSymbol, Guid, ProcessMessage);
        }

        //Doesn't return Task because this shouldn't be awaited
        private async void ProcessMessage(IMessage message)
        {
            message.ThrowIfArgumentIsNull(nameof(message));

            if (botState == BotState.FinishedWork)
            {
                Logger.LogError("Bot is still running after FinishWork was called");
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
                finally
                {
                    semaphore.Release();
                }
            }
        }

        private async Task ProcessMarketData(MarketData marketData)
        {
            latestMarketData = marketData;

            try
            {
                await botStateStrategyDictionary[botState].ProcessMarketData(exchange, spreadConfiguration, buyStopwatch, Balance, HeldAmount, ExecuteOrderFunction, FinishWork, currentOrderData, latestMarketData);
            }
            catch (Exception e)
            {
                //TODO: log all bot properties and fields here as well
                Logger.LogError($"ProcessMarketData error on {botState} state : {JsonSerializer.Serialize(e)}");
            }
        }

        private async Task ExecuteOrderFunction(Func<Task<OrderData>> func)
        {
            var orderData = await func();
            SetCurrentOrderData(orderData);
            await ProcessOrderData(orderData);
        }

        //Refactor/clean this method
        private async Task ProcessOrderData(OrderData orderData)
        {
            if (orderData.Id != currentOrderData?.Id)
                return;

            switch (orderData?.Status)
            {
                case OrderStatus.OPEN:
                    botState = (orderData?.Direction) switch
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
                            HeldAmount += orderData.FillQuantity;
                            Balance -= orderData.Proceeds + orderData.Commission;
                            buyStopwatch.Restart();
                            break;
                        case OrderDirection.SELL:
                            HeldAmount -= orderData.FillQuantity;
                            Balance += orderData.Proceeds - orderData.Commission;
                            break;
                        default:
                            throw new ArgumentException();
                    }

                    if (HeldAmount * latestMarketData.AskRate > appSettings.MinimumNegotiatedAmount)
                    {
                        botState = BotState.Sell;
                        await ProcessMarketData(latestMarketData); //So that we immediatelly set a sell order
                    }
                    else if (Balance > appSettings.MinimumNegotiatedAmount)
                        botState = BotState.Buy;
                    else
                        FinishWork(); //Can't buy or sell, so stop

                    break;
                default:
                    throw new ArgumentException();
            }
        }

        private void FinishWork()
        {
            botState = BotState.FinishedWork;
            dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            SetCurrentOrderData(null);
            semaphore.Clear();
            unallocateBotCallback(this);
        }
    }

    public enum BotState
    {
        Buy,
        BuyOrderActive,
        Sell,
        SellOrderActive,
        FinishedWork
    }

}
