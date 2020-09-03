using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Models
{
    public enum OrderTimeInForce
    {
        UNDEFINED,
        GOOD_TIL_CANCELLED,
        IMMEDIATE_OR_CANCEL,
        FILL_OR_KILL,
        POST_ONLY_GOOD_TIL_CANCELLED,
        BUY_NOW
    }
}
