using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Stopwatch buyStopwatch = new Stopwatch();

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
            latestMarketData = marketData;

            switch (botState)
            {
                case BotState.Buy:
                    {
                        try
                        {
                            if (!marketData.BidRate.HasValue)
                                return;

                            if (marketData.Spread >= spreadConfiguration.MinimumSpread)
                            {

                                decimal bidPrice = marketData.BidRate.Value + 1.Satoshi();
                                decimal amount = Balance * (1 - exchange.FeeRate) / bidPrice;

                                var orderData = await exchange.BuyLimit(MarketSymbol, amount, bidPrice);
                                await ProcessOrderDataInternal(orderData);
                            }
                            else
                                FinishWork();
                        }
                        catch (ExchangeRequestException e)
                        {
                            //TODO: Handle specific errors
                            Logger.LogError($"BuyLimit error: {JsonSerializer.Serialize(e)}");
                        }

                        break;
                    }
                case BotState.BuyOrderActive:
                    {
                        try
                        {
                            if (marketData.Spread < spreadConfiguration.MinimumSpread)
                            {
                                //Cancel order and exit
                                var orderData = await exchange.CancelOrder(currentOrderData.Id);
                                await ProcessOrderDataInternal(orderData);
                            }
                            else if (marketData.BidRate - currentOrderData.Limit > spreadConfiguration.MaxBidAskDifferenceFromOrder)
                            {
                                //Cancel order and switch to BotState.Buy
                                var orderData = await exchange.CancelOrder(currentOrderData.Id);
                                await ProcessOrderDataInternal(orderData);
                            }
                        }
                        catch (ExchangeRequestException e)
                        {
                            //TODO: Handle specific errors
                            Logger.LogError($"CancelOrder error: {JsonSerializer.Serialize(e)}");
                        }

                        break;
                    }
                case BotState.Sell:
                    {
                        try
                        {
                            if (!marketData.AskRate.HasValue)
                                return;

                            decimal askPrice = marketData.AskRate.Value - 1.Satoshi();

                            var orderData = await exchange.SellLimit(MarketSymbol, HeldAmount, askPrice);
                            await ProcessOrderDataInternal(orderData);
                        }
                        catch (ExchangeRequestException e)
                        {
                            //TODO: Handle specific errors
                            Logger.LogError($"SellLimit error: {JsonSerializer.Serialize(e)}");
                        }

                        break;
                    }
                case BotState.SellOrderActive:
                    try
                    {
                        if (currentOrderData.Limit - marketData.AskRate > spreadConfiguration.MaxBidAskDifferenceFromOrder)
                        {
                            //cancel order and switch to BotState.Sell
                            if (buyStopwatch.Elapsed.TotalMinutes > spreadConfiguration.MinutesForLoss)
                            {
                                var orderData = await exchange.CancelOrder(currentOrderData.Id);
                                await ProcessOrderDataInternal(orderData);
                            }
                        }
                    }
                    catch (ExchangeRequestException e)
                    {
                        //TODO: Handle specific errors
                        Logger.LogError($"CancelOrder error: {JsonSerializer.Serialize(e)}");
                    }

                    break;
            }
        }

        private async Task ProcessOrderDataInternal(OrderData orderData)
        {
            if(orderData?.Status == OrderStatus.OPEN)
                SetCurrentOrderData(orderData);

            await ProcessOrderData(orderData);
        }

        /// <summary>
        /// ProcessOrderData should only be called from ProcessOrderDataInternalInternal or as a callback to dataRepository.SubscribeToOrderData
        /// </summary>
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
            dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            SetCurrentOrderData(null);
            semaphore.Clear();
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
