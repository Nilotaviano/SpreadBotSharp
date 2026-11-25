using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>
        /// Rounds a price up to the market's tick so it's valid for an order and never below <paramref name="price"/>.
        /// </summary>
        public static decimal CeilToTick(this decimal price, decimal? tick)
        {
            if (!tick.HasValue || tick.Value <= 0)
                return price;

            return Math.Ceiling(price / tick.Value) * tick.Value;
        }

        /// <summary>
        /// Writes to a temporary file in the same directory, flushes it to disk, then renames it over <paramref name="path"/>,
        /// so a crash mid-write leaves either the previous file or the new one, never a truncated file.
        /// </summary>
        public static void WriteAllTextAtomic(string path, string contents)
        {
            var tempPath = path + ".tmp";

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }

        public static string ToLocalFilePath(this string value)
        {
            return Path.Combine(Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName), value);
        }
    }
}
