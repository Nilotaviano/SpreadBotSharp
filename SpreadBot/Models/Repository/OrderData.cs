using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models.Repository
{
    public class OrderData : IMessage
    {
        public MessageType MessageType => MessageType.OrderData;
    }
}
