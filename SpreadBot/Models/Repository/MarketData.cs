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

        public MarketData(ApiTickersData.Ticker ticker)
        {
            ticker.ThrowIfArgumentIsNull(nameof(ticker));

            LastTradeRate = ticker.LastTradeRate;
            AskRate = ticker.AskRate;
            BidRate = ticker.BidRate;
            Symbol = ticker.Symbol;
        }

        public MarketData(ApiMarketSummariesData.MarketSummary marketSummary)
        {
            marketSummary.ThrowIfArgumentIsNull(nameof(marketSummary));

            High = marketSummary.High;
            Low = marketSummary.Low;
            PercentChange = marketSummary.PercentChange;
            Volume = marketSummary.Volume;
            QuoteVolume = marketSummary.QuoteVolume;
            UpdatedAt = marketSummary.UpdatedAt;
            Symbol = marketSummary.Symbol;
            MinTradeSize = marketSummary.MinTradeSize;
            Precision = marketSummary.Precision;
        }

        public MessageType MessageType => MessageType.MarketData;

        public string Symbol { get; set; }
        public decimal? LastTradeRate { get; set; }
        public decimal? BidRate { get; set; }
        public decimal? AskRate { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Volume { get; set; }
        public decimal? QuoteVolume { get; set; } //"Quote" means BTC in the LTC-BTC market, for example
        public decimal? PercentChange { get; set; }
        public decimal? MinTradeSize { get; set; } //In base currency (minTradeSize is the minimum order quantity in the base currency (e.g. LTC)
        public int? Precision { get; set; } //Number of decimal places allowed on the price when creating an order
        public DateTime? UpdatedAt { get; set; }

        public string BaseMarket => Symbol.Split('-')[1];
        public decimal SpreadPercentage => (AskRate.GetValueOrDefault(0) - BidRate.GetValueOrDefault(0)) / BidRate.GetValueOrDefault(1) * 100; //Formula source = https://www.calculatorsoup.com/calculators/financial/bid-ask-calculator.php
    }
}
