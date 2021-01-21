using SpreadBot.Models.Repository;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
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

            public MarketData ToMarketData()
            {
                return new MarketData()
                {
                    LastTradeRate = this.LastTradeRate,
                    AskRate = this.AskRate,
                    BidRate = this.BidRate,
                    Symbol = this.Symbol
                };
            }
        }
    }
}
