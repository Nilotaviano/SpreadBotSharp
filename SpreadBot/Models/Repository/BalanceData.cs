using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    public class BalanceData : IMessage
    {
        public MessageType MessageType => MessageType.BalanceData;
        
        public string CurrencyAbbreviation { get; set; }
        public decimal Amount { get; set; }
    }
}
