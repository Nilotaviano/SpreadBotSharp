﻿using Newtonsoft.Json;
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

        private readonly BotContext botContext;
        private readonly DataRepository dataRepository;

        private readonly IBotStrategy botStrategy;

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory, decimal existingDust)
        {
            this.dataRepository = dataRepository;
            botContext = new BotContext(appSettings, dataRepository.Exchange, spreadConfiguration, marketData, BotState.Buying, existingDust);
            this.unallocateBotCallback = unallocateBotCallback;
            botStrategy = botStrategiesFactory.GetStrategy();
        }

        public Guid Guid => botContext.Guid;
        public Guid SpreadConfigurationGuid => botContext.spreadConfiguration.Guid;
        public string BaseMarket => botContext.spreadConfiguration.BaseMarket;
        public string MarketSymbol => botContext.latestMarketData.Symbol;
        public decimal Balance => botContext.Balance;
        public decimal HeldAmount => botContext.HeldAmount;
        public decimal LastTradeRate => botContext.latestMarketData.LastTradeRate.GetValueOrDefault();

        private void SetCurrentOrderData(OrderData value)
        {
            if (botContext.currentOrderData?.Id != value?.Id)
            {
                if (botContext.currentOrderData != null)
                    dataRepository.UnsubscribeToOrderData(botContext.currentOrderData.Id, Guid);

                if (value?.Status == OrderStatus.OPEN)
                    dataRepository.SubscribeToOrderData(value.Id, Guid, ProcessMessage);
            }
            if (value?.Status == OrderStatus.CLOSED)
                dataRepository.UnsubscribeToOrderData(value.Id, Guid);


            botContext.currentOrderData = value;
        }

        public void Start()
        {
            LogMessage($"started on {MarketSymbol}");
            //This will trigger a call to ProcessMessage
            dataRepository.SubscribeToMarketData(MarketSymbol, Guid, ProcessMessage);
        }

        //Doesn't return Task because this shouldn't be awaited
        private async void ProcessMessage(IMessage message)
        {
            message.ThrowIfArgumentIsNull(nameof(message));

            LogMessage($"processing message{Environment.NewLine}: {JsonConvert.SerializeObject(message)}");

            if (botContext.botState == BotState.FinishedWork)
            {
                LogError("Bot is still running after FinishWork was called");
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
                    LogError($"ApiException {e.ApiErrorType} context:{Environment.NewLine}{e}{Environment.NewLine}BotContext:{JsonConvert.SerializeObject(botContext, Formatting.Indented)}Message:{JsonConvert.SerializeObject(message, Formatting.Indented)}");

                    //TODO: Clean up/refactor
                    switch (e.ApiErrorType)
                    {
                        case ApiErrorType.DustTrade when botContext.botState == BotState.Bought:
                            //The bot is trying to sell too little of a coin, so we switch to Buy state to accumulate more
                            botContext.botState = BotState.Buying;
                            break;
                        case ApiErrorType.InsufficientFunds:
                            //Too many bots running?
                            await FinishWork();
                            break;
                        case ApiErrorType.MarketOffline when botContext.botState == BotState.Buying:
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
            botContext.latestMarketData = marketData;

            await botStrategy.ProcessMarketData(botContext, ExecuteOrderFunction, FinishWork);
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

                    if (botContext.HeldAmount * botContext.latestMarketData.AskRate > botContext.spreadConfiguration.MinimumNegotiatedAmount)
                    {
                        botContext.botState = BotState.Bought;
                        await ProcessMarketData(botContext.latestMarketData); //So that we immediatelly set a sell order
                    }
                    else if (botContext.Balance > botContext.spreadConfiguration.MinimumNegotiatedAmount)
                        botContext.botState = BotState.Buying;
                    else
                        await FinishWork(); //Can't buy or sell, so stop

                    break;
                default:
                    throw new ArgumentException();
            }
        }

        private async Task FinishWork()
        {
            if (HeldAmount > 0)
                await CleanDust();

            botContext.botState = BotState.FinishedWork;
            dataRepository.UnsubscribeToMarketData(MarketSymbol, Guid);
            SetCurrentOrderData(null);
            semaphore.Clear();
            unallocateBotCallback(this);
            NetProfitRecorder.Instance.RecordProfit(botContext.spreadConfiguration, this);
            LogMessage($"finished on {MarketSymbol}");
        }

        private async Task CleanDust(bool retry = true)
        {
            //Check if dust is worth at least the fee it would cost to clean
            bool dustIsWorthCleaning = HeldAmount * LastTradeRate > 2 * botContext.exchange.FeeRate * botContext.spreadConfiguration.MinimumNegotiatedAmount;

            if (!dustIsWorthCleaning)
                return;

            OrderData sellOrder = null;
            try
            {
                sellOrder = await botContext.exchange.SellMarket(MarketSymbol, HeldAmount.CeilToPrecision(botContext.latestMarketData.Precision));
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

                    decimal buyAmount = (botContext.spreadConfiguration.MinimumNegotiatedAmount / LastTradeRate).CeilToPrecision(botContext.latestMarketData.Precision);
                    buyOrder = await botContext.exchange.BuyMarket(MarketSymbol, buyAmount);

                    if (buyOrder != null && buyOrder.Status == OrderStatus.CLOSED)
                        botContext.HeldAmount += buyOrder.FillQuantity;

                    sellOrder = await botContext.exchange.SellMarket(MarketSymbol, HeldAmount.CeilToPrecision(botContext.latestMarketData.Precision));
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

            if (sellOrder != null && sellOrder.Status == OrderStatus.CLOSED)
            {
                botContext.HeldAmount -= sellOrder.FillQuantity;
                botContext.Balance += sellOrder.Proceeds - sellOrder.Commission;
            }
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
