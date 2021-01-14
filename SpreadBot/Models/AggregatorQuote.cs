using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public class AggregatorQuote
    {
        public decimal price { get; set; }
        public decimal volume_24h { get; set; }
        public decimal percent_change_1h { get; set; }
        public decimal percent_change_24h { get; set; }
        public decimal percent_change_7d { get; set; }
        public decimal market_cap { get; set; }
        public DateTime last_updated { get; set; }
    }
}
