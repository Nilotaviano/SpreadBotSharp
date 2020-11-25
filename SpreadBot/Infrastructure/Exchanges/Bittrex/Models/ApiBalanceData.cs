using System;

namespace SpreadBot.Infrastructure.Exchanges.Bittrex.Models
{
    public class ApiBalanceData
    {
        public string AccountId { get; set; }
        public int Sequence { get; set; }
        public Balance Delta { get; set; }

        public class Balance
        {
            public string CurrencySymbol { get; set; }
            public decimal Total { get; set; }
            public decimal Available { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}
