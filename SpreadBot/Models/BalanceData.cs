using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace SpreadBot.Models
{
    public class BalanceData
    {
        public string AccountId { get; set; }
        public int Sequence { get; set; }
        public Balance Delta { get; set; }

        public class Balance
        {
            public string CurrencySymbol { get; set; }
            public double Total { get; set; }
            public double Available { get; set; }
            public DateTime UpdatedAt { get; set; }
        }
    }
}
