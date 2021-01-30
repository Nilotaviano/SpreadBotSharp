using SpreadBot.Logic;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;

namespace SpreadBot.Infrastructure
{
    public class BotContext
    {
        public event EventHandler ContextChanged;

        public Guid Guid { get; private set; }

        public readonly SpreadConfiguration spreadConfiguration;
        public readonly Stopwatch buyStopwatch = new Stopwatch();

        public MarketData latestMarketData;
        public OrderData currentOrderData;
        public decimal boughtPrice;
        private BotState botState;
        private decimal heldAmount;
        private decimal balance;

        public BotContext(SpreadConfiguration spreadConfiguration, MarketData marketData, BotState buy, decimal existingDust)
        {
            Guid = Guid.NewGuid();
            this.spreadConfiguration = spreadConfiguration;
            this.latestMarketData = marketData;
            this.BotState = buy;
            this.Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
            this.HeldAmount = existingDust;
        }

        //Initial balance + profit/loss
        public decimal Balance
        {
            get => balance; set
            {
                balance = value;
                NotifyPropertyChanged();
            }
        }

        //Amount held of the market currency
        public decimal HeldAmount
        {
            get => heldAmount;
            set
            {
                heldAmount = value;
                NotifyPropertyChanged();
            }
        }

        public BotState BotState
        {
            get => botState; set
            {
                botState = value;
                NotifyPropertyChanged();
            }
        }

        private void NotifyPropertyChanged()
        {
            ContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
