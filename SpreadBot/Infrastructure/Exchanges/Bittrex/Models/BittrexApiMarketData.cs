using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class BittrexApiMarketData
    {
        public string Symbol { get; set; }
        public string BaseCurrencySymbol { get; set; }
        public string QuoteCurrencySymbol { get; set; }
        public decimal MinTradeSize { get; set; }
        public int Precision { get; set; }
        public string Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Notice { get; set; }
        public string[] Tags { get; set; }

        public string BaseMarket => Symbol.Split('-')[1];
        public string Target => Symbol.Split('-')[0];

        public Market ToMarketData()
        {
            return new Market()
            {
                Symbol = this.Symbol,
                Quote = this.BaseMarket,
                Target = this.Target,
                MinTradeSize = this.MinTradeSize,
                Precision = this.Precision,
                CreatedAt = this.CreatedAt,
                Notice = this.Notice,
                Status = this.Status == "ONLINE" ? EMarketStatus.Online : EMarketStatus.Offline,
                IsTokenizedSecurity = Tags?.Contains("TOKENIZED_SECURITY")
            };
        }
    }
}