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

        public static decimal CeilToPrecision(this decimal limit, int? precision)
        {
            if (precision.HasValue)
            {
                decimal multiplier = Convert.ToDecimal(Math.Pow(10, Convert.ToDouble(precision)));
                return Math.Ceiling(limit * multiplier) / multiplier;
            }
            else
                return limit;
        }

        public static string ToLocalFilePath(this string value)
        {
            return Path.Combine(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName), value);
        }
    }
}
