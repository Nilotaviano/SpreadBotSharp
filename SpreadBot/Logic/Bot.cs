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

        public Bot(AppSettings appSettings, DataRepository dataRepository, SpreadConfiguration spreadConfiguration, MarketData marketData)
        {
            this.appSettings = appSettings;
            this.dataRepository = dataRepository;
            this.spreadConfiguration = spreadConfiguration;
            this.marketData = marketData;
            throw new NotImplementedException("Subscribe to streams");
        }
    }
}
