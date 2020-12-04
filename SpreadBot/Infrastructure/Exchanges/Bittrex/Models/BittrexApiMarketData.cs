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
    }
}