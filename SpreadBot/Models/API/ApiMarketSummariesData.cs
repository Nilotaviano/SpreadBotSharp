using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.API
{
    public class ApiMarketSummariesData
    {
        public int Sequence { get; set; }

        public MarketSummary[] Deltas { get; set; }

        public class MarketSummary
        {
            public string Symbol { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Volume { get; set; }
            public decimal QuoteVolume { get; set; }
            public decimal PercentChange { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}