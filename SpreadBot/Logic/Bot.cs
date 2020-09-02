using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Logic
{
    public class Bot
    {
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly SpreadConfiguration spreadConfiguration;
        private readonly MarketData marketData;
        private readonly Action<Bot> unallocateBotCallback;

        private readonly SemaphoreQueue semaphore = new SemaphoreQueue(1, 1);

        private string currentOrderId = null;

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.spreadConfiguration = spreadConfiguration;
            this.marketData = marketData;
            this.unallocateBotCallback = unallocateBotCallback;
            Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
            Guid = Guid.NewGuid();

            dataRepository.SubscribeToMarketData(MarketSymbol, Guid, ProcessMessage);
        }

        public Guid Guid { get; private set; }
        public Guid SpreadConfigurationGuid => spreadConfiguration.Guid;
        public string MarketSymbol => marketData.Symbol;
        public decimal Balance { get; private set; } //Initial balance + profit/loss

        private string CurrentOrderId
        {
            get => currentOrderId;
            set
            {
                //TODO: I just wanted to get this logic done, it shouldn't be here in the final bot implementation
                if (currentOrderId != null)
                    dataRepository.UnsubscribeToOrderData(currentOrderId, Guid);

                if (value != null)
                    dataRepository.SubscribeToOrderData(value, Guid, ProcessMessage);

                currentOrderId = value;
            }
        }

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
            throw new NotImplementedException();
        }

        private async Task ProcessOrderData(OrderData orderData)
        {
            throw new NotImplementedException();
        }

        private void FinishWork()
        {
            unallocateBotCallback(this);
            throw new NotImplementedException("Unsubscribe to dataRepository streams");
        }
    }
}
