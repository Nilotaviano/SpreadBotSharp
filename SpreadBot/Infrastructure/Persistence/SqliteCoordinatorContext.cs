using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure.Persistence
{
    /// <summary>
    /// Keeps the allocated bots and dust in memory, as the Coordinator reads them, and writes them behind to SQLite on one writer
    /// thread: bot rows when a bot is added or removed, and a snapshot of every bot debounced on their changes, which also closes
    /// the rows of bots no longer allocated (a previous session's that weren't resumed) and syncs the dust; every order state a bot
    /// applies is appended to Orders as it happens.
    /// </summary>
    internal sealed class SqliteCoordinatorContext : ICoordinatorContext, IDisposable
    {
        private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);

        private readonly Func<SpreadBotDbContext> createDb;
        private readonly ConcurrentDictionary<Guid, Bot> allocatedBots = new ConcurrentDictionary<Guid, Bot>();
        private readonly ConcurrentDictionary<string, decimal> dustPerMarket = new ConcurrentDictionary<string, decimal>();
        //Client order id -> the bot and the last state appended, so a state applied twice is stored once; a bot's entries go when it does
        private readonly ConcurrentDictionary<string, (Guid bot, string state)> recordedOrderStates = new ConcurrentDictionary<string, (Guid, string)>();
        //Guards disposed with every enqueue and timer start, so nothing races Dispose into a completed queue or a disposed timer
        private readonly object lifecycle = new object();
        private bool disposed;

        //A null write is a flush marker: its event is set once everything queued before it is written (or failed)
        private readonly BlockingCollection<(Action<SpreadBotDbContext> write, ManualResetEventSlim written)> writes = new BlockingCollection<(Action<SpreadBotDbContext>, ManualResetEventSlim)>();
        private readonly Task writer;
        //Writer thread only
        private Dictionary<SpreadConfiguration, int> configurationIds;

        private readonly System.Timers.Timer saveTimer;
        private readonly object snapshotLock = new object();

        internal SqliteCoordinatorContext(Func<SpreadBotDbContext> createDb)
        {
            this.createDb = createDb;

            saveTimer = new System.Timers.Timer(SaveDebounce.TotalMilliseconds) { AutoReset = false };
            saveTimer.Elapsed += (sender, args) => WriteSnapshot();

            writer = Task.Factory.StartNew(Write, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>
        /// Opens the database at <paramref name="path"/>, creating it or applying pending migrations first. An existing file is
        /// copied aside before it's migrated.
        /// </summary>
        public static SqliteCoordinatorContext Open(string path)
        {
            var options = new DbContextOptionsBuilder<SpreadBotDbContext>().UseSqlite($"Data Source={path}").Options;
            var existed = File.Exists(path);

            using (var db = new SpreadBotDbContext(options))
            {
                var pending = db.Database.GetPendingMigrations().ToList();
                if (pending.Count > 0)
                {
                    if (existed)
                    {
                        var backup = $"{path}.before-{pending[0]}.bak";
                        File.Copy(path, backup, overwrite: true);
                        Logger.Instance.LogMessage($"Backed up {path} to {backup} before migrating it");
                    }

                    db.Database.Migrate();
                    Logger.Instance.LogMessage($"Applied {string.Join(", ", pending)} to {path}");
                }
            }

            return new SqliteCoordinatorContext(() => new SpreadBotDbContext(options));
        }

        public PreviousSessionContext GetPreviousSessionContext()
        {
            using var db = createDb();

            var rows = db.Bots.AsNoTracking()
                .Include(b => b.Configuration)
                .Include(b => b.CurrentOrder)
                .Where(b => b.FinishedAt == null)
                .ToList();
            var dust = db.Dust.AsNoTracking().ToDictionary(d => d.MarketSymbol, d => d.Amount);

            if (rows.Count == 0 && dust.Count == 0)
                return null;

            foreach (var row in rows.Where(r => r.CurrentOrder != null))
                recordedOrderStates[row.CurrentOrder.ClientOrderId] = (row.Id, StateKey(row.CurrentOrder));

            return new PreviousSessionContext { BotContexts = rows.Select(ToBotContext).ToList(), DustPerMarket = dust };
        }

        private static BotContext ToBotContext(BotRow row)
        {
            var market = new MarketData { Symbol = row.MarketSymbol, BidRate = row.BidRate, AskRate = row.AskRate, LastTradeRate = row.LastTradeRate, PriceTick = row.PriceTick };

            return new BotContext(row.Configuration, market, row.State, row.HeldAmount)
            {
                Guid = row.Id,
                Balance = row.Balance,
                BoughtPrice = row.BoughtPrice,
                BuyFilledAt = row.BuyFilledAt,
                CurrentOrderData = row.CurrentOrder
            };
        }

        public void AddDustForMarket(string marketSymbol, decimal dust)
        {
            dustPerMarket.AddOrUpdate(marketSymbol, dust, (_, existing) => existing + dust);
            RequestSave();
        }

        public void AddDustForMarkets(Dictionary<string, decimal> dustPerMarket)
        {
            if (dustPerMarket == null)
                return;

            foreach (var dust in dustPerMarket)
                this.dustPerMarket.AddOrUpdate(dust.Key, dust.Value, (_, existing) => existing + dust.Value);

            RequestSave();
        }

        public decimal RemoveDustForMarket(string marketSymbol)
        {
            dustPerMarket.TryRemove(marketSymbol, out var dust);
            RequestSave();
            return dust;
        }

        public IEnumerable<KeyValuePair<string, decimal>> GetDustPerMarket() => dustPerMarket.ToArray();

        public int GetBotCount() => allocatedBots.Count;

        public IEnumerable<Bot> GetBots() => allocatedBots.Values;

        public void AddBot(Bot bot)
        {
            allocatedBots[bot.Guid] = bot;
            bot.botContext.ContextChanged += BotContext_ContextChanged;
            bot.OrderUpdated += RecordOrder;

            //Right away rather than with the next snapshot: its orders reference it. It hasn't started, so nothing is changing it.
            //Allocated, it's open, even if a snapshot taken while it was being restored closed it as a bot this session didn't resume
            var snapshot = Snapshot(bot.botContext);
            var allocatedAt = DateTime.UtcNow;
            Enqueue(db => Upsert(db, snapshot, allocatedAt, reopen: true));
        }

        /// <summary>
        /// The bot is gone for good: its row records when it finished and its profit. A bot of the previous session that isn't
        /// allocated in this one (e.g. it was saved while finishing) is closed from its stored values.
        /// </summary>
        public bool RemoveBot(Guid botId)
        {
            var finishedAt = DateTime.UtcNow;

            if (!allocatedBots.TryRemove(botId, out var bot))
            {
                Enqueue(db => CloseStoredBot(db, botId, finishedAt));
                return false;
            }

            bot.botContext.ContextChanged -= BotContext_ContextChanged;
            bot.OrderUpdated -= RecordOrder;
            foreach (var order in recordedOrderStates.Where(entry => entry.Value.bot == botId).Select(entry => entry.Key).ToList())
                recordedOrderStates.TryRemove(order, out _);

            var snapshot = Snapshot(bot.botContext);
            Enqueue(db =>
            {
                Upsert(db, snapshot, allocatedAt: finishedAt, reopen: false);
                var stored = db.Bots.Find(botId);
                stored.FinishedAt = finishedAt;
                stored.Profit = snapshot.Row.Balance - snapshot.Configuration.AllocatedAmountOfBaseCurrency;
            });
            RequestSave();

            return true;
        }

        public void Save()
        {
            saveTimer.Stop();
            WriteSnapshot();
            Flush();
        }

        /// <summary>
        /// Waits until everything queued so far is written.
        /// </summary>
        internal void Flush()
        {
            //Not disposed: the writer may still set it if a caller stopped waiting
            var written = new ManualResetEventSlim();
            lock (lifecycle)
            {
                if (disposed)
                    return;
                writes.Add((null, written));
            }
            written.Wait();
        }

        internal int PendingWrites => writes.Count;

        internal void Enqueue(Action<SpreadBotDbContext> write)
        {
            //Bots can still change while the process exits; nothing is written after the context is disposed
            lock (lifecycle)
                if (!disposed)
                    writes.Add((write, null));
        }

        private void BotContext_ContextChanged(object sender, EventArgs e) => RequestSave();

        private void RequestSave()
        {
            //An enabled timer is about to save everything anyway
            lock (lifecycle)
                if (!disposed && !saveTimer.Enabled)
                    saveTimer.Start();
        }

        internal void RecordOrder(Bot bot, OrderData order)
        {
            //Raised in the middle of the bot's work: failing here must not fail it
            try
            {
                //Removed while this was raised; its last states went with its final write
                if (order?.ClientOrderId == null || !allocatedBots.ContainsKey(bot.Guid))
                    return;

                var state = StateKey(order);
                if (recordedOrderStates.TryGetValue(order.ClientOrderId, out var recorded) && recorded.state == state)
                    return;

                recordedOrderStates[order.ClientOrderId] = (bot.Guid, state);

                //A copy: the bot may hold the instance, and the writer stores it later
                var copy = Copy(order);
                var botId = bot.Guid;
                var recordedAt = DateTime.UtcNow;
                Enqueue(db => AddOrder(db, copy, botId, recordedAt));
            }
            catch (Exception e)
            {
                Logger.Instance.LogUnexpectedError($"Could not record order {order?.ClientOrderId}: {e}");
            }
        }

        private static string StateKey(OrderData order) =>
            order == null ? null : $"{order.Id}|{order.Status}|{order.Quantity}|{order.Limit}|{order.FillQuantity}|{order.Proceeds}|{order.Commission}|{order.CoinCommission}";

        private void WriteSnapshot()
        {
            //One at a time, so an older snapshot is never queued after a newer one
            lock (snapshotLock)
            {
                try
                {
                    var snapshots = new List<BotSnapshot>();
                    foreach (var bot in allocatedBots.Values)
                    {
                        //Under the bot's own semaphore, so a snapshot never catches a bot halfway through a change (e.g. SellOrderActive without its order)
                        bot.botContext.Semaphore.Wait();
                        try
                        {
                            //Finished while this waited: RemoveBot records it
                            if (bot.botContext.BotState != BotState.FinishedWork)
                                snapshots.Add(Snapshot(bot.botContext));
                        }
                        finally
                        {
                            bot.botContext.Semaphore.Release();
                        }
                    }

                    var dust = dustPerMarket.ToDictionary(d => d.Key, d => d.Value);
                    var snapshotAt = DateTime.UtcNow;

                    Enqueue(db =>
                    {
                        foreach (var snapshot in snapshots)
                            Upsert(db, snapshot, allocatedAt: snapshotAt, reopen: false);

                        //The allocated bots are exactly the ones above: any other row still open is a previous session's bot this one didn't resume
                        var current = snapshots.Select(snapshot => snapshot.Row.Id).ToHashSet();
                        foreach (var stale in db.Bots.Where(b => b.FinishedAt == null).ToList().Where(b => !current.Contains(b.Id) && !allocatedBots.ContainsKey(b.Id)))
                            stale.FinishedAt = snapshotAt;

                        foreach (var stored in db.Dust.ToList())
                        {
                            if (!dust.TryGetValue(stored.MarketSymbol, out var amount))
                                db.Dust.Remove(stored);
                            else
                                stored.Amount = amount;
                        }
                        foreach (var added in dust.Where(d => db.Dust.Local.All(stored => stored.MarketSymbol != d.Key)))
                            db.Dust.Add(new DustRow { MarketSymbol = added.Key, Amount = added.Value });
                    });
                }
                catch (Exception e)
                {
                    Logger.Instance.LogError($"Could not snapshot the bots: {e}");
                }
            }
        }

        //What's written about a bot, taken on the caller's thread: the writer runs later, while the bot keeps changing
        private sealed record BotSnapshot(BotRow Row, SpreadConfiguration Configuration, OrderData CurrentOrder);

        private static BotSnapshot Snapshot(BotContext context) =>
            new BotSnapshot(ToRow(context), context.spreadConfiguration, Copy(context.CurrentOrderData));

        private static OrderData Copy(OrderData order) =>
            order == null ? null : JsonConvert.DeserializeObject<OrderData>(JsonConvert.SerializeObject(order));

        private static BotRow ToRow(BotContext context) => new BotRow
        {
            Id = context.Guid,
            MarketSymbol = context.LatestMarketData.Symbol,
            State = context.BotState,
            Balance = context.Balance,
            HeldAmount = context.HeldAmount,
            BoughtPrice = context.BoughtPrice,
            BuyFilledAt = context.BuyFilledAt,
            BidRate = context.LatestMarketData.BidRate,
            AskRate = context.LatestMarketData.AskRate,
            LastTradeRate = context.LatestMarketData.LastTradeRate,
            PriceTick = context.LatestMarketData.PriceTick
        };

        private void Upsert(SpreadBotDbContext db, BotSnapshot snapshot, DateTime allocatedAt, bool reopen)
        {
            var row = snapshot.Row;
            var stored = db.Bots.Find(row.Id);
            if (stored == null)
            {
                //Before anything else is added: a new configuration is saved at once
                row.ConfigurationId = ConfigurationId(db, snapshot.Configuration);
                row.AllocatedAt = allocatedAt;
                db.Bots.Add(row);
                StoreCurrentOrder(db, row, snapshot, allocatedAt);
                return;
            }

            if (reopen)
            {
                stored.FinishedAt = null;
                stored.Profit = null;
            }

            //A snapshot taken before the bot finished must not overwrite how it ended
            if (stored.FinishedAt != null)
                return;

            StoreCurrentOrder(db, stored, snapshot, allocatedAt);

            stored.State = row.State;
            stored.Balance = row.Balance;
            stored.HeldAmount = row.HeldAmount;
            stored.BoughtPrice = row.BoughtPrice;
            stored.BuyFilledAt = row.BuyFilledAt;
            stored.BidRate = row.BidRate;
            stored.AskRate = row.AskRate;
            stored.LastTradeRate = row.LastTradeRate;
            stored.PriceTick = row.PriceTick;
        }

        /// <summary>
        /// Points the bot's row at a stored state equal to the order it holds, appending one if none is: a restore gets exactly that state.
        /// </summary>
        private static void StoreCurrentOrder(SpreadBotDbContext db, BotRow stored, BotSnapshot snapshot, DateTime recordedAt)
        {
            var order = snapshot.CurrentOrder;
            if (order == null)
            {
                stored.CurrentOrderRecordId = null;
                stored.CurrentOrder = null;
                return;
            }

            //The order references the bot and the bot its order, and EF can't insert two new rows that reference each other:
            //a bot added in this batch (here or by an earlier write) goes in first, within the batch's transaction
            if (db.Entry(stored).State == EntityState.Added)
                db.SaveChanges();

            var state = StateKey(order);
            //Any equal state will do: their content is the same. Recording usually stored it already
            var equal = db.Orders.Local.FirstOrDefault(o => o.ClientOrderId == order.ClientOrderId && StateKey(o) == state)
                ?? db.Orders.Where(o => o.ClientOrderId == order.ClientOrderId).AsEnumerable().FirstOrDefault(o => StateKey(o) == state);

            if (equal == null)
            {
                equal = order;
                AddOrder(db, order, snapshot.Row.Id, recordedAt);
            }

            stored.CurrentOrder = equal;
        }

        private static void AddOrder(SpreadBotDbContext db, OrderData order, Guid botId, DateTime recordedAt)
        {
            db.Orders.Add(order);
            db.Entry(order).Property("BotId").CurrentValue = botId;
            db.Entry(order).Property("RecordedAt").CurrentValue = recordedAt;
        }

        private static void CloseStoredBot(SpreadBotDbContext db, Guid botId, DateTime finishedAt)
        {
            var stored = db.Bots.Include(b => b.Configuration).SingleOrDefault(b => b.Id == botId);
            //A snapshot may have closed it already, as a bot this session didn't resume, but without a profit
            if (stored == null || stored.Profit != null)
                return;

            stored.FinishedAt ??= finishedAt;
            stored.Profit = stored.Balance - stored.Configuration.AllocatedAmountOfBaseCurrency;
        }

        private int ConfigurationId(SpreadBotDbContext db, SpreadConfiguration configuration)
        {
            configurationIds ??= db.Configurations.AsNoTracking()
                .Select(c => new { Configuration = c, Id = EF.Property<int>(c, "Id") })
                .ToList()
                .ToDictionary(c => c.Configuration, c => c.Id);

            if (configurationIds.TryGetValue(configuration, out var id))
                return id;

            //A copy: the configuration is shared with the running bots and settings. Saved now for its id; the batch's transaction still holds it
            var stored = JsonConvert.DeserializeObject<SpreadConfiguration>(JsonConvert.SerializeObject(configuration));
            db.Configurations.Add(stored);
            db.SaveChanges();

            id = db.Entry(stored).Property<int>("Id").CurrentValue;
            configurationIds[configuration] = id;
            return id;
        }

        private void Write()
        {
            foreach (var first in writes.GetConsumingEnumerable())
            {
                var batch = new List<(Action<SpreadBotDbContext> write, ManualResetEventSlim written)> { first };
                while (writes.TryTake(out var next))
                    batch.Add(next);

                try
                {
                    using var db = createDb();
                    //One transaction, though some writes save midway (a new configuration, a new bot before its order): a batch lands whole or not at all
                    using var transaction = db.Database.BeginTransaction();
                    foreach (var (write, _) in batch)
                        write?.Invoke(db);
                    db.SaveChanges();
                    transaction.Commit();
                }
                catch (Exception e)
                {
                    //The next snapshot rewrites the bots and the dust; order states in this batch are lost
                    Logger.Instance.LogUnexpectedError($"Could not write {batch.Count} changes to the database: {e}");
                    configurationIds = null;
                }
                finally
                {
                    foreach (var (_, written) in batch)
                        written?.Set();
                }
            }
        }

        public void Dispose()
        {
            //A snapshot under way gets queued first; one stuck on a bot's semaphore doesn't hold up the exit
            var snapshotDone = Monitor.TryEnter(snapshotLock, TimeSpan.FromSeconds(5));
            if (!snapshotDone)
                Logger.Instance.LogError("A snapshot was still waiting for a bot when the database closed; the bots' last changes since the previous one aren't stored");
            try
            {
                lock (lifecycle)
                {
                    if (disposed)
                        return;
                    disposed = true;
                    saveTimer.Dispose();
                    writes.CompleteAdding();
                }
            }
            finally
            {
                if (snapshotDone)
                    Monitor.Exit(snapshotLock);
            }

            if (!writer.Wait(TimeSpan.FromSeconds(30)))
                Logger.Instance.LogError("Timed out writing the last changes to the database");
        }
    }
}
