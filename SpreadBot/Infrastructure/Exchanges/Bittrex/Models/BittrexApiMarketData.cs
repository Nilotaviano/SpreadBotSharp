using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
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

        public MarketData ToMarketData()
        {
            return new MarketData()
            {
                Symbol = this.Symbol,
                MinTradeSize = this.MinTradeSize,
                Precision = this.Precision,
                CreatedAt = this.CreatedAt,
                Notice = this.Notice,
                Status = this.Status == "ONLINE" ? EMarketStatus.Online : EMarketStatus.Offline
            };
        }
    }
}