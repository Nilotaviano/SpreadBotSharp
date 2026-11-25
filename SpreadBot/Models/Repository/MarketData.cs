using System;

namespace SpreadBot.Models.Repository
{
    public class MarketData : IMessage
    {
        public MarketData() { }

        public MessageType MessageType => MessageType.MarketData;

        public string Symbol { get; set; } //CCXT format: "LTC/BTC"
        public decimal? LastTradeRate { get; set; }
        public decimal? BidRate { get; set; }
        public decimal? AskRate { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Volume { get; set; }
        public decimal? QuoteVolume { get; set; } //"Quote" means BTC in the LTC/BTC market, for example
        public decimal? PercentChange { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public decimal? MinTradeSize { get; set; } //Minimum order quantity in the traded coin (e.g. LTC)
        public decimal? PriceTick { get; set; } //Smallest price increment for orders
        public DateTime? CreatedAt { get; set; }
        public string Notice { get; set; }

        //SpreadBot calls the quote currency the "base market": BTC in LTC/BTC
        public string BaseMarket => Symbol.Split('/')[1];
        public string Target => TargetOf(Symbol);

        public static string TargetOf(string symbol) => symbol.Split('/')[0];
        public decimal SpreadPercentage => SpreadPercentageOf(BidRate, AskRate);

        public static decimal SpreadPercentageOf(decimal? bid, decimal? ask) => ask > 0 && bid > 0 ? (ask.Value - bid.Value) / ask.Value * 100 : 0; //Formula source = https://www.calculatorsoup.com/calculators/financial/bid-ask-calculator.php

        public EMarketStatus? Status { get; set; }

        public bool? IsTokenizedSecurity { get; set; }
    }
}
