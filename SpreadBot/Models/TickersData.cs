using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public class TickersData
    {
        public int Sequence { get; set; }

        public Ticker[] Deltas { get; set; }


        public class Ticker
        {
            public string Symbol { get; set; }
            public decimal LastTradeRate { get; set; }
            public decimal BidRate { get; set; }
            public decimal AskRate { get; set; }
        }
    }
}
