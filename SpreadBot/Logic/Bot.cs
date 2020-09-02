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

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.spreadConfiguration = spreadConfiguration;
            this.marketData = marketData;
            this.unallocateBotCallback = unallocateBotCallback;
            Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
            Guid = Guid.NewGuid();
            throw new NotImplementedException("Subscribe to dataRepository streams");
        }

        public Guid Guid { get; private set; }
        public Guid SpreadConfigurationGuid => spreadConfiguration.Guid;
        public string MarketSymbol => marketData.Symbol;

        public decimal Balance { get; private set; } //Initial balance + profit/loss

        private async Task ProcessMessageAsync(/*TODO*/)
        {
            try
            {
                await semaphore.WaitAsync();
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void FinishWork()
        {
            unallocateBotCallback(this);
            throw new NotImplementedException("Unsubscribe to dataRepository streams");
        }
    }
}
