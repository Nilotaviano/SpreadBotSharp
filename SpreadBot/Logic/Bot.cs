using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
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

        private readonly SemaphoreQueue semaphore = new SemaphoreQueue(1, 1);

        private OrderData currentOrderData = null;

        private BotState botState;

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback)
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
        }

        public Guid Guid { get; private set; }
        public Guid SpreadConfigurationGuid => spreadConfiguration.Guid;
        public string MarketSymbol => latestMarketData.Symbol;
        public decimal Balance { get; private set; } //Initial balance + profit/loss

        private OrderData CurrentOrderData
        {
            get => currentOrderData;
            set
            {
                if (currentOrderData?.Id != value?.Id)
                {
                    //TODO: I just wanted to get this logic done, it shouldn't be here in the final bot implementation
                    if (currentOrderData != null)
                        dataRepository.UnsubscribeToOrderData(currentOrderData.Id, Guid);

                    if (value?.Status == OrderStatus.OPEN)
                        dataRepository.SubscribeToOrderData(value.Id, Guid, ProcessMessage);
                    else
                        dataRepository.UnsubscribeToOrderData(value.Id, Guid);
                }

                currentOrderData = value;
            }
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

        private async Task ProcessMarketData(MarketData marketData)
        {
            switch(botState)
            {
                case BotState.Buy:
                    try
                    {
                        var orderData = await exchange.BuyLimit(MarketSymbol, Balance, marketData.BidRate + 1.Satoshi());

                        if (orderData != null && orderData.Status == OrderStatus.OPEN)
                        {
                            CurrentOrderData = orderData;
                            botState = BotState.BuyOrderActive;
                        }
                        else if (orderData.Status == OrderStatus.CLOSED) //TODO: Check if this ever happens
                            await ProcessOrderData(orderData);
                    }
                    catch (ExchangeRequestException e)
                    {
                        //TODO: Handle specific errors
                        Logger.LogError($"BuyLimit error: {JsonSerializer.Serialize(e)}");
                    }

                    break;
                case BotState.BuyOrderActive:
                    break;
                case BotState.Sell:
                    break;
                case BotState.SellOrderActive:
                    break;
            }
        }

        private async Task ProcessOrderData(OrderData orderData)
        {
            CurrentOrderData = orderData;
        }

        private void FinishWork()
        {
            dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            unallocateBotCallback(this);
        }

        private enum BotState
        {
            Buy,
            BuyOrderActive,
            Sell,
            SellOrderActive
        }
    }
}
