using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    public class MarketData : IMessage
    {
        public MessageType MessageType => MessageType.MarketData;

        public string Symbol { get; set; }
        public decimal LastTradeRate { get; set; }
        public decimal BidRate { get; set; }
        public decimal AskRate { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Volume { get; set; }
        public decimal QuoteVolume { get; set; } //Volume of the base market (i.e. BTC)
        public decimal PercentChange { get; set; }
        public DateTime UpdatedAt { get; set; }

        public string BaseMarket => Symbol.Split('-')[1];
        public decimal Spread => (AskRate - BidRate) / BidRate * 100; //Formula source = https://www.calculatorsoup.com/calculators/financial/bid-ask-calculator.php
    }
}
