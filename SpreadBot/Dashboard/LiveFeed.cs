using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;

namespace SpreadBot.Dashboard
{
    /// <summary>
    /// Fans server-sent events out to every open dashboard. Each connection gets a small bounded queue: a slow one loses its oldest
    /// events (the next state snapshot supersedes them) instead of holding up the others.
    /// </summary>
    internal class LiveFeed
    {
        private const int QueueLength = 256;
        private const int RecentActivityLength = 200;
        private const int RecentLogLength = 500;

        private readonly JsonSerializerOptions json;
        private readonly ConcurrentDictionary<Guid, Channel<string>> connections = new ConcurrentDictionary<Guid, Channel<string>>();
        private readonly LinkedList<ActivityView> recentActivity = new LinkedList<ActivityView>();
        private readonly LinkedList<LogView> recentLogs = new LinkedList<LogView>();

        public LiveFeed(JsonSerializerOptions json)
        {
            this.json = json;
        }

        public int Connections => connections.Count;

        public (Guid id, ChannelReader<string> reader) Connect()
        {
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueLength) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            var id = Guid.NewGuid();
            connections[id] = channel;
            return (id, channel.Reader);
        }

        public void Disconnect(Guid id)
        {
            if (connections.TryRemove(id, out var channel))
                channel.Writer.TryComplete();
        }

        public void Publish(string eventName, object data)
        {
            if (connections.IsEmpty)
                return;

            var message = $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, json)}\n\n";
            foreach (var channel in connections.Values)
                channel.Writer.TryWrite(message);
        }

        public void Activity(ActivityView activity)
        {
            lock (recentActivity)
            {
                recentActivity.AddFirst(activity);
                if (recentActivity.Count > RecentActivityLength)
                    recentActivity.RemoveLast();
            }
            Publish("activity", activity);
        }

        public void Log(LogView log)
        {
            lock (recentLogs)
            {
                recentLogs.AddFirst(log);
                if (recentLogs.Count > RecentLogLength)
                    recentLogs.RemoveLast();
            }
            Publish("log", log);
        }

        public IReadOnlyList<ActivityView> RecentActivity()
        {
            lock (recentActivity)
                return recentActivity.ToList();
        }

        public IReadOnlyList<LogView> RecentLogs()
        {
            lock (recentLogs)
                return recentLogs.ToList();
        }
    }
}
