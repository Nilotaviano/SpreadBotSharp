using System;

namespace SpreadBot.Models.Repository
{
    public class Market : IMessage
    {
        public Market() { }

        public MessageType MessageType => MessageType.MarketData;

        public string Symbol { get; set; } //Must be in LTC-BTC format
        public decimal? LastTradeRate { get; set; }
        public decimal? BidRate { get; set; }
        public decimal? AskRate { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Volume { get; set; }
        public decimal? QuoteVolume { get; set; } //"Quote" means BTC in the LTC-BTC market, for example
        public decimal? PercentChange { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public decimal? MinTradeSize { get; set; } //In base currency (minTradeSize is the minimum order quantity in the base currency (e.g. LTC)
        public int? LimitPrecision { get; set; } //Number of decimal places allowed on the price when creating an order
        public int? AmountPrecision { get; set; } //Number of decimal places allowed on the amount when creating an order
        public DateTime? CreatedAt { get; set; }
        public string Notice { get; set; }

        private string quote;
        public string Quote { get => quote; set => quote = value?.ToUpper(); }
        private string target;
        public string Target { get => target; set => target = value?.ToUpper(); }

        public decimal SpreadPercentage => AskRate > 0 && BidRate > 0 ? (AskRate.Value - BidRate.Value) / AskRate.Value * 100 : 0; //Formula source = https://www.calculatorsoup.com/calculators/financial/bid-ask-calculator.php

        public AggregatorQuote AggregatorQuote { get; set; }

        public EMarketStatus? Status { get; set; }

        public bool? IsTokenizedSecurity { get; set; }
    }

    public class MarketSummaryData
    {
        public long Sequence { get; set; }
        public Market[] Markets { get; set; }
    }

    public class TickerData
    {
        public long Sequence { get; set; }
        public Market[] Markets { get; set; }
    }
}
