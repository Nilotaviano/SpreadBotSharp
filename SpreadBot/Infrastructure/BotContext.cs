using SpreadBot.Logic;
using SpreadBot.Models.Repository;
using System;
using Newtonsoft.Json;

namespace SpreadBot.Infrastructure
{
    public class BotContext
    {
        public event EventHandler ContextChanged;

        public Guid Guid { get; private set; }

        public readonly SpreadConfiguration spreadConfiguration;

        private MarketData latestMarketData;
        private OrderData currentOrderData;
        private decimal boughtPrice;
        private DateTime? buyFilledAt;
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

        public DateTime? BuyFilledAt
        {
            get => buyFilledAt;
            set
            {
                buyFilledAt = value;
                NotifyPropertyChanged();
            }
        }

        public bool CanSellAtLoss(DateTime utcNow)
        {
            return BuyFilledAt.HasValue && (utcNow - BuyFilledAt.Value).TotalMinutes > spreadConfiguration.MinutesForLoss;
        }

        /// <summary>
        /// The lowest sell price that recovers what the held coins cost, with both the buy and the sell charged <paramref name="feeRate"/>.
        /// A buy fee taken from the coins (B / (1 - fee) per coin held) costs more than one charged on top (B × (1 + fee)), so it assumes the former.
        /// </summary>
        public decimal BreakEvenPrice(decimal feeRate)
        {
            return BoughtPrice / ((1 - feeRate) * (1 - feeRate));
        }

        private void NotifyPropertyChanged()
        {
            ContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
