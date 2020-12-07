﻿namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class BittrexApiTickersData
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