﻿using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class BittrexApiMarketSummariesData
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

            public MarketData ToMarketData()
            {
                return new MarketData()
                {
                    High = this.High,
                    Low = this.Low,
                    PercentChange = this.PercentChange,
                    Volume = this.Volume,
                    QuoteVolume = this.QuoteVolume,
                    UpdatedAt = this.UpdatedAt,
                    Symbol = this.Symbol
                };
            }
        }
    }
}