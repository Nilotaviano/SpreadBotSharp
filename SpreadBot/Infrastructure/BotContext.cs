using SpreadBot.Logic;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace SpreadBot.Infrastructure
{
    public class BotContext
    {
        public event EventHandler ContextChanged;

        public Guid Guid { get; private set; }

        public readonly SpreadConfiguration spreadConfiguration;
        public readonly Stopwatch buyStopwatch = new Stopwatch();

        private MarketData latestMarketData;
        private OrderData currentOrderData;
        private decimal boughtPrice;
        private BotState botState;
        private decimal heldAmount;
        private decimal balance;

        [JsonIgnore]
        public readonly SemaphoreQueue Semaphore = new SemaphoreQueue(1, 1);

        //Just so that this exists o coordinatorContext.json
        public int SemaphoreCurrentCount => Semaphore.CurrentCount;

        public BotContext(SpreadConfiguration spreadConfiguration, MarketData marketData, BotState buy, decimal existingDust)
        {
            Guid = Guid.NewGuid();
            this.spreadConfiguration = spreadConfiguration;
            this.LatestMarketData = marketData;
            this.BotState = buy;
            this.Balance = spreadConfiguration.AllocatedAmountOfBaseCurrency;
            this.HeldAmount = existingDust;
        }

        //Initial balance + profit/loss
        public decimal Balance
        {
            get => balance;
            set
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
            get => botState;
            set
            {
                botState = value;
                NotifyPropertyChanged();
            }
        }

        public OrderData CurrentOrderData
        {
            get => currentOrderData;
            set
            {
                currentOrderData = value;
                NotifyPropertyChanged();
            }
        }

        public MarketData LatestMarketData
        {
            get => latestMarketData;
            set
            {
                latestMarketData = value;
                NotifyPropertyChanged();
            }
        }
        public decimal BoughtPrice
        {
            get => boughtPrice;
            set
            {
                boughtPrice = value;
                NotifyPropertyChanged();
            }
        }

        private void NotifyPropertyChanged()
        {
            ContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
