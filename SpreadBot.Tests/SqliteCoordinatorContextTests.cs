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
        public void CurrentOrderThatWasntStored_IsRestoredAsAnExpiredUnknownOrder()
        {
            // Looking it up by client order id is the only safe way on: placing again could duplicate a live order
            var saved = Holding();
            saved.CurrentOrderData = Sell("sell-lost");
            var context = Context();

            context.AddBot(Bot(saved));
            context.Save();

            var order = Context().GetPreviousSessionContext().BotContexts.Single().CurrentOrderData;

            Assert.AreEqual(OrderStatus.UNKNOWN, order.Status);
            Assert.AreEqual("sell-lost", order.ClientOrderId);
            Assert.AreEqual(Symbol, order.MarketSymbol);
            Assert.IsTrue(DateTime.UtcNow - order.CreatedAt > TimeSpan.FromDays(1));
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
            // Booked without ever being the current order
            context.RecordOrder(bot, Sell("sell-1", OrderStatus.CLOSED, filled: 0.25m));
            context.Save();

            using var db = Db();
            var states = db.Orders.OrderBy(o => EF.Property<long>(o, "RecordId")).ToList();
            CollectionAssert.AreEqual(new[] { 0m, 0.1m, 0.25m }, states.Select(o => o.FillQuantity).ToArray());
            Assert.AreEqual(OrderStatus.CLOSED, states.Last().Status);
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
            try
            {
                context.AddBot(bot);
                saved.BotState = BotState.SellOrderActive;
                Thread.Sleep(1500); // past the 1 s save debounce
                context.Flush();

                using (var db = Db())
                    Assert.AreEqual(BotState.Bought, db.Bots.Single().State, "the snapshot must wait for the bot to finish its change");

                saved.CurrentOrderData = Sell("sell-1");
            }
            finally
            {
                saved.Semaphore.Release();
            }

            context.Save();

            using var after = Db();
            var row = after.Bots.Single();
            Assert.AreEqual(BotState.SellOrderActive, row.State);
            Assert.AreEqual("sell-1", row.CurrentOrderClientId);
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
