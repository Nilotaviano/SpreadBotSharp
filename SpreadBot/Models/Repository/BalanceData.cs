using System;
using System.Collections.Generic;
using System.Text;
using static SpreadBot.Models.API.ApiBalanceData;

namespace SpreadBot.Models.Repository
{
    public class BalanceData : IMessage
    {
        public BalanceData() { }

        public BalanceData(Balance balance)
        {
            CurrencyAbbreviation = balance.CurrencySymbol;
            Amount = balance.Available;
        }

        public MessageType MessageType => MessageType.BalanceData;
        
        public string CurrencyAbbreviation { get; set; }
        public decimal Amount { get; set; }
    }
}
