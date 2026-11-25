using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    /// <summary>
    /// Against in-memory SQLite databases that live only while the test holds a connection (the user's call, 2026-11-24).
    /// </summary>
    [TestClass]
    public class SqliteCoordinatorContextTests
    {
        private const string Symbol = "NMR/BTC";

        private SqliteConnection keepAlive;
        private DbContextOptions<SpreadBotDbContext> options;

        [TestInitialize]
        public void CreateDatabase()
        {
            //Shared cache: each context gets its own connection (the writer runs on its own thread), and the database lives while this one is open
            var connectionString = $"Data Source=spreadbot-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            keepAlive = new SqliteConnection(connectionString);
            keepAlive.Open();

            options = new DbContextOptionsBuilder<SpreadBotDbContext>().UseSqlite(connectionString).Options;
            using var db = Db();
            db.Database.Migrate();
        }

        [TestCleanup]
        public void DropDatabase() => keepAlive.Dispose();

        private SpreadBotDbContext Db() => new SpreadBotDbContext(options);

        private SqliteCoordinatorContext Context() => new SqliteCoordinatorContext(Db);

        private static SpreadConfiguration Configuration(BotStrategyType strategy = BotStrategyType.Spread) => new SpreadConfiguration
        {
            Strategy = strategy,
            BaseMarket = "BTC",
            AllocatedAmountOfBaseCurrency = 0.002m,
            MinimumNegotiatedAmount = 0.0005m,
            MinimumSpreadPercentage = 1,
            MinutesForLoss = 30
        };

        private static Bot Bot(BotContext context)
        {
            var exchange = new MockExchange();
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            return new Bot(repository, context, _ => { }, new BotStrategiesFactory(), TestLedger.Empty(repository));
        }

        private static BotContext Holding(SpreadConfiguration configuration = null) =>
            new BotContext(configuration ?? Configuration(), new MarketData { Symbol = Symbol, BidRate = 0.00009999m, AskRate = 0.00010101m, LastTradeRate = 0.0001m, PriceTick = MockExchange.Tick }, BotState.Bought, 0.25m)
            {
                Balance = 0.0004m,
                BoughtPrice = 0.0001m,
                BuyFilledAt = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc)
            };

        private static OrderData Sell(string clientOrderId, OrderStatus status = OrderStatus.OPEN, decimal filled = 0) => new OrderData
        {
            Id = "order-" + clientOrderId,
            ClientOrderId = clientOrderId,
            MarketSymbol = Symbol,
            Direction = OrderDirection.SELL,
            Type = OrderType.LIMIT,
            Quantity = 0.25m,
            Limit = 0.000101m,
            FillQuantity = filled,
            Status = status,
            CreatedAt = new DateTime(2026, 11, 24, 12, 5, 0, DateTimeKind.Utc)
        };

        [TestMethod]
        public void Migrations_MatchTheModel()
        {
            using var db = Db();

            Assert.AreEqual(0, db.Database.GetPendingMigrations().Count());
            Assert.IsFalse(db.Database.HasPendingModelChanges(), "the model changed without a migration: dotnet dotnet-ef migrations add <Name> --project SpreadBot --output-dir Infrastructure/Persistence/Migrations");
        }

        [TestMethod]
        public void ActiveBot_IsRestoredWithEverythingItNeeds()
        {
            var saved = Holding();
            saved.BotState = BotState.SellOrderActive;
            saved.CurrentOrderData = Sell("sell-1");
            var bot = Bot(saved);
            var context = Context();

            context.AddBot(bot);
            context.RecordOrder(bot, saved.CurrentOrderData);
            context.Save();

            var restored = Context().GetPreviousSessionContext().BotContexts.Single();

            Assert.AreEqual(saved.Guid, restored.Guid, "the same bot, so its row and orders stay its own");
            Assert.AreEqual(saved.spreadConfiguration, restored.spreadConfiguration);
            Assert.AreEqual(BotState.SellOrderActive, restored.BotState);
            Assert.AreEqual(0.0004m, restored.Balance);
            Assert.AreEqual(0.25m, restored.HeldAmount);
            Assert.AreEqual(0.0001m, restored.BoughtPrice);
            Assert.AreEqual(saved.BuyFilledAt, restored.BuyFilledAt);
            Assert.AreEqual(DateTimeKind.Utc, restored.BuyFilledAt.Value.Kind);
            Assert.AreEqual(Symbol, restored.LatestMarketData.Symbol);
            Assert.AreEqual(0.00010101m, restored.LatestMarketData.AskRate);
            Assert.AreEqual(MockExchange.Tick, restored.LatestMarketData.PriceTick);
            Assert.AreEqual("order-sell-1", restored.CurrentOrderData.Id);
            Assert.AreEqual("sell-1", restored.CurrentOrderData.ClientOrderId);
            Assert.AreEqual(OrderStatus.OPEN, restored.CurrentOrderData.Status);
            Assert.AreEqual(0.000101m, restored.CurrentOrderData.Limit);
        }

        [TestMethod]
        public void OrderWhosePlacementIsUnknown_IsRestoredAsSent()
        {
            var sentAt = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
            var saved = Holding();
            saved.CurrentOrderData = new OrderData { ClientOrderId = "sb-unknown", MarketSymbol = Symbol, Direction = OrderDirection.SELL, Status = OrderStatus.UNKNOWN, CreatedAt = sentAt };
            var bot = Bot(saved);
            var context = Context();

            context.AddBot(bot);
            context.RecordOrder(bot, saved.CurrentOrderData);
            context.Save();

            var order = Context().GetPreviousSessionContext().BotContexts.Single().CurrentOrderData;

            Assert.AreEqual(OrderStatus.UNKNOWN, order.Status);
            Assert.IsNull(order.Id);
            Assert.AreEqual("sb-unknown", order.ClientOrderId);
            Assert.AreEqual(sentAt, order.CreatedAt, "its lifetime counts from when it was sent");
            Assert.AreEqual(DateTimeKind.Utc, order.CreatedAt.Kind);
        }

        [TestMethod]
        public void CurrentOrderNeverRecorded_IsStoredWithTheBot()
        {
            var saved = Holding();
            saved.CurrentOrderData = Sell("sell-1", filled: 0.1m);
            var context = Context();

            context.AddBot(Bot(saved));
            context.Save();

            var order = Context().GetPreviousSessionContext().BotContexts.Single().CurrentOrderData;
            Assert.AreEqual(OrderStatus.OPEN, order.Status);
            Assert.AreEqual("order-sell-1", order.Id);
            Assert.AreEqual(0.1m, order.FillQuantity);
        }

        [TestMethod]
        public void CurrentOrderRecordedFirst_IsLinkedRatherThanStoredAgain()
        {
            var saved = Holding();
            saved.BotState = BotState.SellOrderActive;
            var bot = Bot(saved);
            var context = Context();
            context.AddBot(bot);

            saved.CurrentOrderData = Sell("sell-1");
            context.RecordOrder(bot, saved.CurrentOrderData);
            context.Save();

            using var db = Db();
            Assert.AreEqual(1, db.Orders.Count());
            Assert.AreEqual("sell-1", db.Bots.Include(b => b.CurrentOrder).Single().CurrentOrder.ClientOrderId);
        }

        [TestMethod]
        public void FailedBatch_LeavesNothingBehind_AndTheNextOneStoresItAgain()
        {
            var context = Context();
            // Hold the writer so the next writes form one batch
            var gate = new ManualResetEventSlim();
            context.Enqueue(_ => gate.Wait());

            Assert.IsTrue(SpinWait.SpinUntil(() => context.PendingWrites == 0, TimeSpan.FromSeconds(2)), "the writer holds the gate");
            var first = Holding(Configuration(BotStrategyType.PatientSpread));
            context.AddBot(Bot(first)); // saves its new configuration midway
            context.Enqueue(_ => throw new InvalidOperationException("the disk is full"));
            gate.Set();
            context.Flush();

            using (var db = Db())
            {
                Assert.AreEqual(0, db.Configurations.Count(), "the configuration saved midway went with the batch");
                Assert.AreEqual(0, db.Bots.Count());
            }

            var second = Holding(Configuration(BotStrategyType.PatientSpread));
            context.AddBot(Bot(second));
            context.Save();

            using var after = Db();
            Assert.AreEqual(1, after.Configurations.Count(), "stored again, not taken from a cache of the rolled-back batch");
            Assert.AreEqual(2, after.Bots.Count(), "the snapshot stores the first bot too");
        }

        [TestMethod]
        public void BotAddedAndGivenAnOrderInOneBatch_IsStoredWithIt()
        {
            var saved = Holding();
            saved.BotState = BotState.SellOrderActive;
            var bot = Bot(saved);
            var context = Context();
            var gate = new ManualResetEventSlim();
            context.Enqueue(_ => gate.Wait());

            Assert.IsTrue(SpinWait.SpinUntil(() => context.PendingWrites == 0, TimeSpan.FromSeconds(2)), "the writer holds the gate");
            context.AddBot(bot);
            saved.CurrentOrderData = Sell("sell-1");
            context.RecordOrder(bot, saved.CurrentOrderData);
            var saving = Task.Run(context.Save); // its snapshot and flush marker join the queue
            //At least: the debounced snapshot the order change requested may join them, in the same batch
            Assert.IsTrue(SpinWait.SpinUntil(() => context.PendingWrites >= 4, TimeSpan.FromSeconds(2)));
            gate.Set();
            Assert.IsTrue(saving.Wait(TimeSpan.FromSeconds(5)));

            Assert.AreEqual("sell-1", Context().GetPreviousSessionContext().BotContexts.Single().CurrentOrderData.ClientOrderId);
        }

        [TestMethod]
        public void BotRowNamingAnOrderThatIsntStored_IsRefused()
        {
            // The database keeps a bot's current order with it, so a restore never has to guess one
            var context = Context();
            context.AddBot(Bot(Holding()));
            context.Save();

            using var db = Db();
            db.Bots.Single().CurrentOrderRecordId = 12345;
            Assert.ThrowsExactly<DbUpdateException>(() => db.SaveChanges());
        }

        [TestMethod]
        public void CurrentOrderStateThatWasntRecorded_IsStoredWithTheBot()
        {
            // e.g. the batch that recorded it failed: the snapshot stores the state the bot holds
            var saved = Holding();
            saved.BotState = BotState.SellOrderActive;
            var bot = Bot(saved);
            var context = Context();
            context.AddBot(bot);
            context.RecordOrder(bot, Sell("sell-1"));
            context.Flush();

            saved.CurrentOrderData = Sell("sell-1", filled: 0.1m);
            context.Save();

            Assert.AreEqual(0.1m, Context().GetPreviousSessionContext().BotContexts.Single().CurrentOrderData.FillQuantity);
        }

        [TestMethod]
        public void AfterDispose_BotChangesWriteNothingAndDontThrow()
        {
            var saved = Holding();
            var bot = Bot(saved);
            var context = Context();
            context.AddBot(bot);
            context.Save();

            context.Dispose();
            saved.BotState = BotState.Buying;
            context.RecordOrder(bot, Sell("sell-late"));
            context.Save();
            context.RemoveBot(saved.Guid);

            using var db = Db();
            Assert.AreEqual(BotState.Bought, db.Bots.Single().State);
            Assert.AreEqual(0, db.Orders.Count());
        }

        [TestMethod]
        public void BotReaddedAfterASnapshotClosedIt_IsOpenAgain()
        {
            // A snapshot during a restore can close a restored bot before the restore adds it
            var saved = Holding();
            var first = Context();
            first.AddBot(Bot(saved));
            first.Save();

            var second = Context();
            second.Save();
            using (var db = Db())
                Assert.IsNotNull(db.Bots.Single().FinishedAt);

            second.AddBot(Bot(saved));
            second.Save();

            Assert.AreEqual(saved.Guid, Context().GetPreviousSessionContext().BotContexts.Single().Guid);
        }

        [TestMethod]
        public void RemovedBot_RecordsItsEndAndProfit_AndIsntRestored()
        {
            var saved = Holding();
            var bot = Bot(saved);
            var context = Context();
            context.AddBot(bot);
            saved.Balance = 0.0021m;

            Assert.IsTrue(context.RemoveBot(saved.Guid));
            context.Save();

            using var db = Db();
            var row = db.Bots.Single();
            Assert.IsNotNull(row.FinishedAt);
            Assert.AreEqual(0.0001m, row.Profit, "its balance minus the 0.002 allocated");
            Assert.IsNull(Context().GetPreviousSessionContext());
        }

        [TestMethod]
        public void PreviousSessionsBotThatIsntResumed_IsClosedByTheNextSave()
        {
            // As when TryToRestoreSession is off: the stored session must not come back in a later one
            var first = Context();
            first.AddBot(Bot(Holding()));
            first.AddDustForMarket("XYZ/BTC", 0.5m);
            first.Save();

            var second = Context();
            var resumed = Holding();
            second.AddBot(Bot(resumed));
            second.Save();

            var restored = Context().GetPreviousSessionContext();
            Assert.AreEqual(resumed.Guid, restored.BotContexts.Single().Guid);
            Assert.AreEqual(0, restored.DustPerMarket.Count, "the dust is this session's");
            using var db = Db();
            Assert.AreEqual(1, db.Bots.Count(b => b.FinishedAt != null && b.Profit == null), "closed without a profit, since it never finished");
        }

        [TestMethod]
        public void StoredBotSavedWhileFinishing_IsClosedWithTheProfitItHad()
        {
            var saved = Holding();
            saved.Balance = 0.0025m;
            var first = Context();
            first.AddBot(Bot(saved));
            first.Save();

            var second = Context();
            Assert.IsFalse(second.RemoveBot(saved.Guid), "it isn't allocated in this session");
            second.Save();

            using var db = Db();
            var row = db.Bots.Single();
            Assert.IsNotNull(row.FinishedAt);
            Assert.AreEqual(0.0005m, row.Profit);
        }

        [TestMethod]
        public void Dust_IsStoredAsTheSessionHoldsIt()
        {
            var context = Context();
            context.AddDustForMarket("XYZ/BTC", 0.5m);
            context.AddDustForMarket("XYZ/BTC", 0.25m);
            context.AddDustForMarket("ABC/BTC", 3m);
            context.RemoveDustForMarket("ABC/BTC");
            context.Save();

            var dust = Context().GetPreviousSessionContext().DustPerMarket;

            Assert.AreEqual(1, dust.Count);
            Assert.AreEqual(0.75m, dust["XYZ/BTC"]);
        }

        [TestMethod]
        public void Orders_AreAppendedOncePerState()
        {
            var saved = Holding();
            var bot = Bot(saved);
            var context = Context();
            context.AddBot(bot);

            context.RecordOrder(bot, Sell("sell-1"));
            context.RecordOrder(bot, Sell("sell-1"));
            context.RecordOrder(bot, Sell("sell-1", filled: 0.1m));
            // Applied as the current order and then booked: the same state twice
            context.RecordOrder(bot, Sell("sell-1", OrderStatus.CLOSED, filled: 0.25m));
            context.RecordOrder(bot, Sell("sell-1", OrderStatus.CLOSED, filled: 0.25m));
            context.Save();

            using var db = Db();
            var states = db.Orders.ToList();
            CollectionAssert.AreEquivalent(new[] { 0m, 0.1m, 0.25m }, states.Select(o => o.FillQuantity).ToArray());
            Assert.AreEqual(OrderStatus.CLOSED, states.Single(o => o.FillQuantity == 0.25m).Status);
            Assert.IsTrue(states.All(o => db.Entry(o).Property<Guid>("BotId").CurrentValue == saved.Guid));
        }

        [TestMethod]
        public void Configurations_AreStoredOncePerConfiguration()
        {
            var context = Context();
            context.AddBot(Bot(Holding(Configuration())));
            context.AddBot(Bot(Holding(Configuration())));
            context.AddBot(Bot(Holding(Configuration(BotStrategyType.CalmSpread))));
            context.Save();

            using var db = Db();
            Assert.AreEqual(2, db.Configurations.Count());
            Assert.AreEqual(3, db.Bots.Count());
        }

        [TestMethod]
        public void Snapshot_WaitsForABotToFinishItsChange()
        {
            var saved = Holding();
            var bot = Bot(saved);
            var context = Context();

            // Halfway through a change, as the bot would be while processing a message: its sell is placed but not yet its current order
            saved.Semaphore.Wait();
            Task saving;
            try
            {
                context.AddBot(bot);
                saved.BotState = BotState.SellOrderActive;

                saving = Task.Run(context.Save);
                Assert.IsFalse(saving.Wait(TimeSpan.FromMilliseconds(300)), "the snapshot must wait for the bot to finish its change");

                saved.CurrentOrderData = Sell("sell-1");
            }
            finally
            {
                saved.Semaphore.Release();
            }

            Assert.IsTrue(saving.Wait(TimeSpan.FromSeconds(2)));

            using var after = Db();
            var row = after.Bots.Single();
            Assert.AreEqual(BotState.SellOrderActive, row.State);
            Assert.AreEqual("sell-1", after.Bots.Include(b => b.CurrentOrder).Single().CurrentOrder.ClientOrderId);
        }

        [TestMethod]
        public void Snapshot_LeavesOutABotThatFinished()
        {
            var saved = Holding();
            var context = Context();
            context.AddBot(Bot(saved));
            saved.BotState = BotState.FinishedWork;

            context.Save();

            using var db = Db();
            Assert.AreEqual(BotState.Bought, db.Bots.Single().State, "RemoveBot records how it ended");
        }
    }
}
