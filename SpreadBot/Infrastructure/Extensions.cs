using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public static class Extensions
    {
        public static void ThrowIfArgumentIsNull(this object arg, string argName)
        {
            if (arg == null)
                throw new ArgumentNullException(argName);
        }

        public static decimal Satoshi(this int amount)
        {
            return 0.00000001m * amount;
        }
    }
}
