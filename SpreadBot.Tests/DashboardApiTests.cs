using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Dashboard;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Persistence;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    /// <summary>
    /// The dashboard's API, in process (TestServer) over an in-memory SQLite database (the user's call, 2026-11-24).
    /// </summary>
    [TestClass]
    public class DashboardApiTests
    {
        private const string Token = "test-token";
        private const string Symbol = "NMR/BTC";
        private const string QuietSymbol = "ABC/BTC";

        private SqliteConnection keepAlive;
        private SqliteCoordinatorContext context;
        private MockExchange exchange;
        private DataRepository repository;
        private Coordinator coordinator;
        private DashboardHost host;
        private HttpClient client;

        private static SpreadConfiguration Configuration() => new SpreadConfiguration
        {
            BaseMarket = "BTC",
            AllocatedAmountOfBaseCurrency = 0.003m,
            MinimumNegotiatedAmount = 0.001m,
            MinimumSpreadPercentage = 1,
            MinimumQuoteVolume = 150,
            MaxPercentChangeFromPreviousDay = 40,
            MinimumPrice = 0.000008m
        };

        private async Task Start(string publicUrl = null)
        {
            var connectionString = $"Data Source=dashboard-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            keepAlive = new SqliteConnection(connectionString);
            keepAlive.Open();
            var options = new DbContextOptionsBuilder<SpreadBotDbContext>().UseSqlite(connectionString).Options;
            using (var db = new SpreadBotDbContext(options))
                db.Database.Migrate();
            context = new SqliteCoordinatorContext(() => new SpreadBotDbContext(options));

            exchange = new MockExchange(new BalanceData { CurrencyAbbreviation = "BTC", Total = 1, Amount = 1 });
            exchange.WithMarkets(Symbol, QuietSymbol);
            exchange.WithTickers(
                MockExchange.Ticker(Symbol, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 200, percentChange: 5),
                MockExchange.Ticker(QuietSymbol, ask: 0.00010101m, bid: 0.00009999m, last: 0.0001m, quoteVolume: 10, percentChange: 5));
            exchange.Mock.Setup(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>()))
                .ReturnsAsync(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.0001m, Quantity = 29 });
            repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var settings = new AppSettings { ExchangeId = "binance", UseSandbox = true, MaxNumberOfBots = 3, ResyncIntervalMs = 600000, SpreadConfigurations = new[] { Configuration() } };
            coordinator = new Coordinator(settings, repository, context);
            coordinator.Start();

            var keys = WebPush.VapidHelper.GenerateVapidKeys();
            var secrets = new DashboardSecrets { AccessToken = Token, VapidPublicKey = keys.PublicKey, VapidPrivateKey = keys.PrivateKey };
            host = DashboardHost.Create(new DashboardSettings { Enabled = true, PublicUrl = publicUrl }, secrets, settings, repository, coordinator, context, context.Database, web => web.UseTestServer());
            await host.StartAsync();
            client = host.App.GetTestClient();
        }

        [TestCleanup]
        public async Task Stop()
        {
            if (host != null)
                await host.DisposeAsync();
            context?.Dispose();
            keepAlive?.Dispose();
        }

        private HttpRequestMessage Request(HttpMethod method, string path, object body = null, string token = Token)
        {
            var request = new HttpRequestMessage(method, path);
            if (token != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null)
                request.Content = JsonContent.Create(body);
            return request;
        }

        private async Task<JsonElement> Get(string path)
        {
            var response = await client.SendAsync(Request(HttpMethod.Get, path));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        private static void WaitUntil(Func<bool> condition, string description)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds > 3000)
                    Assert.Fail($"Timed out waiting until {description}");
                Thread.Sleep(10);
            }
        }

        [TestMethod]
        public async Task Api_NeedsTheToken_ThePageDoesnt()
        {
            await Start();

            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Get, "/api/state", token: null))).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(HttpMethod.Get, "/api/state", token: "wrong"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Get, "/api/state"))).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);

            var page = await client.GetAsync("/");
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "default-src 'self'");
        }

        [TestMethod]
        public async Task EveryApiRoute_NeedsTheToken_AndDoesNothingWithout()
        {
            await Start(publicUrl: "https://box.example.ts.net");
            var routes = new (HttpMethod method, string path, object body)[]
            {
                (HttpMethod.Get, "/api/state", null), (HttpMethod.Get, "/api/markets", null), (HttpMethod.Get, "/api/history", null),
                (HttpMethod.Get, "/api/orders", null), (HttpMethod.Get, "/api/activity", null), (HttpMethod.Get, "/api/logs", null),
                (HttpMethod.Get, "/api/stream", null), (HttpMethod.Get, "/api/push", null), (HttpMethod.Get, "/API/state", null),
                (HttpMethod.Post, "/api/controls/allocation", new { paused = true }),
                (HttpMethod.Post, "/api/controls/resync", null),
                (HttpMethod.Post, "/api/controls/halt", new { confirm = "HALT" }),
                (HttpMethod.Post, $"/api/bots/{Guid.NewGuid()}/finish", new { confirm = Symbol }),
                (HttpMethod.Post, "/api/push/subscriptions", new { endpoint = "https://fcm.googleapis.com/fcm/send/abc", keys = new { p256dh = "key", auth = "auth" } }),
                (HttpMethod.Post, "/api/push/subscriptions/lookup", new { endpoint = "https://fcm.googleapis.com/fcm/send/abc" }),
                (HttpMethod.Post, "/api/push/subscriptions/remove", new { endpoint = "https://fcm.googleapis.com/fcm/send/abc" }),
                (HttpMethod.Post, "/api/push/test", null),
            };

            foreach (var (method, path, body) in routes)
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Request(method, path, body, token: null))).StatusCode, $"{method} {path}");

            Assert.IsFalse(coordinator.IsAllocationPaused);
            Assert.IsFalse(coordinator.IsHalted);
            using var db = context.Database();
            Assert.AreEqual(0, db.PushSubscriptions.Count());
        }

        [TestMethod]
        public void NonLoopbackAddress_IsRefused()
        {
            var keys = WebPush.VapidHelper.GenerateVapidKeys();
            var secrets = new DashboardSecrets { AccessToken = Token, VapidPublicKey = keys.PublicKey, VapidPrivateKey = keys.PrivateKey };

            Assert.ThrowsExactly<InvalidOperationException>(() => DashboardHost.Create(new DashboardSettings { Enabled = true, Url = "http://0.0.0.0:5080" }, secrets, new AppSettings(), null, null, null, null));
            Assert.ThrowsExactly<InvalidOperationException>(() => DashboardHost.Create(new DashboardSettings { Enabled = true, Url = "http://192.168.1.10:5080" }, secrets, new AppSettings(), null, null, null, null));
        }

        [TestMethod]
        public async Task State_ShowsTheBotsMoneyAndHealth()
        {
            await Start();
            WaitUntil(() => context.GetBots().Any(b => b.botContext.BotState == BotState.BuyOrderActive), "the bot's buy rests");

            var state = await Get("/api/state");

            Assert.AreEqual("binance", state.GetProperty("exchange").GetString());
            Assert.IsTrue(state.GetProperty("sandbox").GetBoolean());
            var bot = state.GetProperty("bots").EnumerateArray().Single();
            Assert.AreEqual(Symbol, bot.GetProperty("market").GetString());
            Assert.AreEqual("BuyOrderActive", bot.GetProperty("state").GetString());
            Assert.AreEqual("buy-1", bot.GetProperty("order").GetProperty("id").GetString());
            var btc = state.GetProperty("baseMarkets").EnumerateArray().Single();
            Assert.AreEqual("BTC", btc.GetProperty("currency").GetString());
            Assert.AreEqual(1, btc.GetProperty("bots").GetInt32());
            Assert.IsNotNull(state.GetProperty("streams").GetProperty("lastMarketUpdate").GetString());
        }

        [TestMethod]
        public async Task Markets_SayWhyAMarketHasNoBot()
        {
            await Start();

            var markets = (await Get("/api/markets")).EnumerateArray().ToDictionary(m => m.GetProperty("symbol").GetString());

            Assert.AreEqual(JsonValueKind.Null, markets[Symbol].GetProperty("verdicts")[0].GetProperty("rejection").ValueKind);
            StringAssert.StartsWith(markets[QuietSymbol].GetProperty("verdicts")[0].GetProperty("rejection").GetString(), "volume ");
        }

        [TestMethod]
        public async Task Controls_NeedTheirConfirmation()
        {
            await Start();
            WaitUntil(() => context.GetBots().Any(), "a bot is allocated");
            var bot = context.GetBots().Single();

            Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, "/api/controls/allocation", new { paused = true }))).StatusCode);
            Assert.IsTrue(coordinator.IsAllocationPaused);

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Post, $"/api/bots/{bot.Guid}/finish", new { confirm = "ETH/BTC" }))).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Post, $"/api/bots/{Guid.NewGuid()}/finish", new { confirm = Symbol }))).StatusCode);

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Post, "/api/controls/halt", new { confirm = "halt please" }))).StatusCode);
            Assert.IsFalse(coordinator.IsHalted);
            Assert.AreEqual(HttpStatusCode.Accepted, (await client.SendAsync(Request(HttpMethod.Post, "/api/controls/halt", new { confirm = "HALT" }))).StatusCode);
            Assert.IsTrue(coordinator.IsHalted);
            Assert.AreEqual("Halted from the dashboard", coordinator.HaltReason);
        }

        [TestMethod]
        public async Task Push_IsOffWithoutAPublicAddress()
        {
            await Start();

            Assert.IsFalse((await Get("/api/push")).GetProperty("available").GetBoolean());
            var subscribe = await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", new { endpoint = "https://fcm.googleapis.com/fcm/send/abc", keys = new { p256dh = "key", auth = "auth" } }));
            Assert.AreEqual(HttpStatusCode.BadRequest, subscribe.StatusCode);
        }

        [TestMethod]
        public async Task Push_StoresEachBrowsersChoice()
        {
            await Start(publicUrl: "https://box.example.ts.net");
            var push = await Get("/api/push");
            Assert.IsTrue(push.GetProperty("available").GetBoolean());
            Assert.IsFalse(string.IsNullOrEmpty(push.GetProperty("publicKey").GetString()));

            const string endpoint = "https://fcm.googleapis.com/fcm/send/abc";
            var subscription = new { endpoint, keys = new { p256dh = "key", auth = "auth" }, categories = new[] { "halt", "fills", "nonsense" } };
            Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", subscription))).StatusCode);

            var lookup = await (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions/lookup", new { endpoint }))).Content.ReadFromJsonAsync<JsonElement>();
            CollectionAssert.AreEqual(new[] { "halt", "fills" }, lookup.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).ToArray());

            // The bot POSTs to the endpoint: anything but a browser's push service is refused
            foreach (var other in new[] { "https://evil.example/hook", "http://fcm.googleapis.com/fcm/send/abc", "https://fcm.googleapis.com.evil.example/x", "https://127.0.0.1/x", "https://fcm.googleapis.com:8443/x", "https://user@fcm.googleapis.com/x" })
                Assert.AreEqual(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", new { endpoint = other, keys = new { p256dh = "key", auth = "auth" } }))).StatusCode, other);

            Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions/remove", new { endpoint }))).StatusCode);
            using var db = context.Database();
            Assert.AreEqual(0, db.PushSubscriptions.Count());
        }

        [TestMethod]
        public async Task Push_AcceptsAtMostTwentyDevices()
        {
            await Start(publicUrl: "https://box.example.ts.net");

            for (var i = 0; i < 20; i++)
                Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", new { endpoint = $"https://fcm.googleapis.com/fcm/send/{i}", keys = new { p256dh = "key", auth = "auth" } }))).StatusCode);

            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", new { endpoint = "https://fcm.googleapis.com/fcm/send/21", keys = new { p256dh = "key", auth = "auth" } }))).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, "/api/push/subscriptions", new { endpoint = "https://fcm.googleapis.com/fcm/send/3", keys = new { p256dh = "key", auth = "auth" }, categories = new[] { "halt" } }))).StatusCode, "a known device can still change its choice");
        }

        [TestMethod]
        public async Task History_ShowsABotThatFinishedAfterItWasRead()
        {
            await Start();
            coordinator.SetAllocationPaused(true);
            WaitUntil(() => context.GetBots().Any(), "a bot is allocated");
            Assert.AreEqual(0, (await Get("/api/history")).GetProperty("finishedBots").GetArrayLength(), "read, and cached");

            var bot = context.GetBots().Single();
            context.RemoveBot(bot.Guid);
            context.Save();
            context.Flush();
            host.Monitor.Poll();

            Assert.AreEqual(1, (await Get("/api/history")).GetProperty("finishedBots").GetArrayLength(), "the finish cleared the cache");
        }

        [TestMethod]
        public async Task Activity_ReportsABotWhoseFinishWasWrittenLate()
        {
            // Stamped minutes before its row was written (the writer's queue, SQLite's busy waits): the cursor has moved past it
            await Start();
            coordinator.SetAllocationPaused(true);
            WaitUntil(() => context.GetBots().Any(), "a bot is allocated, storing its configuration");
            context.Flush();
            host.Monitor.Poll();
            using (var db = context.Database())
            {
                var configuration = db.Configurations.First();
                db.Bots.Add(new BotRow { Id = Guid.NewGuid(), ConfigurationId = db.Entry(configuration).Property<int>("Id").CurrentValue, MarketSymbol = "LATE/BTC", State = BotState.FinishedWork, AllocatedAt = DateTime.UtcNow.AddMinutes(-20), FinishedAt = DateTime.UtcNow.AddMinutes(-5), Profit = 0.0001m });
                db.SaveChanges();
            }

            host.Monitor.Poll();
            host.Monitor.Poll();

            Assert.AreEqual(1, (await Get("/api/activity")).EnumerateArray().Count(a => a.GetProperty("kind").GetString() == "finished" && a.GetProperty("market").GetString() == "LATE/BTC"));
        }

        [TestMethod]
        public async Task Activity_DoesNotAnnounceAgainWhatFinishedBeforeARestart()
        {
            await Start();
            coordinator.SetAllocationPaused(true);
            WaitUntil(() => context.GetBots().Any(), "a bot is allocated");
            var bot = context.GetBots().Single();
            context.RemoveBot(bot.Guid);
            context.Save();
            context.Flush();
            host.Monitor.Poll();
            Assert.AreEqual(1, (await Get("/api/activity")).EnumerateArray().Count(a => a.GetProperty("kind").GetString() == "finished"));

            // The bot restarts: a new monitor over the same database
            var feed = new LiveFeed(DashboardHost.Json);
            using var push = new PushNotifier(context.Database, new DashboardSecrets(), null, DashboardHost.Json);
            var restarted = new ActivityMonitor(new DashboardService(new AppSettings { SpreadConfigurations = new[] { Configuration() } }, repository, coordinator, context, context.Database, false), feed, push);
            restarted.Start();
            try
            {
                restarted.Poll();
                Assert.AreEqual(0, feed.RecentActivity().Count(a => a.Kind == "finished"), "already announced before the restart");
            }
            finally
            {
                restarted.Dispose();
            }
        }

        [TestMethod]
        public async Task History_SumsWhatFinishedBotsMade()
        {
            await Start();
            coordinator.SetAllocationPaused(true);
            WaitUntil(() => context.GetBots().Any(), "a bot is allocated");
            var bot = context.GetBots().Single();
            bot.botContext.Balance = 0.0032m;
            context.RemoveBot(bot.Guid);
            context.Save();

            var history = await Get("/api/history");

            var total = history.GetProperty("profitByBaseMarket").EnumerateArray().Single();
            Assert.AreEqual(0.0002m, total.GetProperty("profit").GetDecimal());
            Assert.AreEqual(1, total.GetProperty("winners").GetInt32());
            Assert.AreEqual(Symbol, history.GetProperty("profitByMarket")[0].GetProperty("name").GetString());
            StringAssert.StartsWith(history.GetProperty("profitByConfiguration")[0].GetProperty("name").GetString(), "Spread");
            Assert.AreEqual(1, history.GetProperty("finishedBots").GetArrayLength());
            Assert.AreEqual(1, history.GetProperty("dailyProfit").GetArrayLength());
        }

        [TestMethod]
        public async Task Activity_ReportsFillsAndSamplesEquity()
        {
            await Start();
            WaitUntil(() => context.GetBots().Any(b => b.botContext.BotState == BotState.BuyOrderActive), "the bot's buy rests");
            var bot = context.GetBots().Single();
            context.RecordOrder(bot, new OrderData { Id = "buy-1", ClientOrderId = "buy-1", MarketSymbol = Symbol, Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.0001m, Quantity = 29, FillQuantity = 29, Proceeds = 0.0029m });
            context.Flush();

            host.Monitor.Poll();
            host.Monitor.Poll();

            var fills = (await Get("/api/activity")).EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "fill").ToList();
            Assert.AreEqual(1, fills.Count, "reported once, however often it polls");
            Assert.AreEqual("Bought 29 NMR", fills[0].GetProperty("title").GetString());

            context.RecordOrder(bot, new OrderData { Id = "sell-1", ClientOrderId = "sell-1", MarketSymbol = Symbol, Direction = OrderDirection.SELL, Status = OrderStatus.CLOSED, Limit = 0.00011m, Quantity = 29, FillQuantity = 29, Proceeds = 0.00319m });
            context.Flush();
            host.Monitor.Poll();

            Assert.AreEqual(1, (await Get("/api/activity")).EnumerateArray().Count(a => a.GetProperty("title").GetString() == "Sold 29 NMR"));
            using var db = context.Database();
            Assert.IsTrue(db.EquitySnapshots.Any(e => e.BaseMarket == "BTC"));
        }

        [TestMethod]
        public async Task Stream_StartsWithTheState()
        {
            await Start();

            using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/stream"), HttpCompletionOption.ResponseHeadersRead);
            Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual("event: state", first);
            StringAssert.StartsWith(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)), "data: {");
        }

        [TestMethod]
        public void Settings_BindTheDashboardSection()
        {
            const string json = @"{ ""Dashboard"": { ""Enabled"": true, ""Url"": ""http://127.0.0.1:6000"", ""PublicUrl"": ""https://box.example.ts.net"" } }";
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var bound = new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AppSettings>();

            Assert.IsTrue(bound.Dashboard.Enabled);
            Assert.AreEqual("http://127.0.0.1:6000", bound.Dashboard.Url);
            Assert.AreEqual("https://box.example.ts.net", bound.Dashboard.PublicUrl);
            Assert.IsFalse(new AppSettings().Dashboard.Enabled, "off unless enabled");
        }

        [TestMethod]
        public void Secrets_AreCreatedOnceAndKept()
        {
            var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var created = DashboardSecrets.LoadOrCreate(directory);
                var loaded = DashboardSecrets.LoadOrCreate(directory);

                Assert.IsTrue(created.AccessToken.Length >= 40);
                Assert.AreEqual(created.AccessToken, loaded.AccessToken);
                Assert.AreEqual(created.VapidPublicKey, loaded.VapidPublicKey);
                Assert.IsFalse(string.IsNullOrEmpty(loaded.VapidPrivateKey));
                Assert.IsFalse(File.Exists(Path.Combine(directory, DashboardSecrets.FileName + ".tmp")));
                if (!OperatingSystem.IsWindows())
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, DashboardSecrets.FileName)));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
