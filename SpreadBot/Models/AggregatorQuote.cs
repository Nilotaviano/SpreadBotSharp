using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public class AggregatorQuote
    {
        public decimal CurrentPrice { get; set; }
        public decimal volume_24h { get; set; }
        public decimal PriceChangePercentage24H { get; set; }
        public DateTime LastUpdated { get; set; }
    }
}
