using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public enum OrderStatus
    {
        UNDEFINED,
        OPEN,
        CLOSED,
        /// <summary>Sent, but whether the exchange accepted it is not known yet; only its client order id identifies it.</summary>
        UNKNOWN
    }
}
