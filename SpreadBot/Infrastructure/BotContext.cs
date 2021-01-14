using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Logic;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;

namespace SpreadBot.Infrastructure
{
    public class BotContext
    {
        public Guid Guid { get; private set; }

        public readonly AppSettings appSettings;
        public readonly IExchange exchange;
        public readonly SpreadConfiguration spreadConfiguration;
        public MarketData latestMarketData;
        public BotState botState;
        public readonly Stopwatch buyStopwatch = new Stopwatch();
        public OrderData currentOrderData;
        public decimal Balance { get; set; } //Initial balance + profit/loss
        public decimal boughtPrice;
        public decimal HeldAmount { get; set; } //Amount held of the market currency

        public BotContext(AppSettings appSettings, IExchange exchange, SpreadConfiguration spreadConfiguration, MarketData marketData, BotState buy, decimal existingDust)
        {
            Guid = Guid.NewGuid();
            this.appSettings = appSettings;
            this.exchange = exchange;
            this.spreadConfiguration = spreadConfiguration;
            this.latestMarketData = marketData;
            this.botState = buy;
            this.Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
            this.HeldAmount = existingDust;
        }
    }
}
