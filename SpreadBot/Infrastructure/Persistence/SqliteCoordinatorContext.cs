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
        //Client order id -> the last state appended, so a state applied twice is stored once
        private readonly ConcurrentDictionary<string, string> recordedOrderStates = new ConcurrentDictionary<string, string>();

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

            var rows = db.Bots.AsNoTracking().Include(b => b.Configuration).Where(b => b.FinishedAt == null).ToList();
            var dust = db.Dust.AsNoTracking().ToDictionary(d => d.MarketSymbol, d => d.Amount);

            if (rows.Count == 0 && dust.Count == 0)
                return null;

            var botContexts = new List<BotContext>();
            foreach (var row in rows)
            {
                var currentOrder = LatestOrderState(db, row);
                if (currentOrder != null)
                    recordedOrderStates[currentOrder.ClientOrderId] = StateKey(currentOrder);

                botContexts.Add(ToBotContext(row, currentOrder));
            }

            return new PreviousSessionContext { BotContexts = botContexts, DustPerMarket = dust };
        }

        private static OrderData LatestOrderState(SpreadBotDbContext db, BotRow row)
        {
            if (row.CurrentOrderClientId == null)
                return null;

            return db.Orders.AsNoTracking()
                .Where(o => o.ClientOrderId == row.CurrentOrderClientId)
                .OrderByDescending(o => EF.Property<long>(o, "RecordId"))
                .FirstOrDefault();
        }

        private static BotContext ToBotContext(BotRow row, OrderData currentOrder)
        {
            var market = new MarketData { Symbol = row.MarketSymbol, BidRate = row.BidRate, AskRate = row.AskRate, LastTradeRate = row.LastTradeRate, PriceTick = row.PriceTick };

            //Each state is appended before the snapshot that names it, so this only happens if that write failed: the bot can't know
            //whether the order is live, which is what an unknown placement is; it's looked up by client order id and dropped only once
            //the exchange confirms it absent
            if (row.CurrentOrderClientId != null && currentOrder == null)
            {
                Logger.Instance.LogUnexpectedError($"Bot {row.Id}'s current order {row.CurrentOrderClientId} wasn't stored; restoring it as an order whose placement is unknown");
                currentOrder = new OrderData { ClientOrderId = row.CurrentOrderClientId, MarketSymbol = row.MarketSymbol, Status = OrderStatus.UNKNOWN, CreatedAt = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc) };
            }

            return new BotContext(row.Configuration, market, row.State, row.HeldAmount)
            {
                Guid = row.Id,
                Balance = row.Balance,
                BoughtPrice = row.BoughtPrice,
                BuyFilledAt = row.BuyFilledAt,
                CurrentOrderData = currentOrder
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

            //Right away rather than with the next snapshot: its orders reference it. It hasn't started, so nothing is changing it
            var row = ToRow(bot.botContext);
            var configuration = bot.botContext.spreadConfiguration;
            Enqueue(db => Upsert(db, row, configuration, allocatedAt: DateTime.UtcNow));
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

            var row = ToRow(bot.botContext);
            var configuration = bot.botContext.spreadConfiguration;
            Enqueue(db =>
            {
                Upsert(db, row, configuration, allocatedAt: finishedAt);
                var stored = db.Bots.Find(botId);
                stored.FinishedAt = finishedAt;
                stored.Profit = row.Balance - configuration.AllocatedAmountOfBaseCurrency;
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
            writes.Add((null, written));
            written.Wait();
        }

        private void Enqueue(Action<SpreadBotDbContext> write) => writes.Add((write, null));

        private void BotContext_ContextChanged(object sender, EventArgs e) => RequestSave();

        private void RequestSave()
        {
            //An enabled timer is about to save everything anyway
            if (!saveTimer.Enabled)
                saveTimer.Start();
        }

        internal void RecordOrder(Bot bot, OrderData order)
        {
            //Raised in the middle of the bot's work: failing here must not fail it
            try
            {
                if (order?.ClientOrderId == null)
                    return;

                var state = StateKey(order);
                if (recordedOrderStates.TryGetValue(order.ClientOrderId, out var recorded) && recorded == state)
                    return;

                //A closed order is booked once; dropping its entry keeps the map to the orders still in play
                if (order.Status == OrderStatus.CLOSED)
                    recordedOrderStates.TryRemove(order.ClientOrderId, out _);
                else
                    recordedOrderStates[order.ClientOrderId] = state;

                //A copy: the bot may hold the instance, and the writer stores it later
                var copy = JsonConvert.DeserializeObject<OrderData>(JsonConvert.SerializeObject(order));
                var botId = bot.Guid;
                var recordedAt = DateTime.UtcNow;
                Enqueue(db =>
                {
                    db.Orders.Add(copy);
                    db.Entry(copy).Property("BotId").CurrentValue = botId;
                    db.Entry(copy).Property("RecordedAt").CurrentValue = recordedAt;
                });
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
                    var rows = new List<(BotRow row, SpreadConfiguration configuration)>();
                    foreach (var bot in allocatedBots.Values)
                    {
                        //Under the bot's own semaphore, so a snapshot never catches a bot halfway through a change (e.g. SellOrderActive without its order)
                        bot.botContext.Semaphore.Wait();
                        try
                        {
                            //Finished while this waited: RemoveBot records it
                            if (bot.botContext.BotState != BotState.FinishedWork)
                                rows.Add((ToRow(bot.botContext), bot.botContext.spreadConfiguration));
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
                        foreach (var (row, configuration) in rows)
                            Upsert(db, row, configuration, allocatedAt: snapshotAt);

                        //The allocated bots are exactly the ones above: any other row still open is a previous session's bot this one didn't resume
                        var current = rows.Select(r => r.row.Id).ToHashSet();
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

        private static BotRow ToRow(BotContext context) => new BotRow
        {
            Id = context.Guid,
            MarketSymbol = context.LatestMarketData.Symbol,
            State = context.BotState,
            Balance = context.Balance,
            HeldAmount = context.HeldAmount,
            BoughtPrice = context.BoughtPrice,
            BuyFilledAt = context.BuyFilledAt,
            CurrentOrderClientId = context.CurrentOrderData?.ClientOrderId,
            BidRate = context.LatestMarketData.BidRate,
            AskRate = context.LatestMarketData.AskRate,
            LastTradeRate = context.LatestMarketData.LastTradeRate,
            PriceTick = context.LatestMarketData.PriceTick
        };

        private void Upsert(SpreadBotDbContext db, BotRow row, SpreadConfiguration configuration, DateTime allocatedAt)
        {
            var stored = db.Bots.Find(row.Id);
            if (stored == null)
            {
                row.ConfigurationId = ConfigurationId(db, configuration);
                row.AllocatedAt = allocatedAt;
                db.Bots.Add(row);
                return;
            }

            //A snapshot taken before the bot finished must not overwrite how it ended
            if (stored.FinishedAt != null)
                return;

            stored.State = row.State;
            stored.Balance = row.Balance;
            stored.HeldAmount = row.HeldAmount;
            stored.BoughtPrice = row.BoughtPrice;
            stored.BuyFilledAt = row.BuyFilledAt;
            stored.CurrentOrderClientId = row.CurrentOrderClientId;
            stored.BidRate = row.BidRate;
            stored.AskRate = row.AskRate;
            stored.LastTradeRate = row.LastTradeRate;
            stored.PriceTick = row.PriceTick;
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

            //A copy: the configuration is shared with the running bots and settings
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
                    foreach (var (write, _) in batch)
                        write?.Invoke(db);
                    db.SaveChanges();
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
            saveTimer.Dispose();
            writes.CompleteAdding();
            if (!writer.Wait(TimeSpan.FromSeconds(30)))
                Logger.Instance.LogError("Timed out writing the last changes to the database");
        }
    }
}
