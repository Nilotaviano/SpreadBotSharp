using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Logic
{
    public class Bot
    {
        private readonly AppSettings appSettings;
        private readonly DataRepository dataRepository;
        private readonly SpreadConfiguration spreadConfiguration;
        private readonly MarketData marketData;
        private readonly Action<Bot> unallocateBotCallback;

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData, Action<Bot> unallocateBotCallback)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.spreadConfiguration = spreadConfiguration;
            this.marketData = marketData;
            this.unallocateBotCallback = unallocateBotCallback;
            Balance = this.spreadConfiguration.AllocatedAmountOfBaseCurrency;
            Guid = Guid.NewGuid();
            throw new NotImplementedException("Subscribe to dataRepository streams");
        }

        public Guid Guid { get; private set; }

        public decimal Balance { get; private set; } //Initial balance + profit/loss

        public void FinishWork()
        {
            unallocateBotCallback(this);
            throw new NotImplementedException("Unsubscribe to dataRepository streams");
        }
    }
}
