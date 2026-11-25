using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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

            Task.Factory.StartNew(ConsumePendingLogs, System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Raised on the logger's thread after each log is written (e.g. for the dashboard's live log). A listener must only hand the
        /// entry off (e.g. queue it): anything slow stalls every log, and logging from it would feed itself.
        /// </summary>
        public event Action<LogEntry> Logged;

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

        /// <summary>
        /// Waits, at most <paramref name="timeout"/>, until everything logged so far is written, e.g. before the process exits
        /// </summary>
        public void Flush(TimeSpan timeout)
        {
            //Not disposed: after a timeout the consumer may still set it
            var written = new ManualResetEventSlim();
            pendingLogs.Add(new Log() { Written = written });
            written.Wait(timeout);
        }

        private void ConsumePendingLogs()
        {
            foreach (var log in pendingLogs.GetConsumingEnumerable())
            {
                if (log.Written != null)
                {
                    log.Written.Set();
                    continue;
                }

                var at = DateTime.UtcNow;
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

                try
                {
                    Logged?.Invoke(new LogEntry(at, log.LogLevel.ToString(), log.Content));
                }
                catch (Exception e)
                {
                    //Writing it here would raise Logged again
                    Console.Error.WriteLine($"A log listener failed: {e}");
                }
            }
        }

        private string GetTimestamp()
        {
            var now = DateTime.UtcNow;
            return $"{now.Day}/{now.Month}T{now.Hour}:{now.Minute}:{now.Second}Z - ";
        }

        private class Log
        {
            public LogLevel LogLevel { get; set; }
            public string Content { get; set; }
            //A flush marker rather than a log
            public ManualResetEventSlim Written { get; set; }
        }

        public record LogEntry(DateTime At, string Level, string Content);

        private enum LogLevel
        {
            Message,
            Error,
            UnexpectedError
        }
    }
}
