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
            public double LastTradeRate { get; set; }
            public double BidRate { get; set; }
            public double AskRate { get; set; }
        }
    }
}
