using SpreadBot.Models.Repository;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class BittrexApiTickersData
    {
        public long Sequence { get; set; }

        public Ticker[] Deltas { get; set; }


        public class Ticker
        {
            public string Symbol { get; set; }
            public decimal LastTradeRate { get; set; }
            public decimal BidRate { get; set; }
            public decimal AskRate { get; set; }

            public string BaseMarket => Symbol.Split('-')[1];
            public string Target => Symbol.Split('-')[0];

            public Market ToMarketData()
            {
                return new Market()
                {
                    LastTradeRate = this.LastTradeRate,
                    AskRate = this.AskRate,
                    BidRate = this.BidRate,
                    Symbol = this.Symbol,
                    Quote = this.BaseMarket,
                    Target = this.Target,
                };
            }
        }
    }
}
