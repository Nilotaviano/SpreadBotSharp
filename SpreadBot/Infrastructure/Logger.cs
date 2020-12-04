using System;
using System.Collections.Generic;
using System.Text;

namespace SpreadBot.Infrastructure
{
    public static class Logger
    {
        public static void LogMessage(string message)
        {
            //TODO
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine(message);
        }

        public static void LogError(string message)
        {
            //TODO
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
        }

        public static void LogUnexpectedError(string message)
        {
            //TODO
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine(message);
        }
    }
}
