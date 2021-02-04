using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    public class Logger
    {
        private BlockingCollection<Log> pendingLogs;

        private static Logger instance;

        private Logger()
        {
            pendingLogs = new BlockingCollection<Log>();

            Task.Run(ConsumePendingLogs);
        }

        public static Logger Instance
        {
            get
            {
                if (instance == null)
                    instance = new Logger();

                return instance;
            }
        }

        public void LogMessage(string message)
        {
            pendingLogs.Add(new Log() { LogLevel = LogLevel.Message, Content = message });
        }

        public void LogError(string message)
        {
            pendingLogs.Add(new Log() { LogLevel = LogLevel.Error, Content = message });
        }

        public void LogUnexpectedError(string message)
        {
            pendingLogs.Add(new Log() { LogLevel = LogLevel.UnexpectedError, Content = message });
        }

        private void ConsumePendingLogs()
        {
            foreach (var log in pendingLogs.GetConsumingEnumerable())
            {
                switch (log.LogLevel)
                {
                    case LogLevel.Message:
                        Console.ForegroundColor = ConsoleColor.Blue;
                        Console.BackgroundColor = ConsoleColor.White;
                        Console.Out.Write(GetTimestamp());
                        Console.Out.WriteLine(log.Content);
                        break;
                    case LogLevel.Error:
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.BackgroundColor = ConsoleColor.White;
                        Console.Error.Write(GetTimestamp());
                        Console.Error.WriteLine(log.Content);
                        break;
                    case LogLevel.UnexpectedError:
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        Console.BackgroundColor = ConsoleColor.White;
                        Console.Error.Write(GetTimestamp());
                        Console.Error.WriteLine(log.Content);
                        break;
                }
            }
        }

        private string GetTimestamp()
        {
            var now = DateTime.UtcNow;
            return $"{now.Day}/{now.Month}T{now.ToShortTimeString()}Z - ";
        }

        private class Log
        {
            public LogLevel LogLevel { get; set; }
            public string Content { get; set; }
        }

        private enum LogLevel
        {
            Message,
            Error,
            UnexpectedError
        }
    }
}
