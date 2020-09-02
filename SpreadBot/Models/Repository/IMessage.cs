using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    interface IMessage
    {
        public MessageType MessageType { get; }
    }

    public enum MessageType
    {
        MarketData,
        BalanceData,
        OrderData
    }
}
