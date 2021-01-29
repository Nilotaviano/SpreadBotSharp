using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    public class MarketData : IMessage
    {
        public MarketData() { }

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
        public int? Precision { get; set; } //Number of decimal places allowed on the price when creating an order
        public DateTime? CreatedAt { get; set; }
        public string Notice { get; set; }

        public string BaseMarket => Symbol.Split('-')[1];
        public string Target => Symbol.Split('-')[0];
        public decimal SpreadPercentage => AskRate > 0 && BidRate > 0 ? (AskRate.Value - BidRate.Value) / AskRate.Value * 100 : 0; //Formula source = https://www.calculatorsoup.com/calculators/financial/bid-ask-calculator.php

        public AggregatorQuote AggregatorQuote { get; set; }

        public EMarketStatus? Status { get; set; }

        public bool? IsTokenizedSecurity { get; set; }
    }
}
