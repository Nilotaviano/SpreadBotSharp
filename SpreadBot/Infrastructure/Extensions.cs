using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

        public static string Hash(this string input)
        {
            string hash;
            using (SHA512 sha512 = new SHA512Managed())
            {
                var hashBytes = sha512.ComputeHash(Encoding.UTF8.GetBytes(input));

                hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLower();
            }

            return hash;
        }

        public static string Sign(this string input, string key)
        {
            string hash;
            using (HMACSHA512 hmac = new HMACSHA512(Encoding.UTF8.GetBytes(key)))
            {
                var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));

                hash = BitConverter.ToString(hashBytes).Replace("-", string.Empty).ToLower();
            }

            return hash;
        }

        //TODO: Create test case to validate this behavior
        //TODO: figure out if we should use Ceil/Floor instead of Round, 
        //because if we do end up truncating a Satoshi unit, the price won't be incremented/decremented from the current bid/ask when creating an order
        public static decimal RoundOrderLimitPrice(this decimal limit, int? precision)
        {
            return precision.HasValue ? Math.Round(limit, precision.Value) : limit;
        }

        public static string ToLocalFilePath(this string value)
        {
            return Path.Combine(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName), value);
        }
    }
}
