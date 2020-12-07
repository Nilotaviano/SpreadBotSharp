using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    public class Logger
    {
        private static BlockingCollection<Log> pendingLogs;

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
                        break;
                    case LogLevel.Error:
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        break;
                    case LogLevel.UnexpectedError:
                        Console.ForegroundColor = ConsoleColor.DarkRed;
                        break;
                }

                Console.WriteLine(log.Content);
            }
        }

        public class Log
        {
            public LogLevel LogLevel { get; set; }
            public string Content { get; set; }
        }

        public enum LogLevel
        {
            Message,
            Error,
            UnexpectedError
        }
    }
}
