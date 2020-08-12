using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public class MarketSummariesData
    {
        public int Sequence { get; set; }

        public MarketSummary[] Deltas { get; set; }

        public class MarketSummary
        {
            public string Symbol { get; set; }
            public double High { get; set; }
            public double Low { get; set; }
            public double Volume { get; set; }
            public double QuoteVolume { get; set; }
            public double PercentChange { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}