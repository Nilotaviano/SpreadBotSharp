using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public enum OrderType
    {
        UNDEFINED,
        LIMIT,
        MARKET,
        CEILING_LIMIT,
        CEILING_MARKET
    }
}
