# .NET 10 Upgrade Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move SpreadBot and SpreadBot.Tests from `netcoreapp3.1` to `net10.0`, clear every known-vulnerable package, and keep runtime behavior identical, with a warning-free build and a test suite that pins the seams the upgrade can silently break.

**Architecture:** Test-first migration. Tasks 1–2 run on the *current* `netcoreapp3.1` build: they make the existing test green and add characterization tests (hashing/signing, config binding, websocket payload decoding, RestSharp request building, session-file round-trip). Tasks 3–5 then change the target framework, packages, build settings and publish settings, and every task re-runs the same suite as its safety net. Bittrex Global shut down in Dec 2023 (`socket-v3.bittrex.com` no longer resolves), so nothing can be checked against the live exchange. Unit tests plus a smoke run that gets as far as the websocket connect are the only verification available.

**Tech Stack:** .NET 10 SDK (10.0.400 installed), C# (default language version, no `ImplicitUsings`/`Nullable`), MSTest 4 + Moq, Newtonsoft.Json, RestSharp 106, legacy ASP.NET SignalR client, Microsoft.Extensions.Configuration.

**Spec:** None. Source: user request on 2026-10-05, "We need to upgrade this to .net 10". Every version, warning and expected output below comes from a trial migration run in a scratch clone while planning (2026-10-05). The trial passed 14/14 tests on both `netcoreapp3.1` and `net10.0`, built with 0 warnings under `-warnaserror`, and produced both the linux-x64 and win-x64 single-file publishes.

## Prerequisites (before Task 1)

- `CLAUDE.md` and this plan are currently **untracked** on `master`. Commit them to `master` first (the user's call), otherwise a worker in a fresh worktree won't have them and Task 5 Step 6 has nothing to edit.
- The .NET Core 3.1 runtime must be installed for Tasks 1–2 (`dotnet --list-runtimes` shows `Microsoft.NETCore.App 3.1.32` on the dev machine). Restores during Tasks 1–2 print `warning NETSDK1138` (3.1 is out of support) and `warning NU1903` (RestSharp 106.11.4). Both are expected until Task 3.

## Global Constraints

- Branch: `chore/net10-upgrade` off `master`. Never commit to `master`.
- Both projects target exactly `net10.0`.
- Package versions (exact):
  - SpreadBot: `Microsoft.AspNet.SignalR.Client` 2.4.3, `Microsoft.Extensions.Configuration` 10.0.12, `Microsoft.Extensions.Configuration.Binder` 10.0.12, `Microsoft.Extensions.Configuration.Json` 10.0.12, `Newtonsoft.Json` 13.0.4 (new explicit reference), `RestSharp` 106.15.0, `Telegram.Bot` 15.7.1 (unchanged).
  - SpreadBot.Tests: `Microsoft.NET.Test.Sdk` 18.10.1, `Moq` 4.21.0, `MSTest.TestAdapter` 4.4.1, `MSTest.TestFramework` 4.4.1, `coverlet.collector` 10.1.0.
- Do NOT move RestSharp to 107+. Those versions rewrote `RestClient`/`RestRequest`/`IRestResponse`, which `BittrexClient` request signing depends on.
- Do NOT move Telegram.Bot past 15.x (breaking API changes).
- Do NOT switch to the ASP.NET Core SignalR client: the Bittrex hub is classic SignalR.
- Do NOT enable `ImplicitUsings`. `Bot.cs`, `DataRepository.cs`, `BittrexClient.cs` and `FileCoordinatorContext.cs` use `System.Timers.Timer`, which would become ambiguous with `System.Threading.Timer` (CS0104).
- Do NOT enable `Nullable`, set `LangVersion`, or add `TreatWarningsAsErrors` to project files.
- Do NOT re-enable trimming (`PublishTrimmed`). Newtonsoft.Json, the configuration binder and SignalR's `dynamic` callbacks rely on reflection.
- Production code changes are limited to the lines listed in each task. No refactors, renames or "while I'm here" fixes.
- No changelog-style code comments ("was X", "moved from Y", "removed because…"). Put the rationale in the commit message. A comment stays only if it explains the current code.
- Edit existing files with targeted edits (keeps CRLF line endings and BOMs). Don't rewrite whole files unless the step shows the full file.
- Commit messages: imperative sentence, same style as `git log` (e.g. `Handle ApiErrorType.OrderNotOpen`), with a body giving the why. No attribution/co-author lines.
- Never put real credentials in any committed file. Smoke runs use dummy keys in a temp directory outside the repo.

## Review Focus

Things a user of this software would hit that the upgrade could break unnoticed. Each one has a pinning test or check in the task named:

1. **Restoring a `coordinatorContext.json` written by the current production build** (it contains a serialized `"Semaphore": {"CurrentCount": n}`) must bring the bots back. Today it silently fails; after the fix it must succeed → Task 2, `PreviousSessionContext_RestoresFileThatStillContainsSerializedSemaphore`.
2. **Settings decimals on a machine whose culture uses comma decimals** (e.g. pt-BR) must still bind `0.00000800` as 0.000008 after the binder 5 → 10 bump → Task 1, `Get_ParsesDecimalsWithInvariantCultureRegardlessOfCurrentCulture`.
3. **Hot-reload of the settings file** (Program.cs deserializes it with Newtonsoft and calls `AppSettings.Reload`) must still update `MaxNumberOfBots`/`SpreadConfigurations`, keep `ApiKey`, and raise `Reloaded` after the Newtonsoft 11 → 13 bump → Task 1, `Reload_WithNewtonsoftDeserializedSettings_UpdatesBotLimitAndConfigurationsAndRaisesReloaded`.
4. **A `SpreadConfiguration` that omits optional fields** must keep the class defaults (notably `SpreadThresholdBeforeCancelingCurrentOrder` = 1 satoshi) under binder 10 → Task 1, `Get_KeepsDefaultsForFieldsMissingFromJson`.
5. **Published single-file binary started from a different working directory** must still find `appsettings.json` next to the executable (`ToLocalFilePath` uses `Process.MainModule`) → Task 5, Step 3 smoke run.

---

### Task 1: Green, broadened test baseline on netcoreapp3.1

**Files:**
- Modify: `SpreadBot.Tests/UnitTest1.cs` (mock setup block, ~lines 75–130)
- Create: `SpreadBot.Tests/ExtensionsTests.cs`
- Create: `SpreadBot.Tests/AppSettingsBindingTests.cs`
- Create: `SpreadBot.Tests/DataConverterTests.cs`
- Create: `SpreadBot.Tests/RestSharpContractTests.cs`

**Interfaces:**
- Consumes: existing production types only: `Extensions.Hash(string)`, `Extensions.Sign(string, string)`, `AppSettings`, `SpreadConfiguration`, `AppSettings.Reload(AppSettings)`, `AppSettings.Reloaded`, `DataConverter.Decode<T>(string)`, `BittrexApiOrderData`, RestSharp 106 `RestClient.BuildUri(IRestRequest)`, `RestRequest.Parameters`.
- Produces: a 12-test suite (1 + 3 + 5 + 1 + 2). Every later task runs it with `dotnet test SpreadBot.sln`. **No production code changes in this task.**

- [ ] **Step 1: Create the branch**

```bash
git checkout master
git checkout -b chore/net10-upgrade
```

- [ ] **Step 2: Confirm the existing test is red**

Run: `dotnet test SpreadBot.sln`
Expected: `Failed!  - Failed:     1, Passed:     0` with `System.NullReferenceException` at `DataRepository.FetchOpenOrdersData`. The mock has no setup for `GetOpenOrdersData()`, so Moq returns `null`.

- [ ] **Step 3: Fix the mock drift in `SpreadBot.Tests/UnitTest1.cs`**

Two problems, both in the test and not in production code:
1. `GetOpenOrdersData()` and `GetMarketsData()` are not set up.
2. The mocked `BuyLimit`/`SellLimit` return an `OrderData` with no `ClientOrderId`. The real `BittrexClient` always sets it (`ClientOrderId ?? Id`), and `Bot`/`DataRepository` key order subscriptions by it. A null key throws `ArgumentNullException (Parameter 'key')` inside `SubscribeToOrderData`.

Insert directly after the existing `GetClosedOrdersData` setup (the block ending `.ReturnsAsync(new ApiRestResponse<BittrexApiOrderData.Order[]> { Data = new BittrexApiOrderData.Order[0] });`) and before the `OnBalance` setup:

```csharp
            mockExchange
                .Setup(e => e.GetOpenOrdersData())
                .ReturnsAsync(new ApiRestResponse<BittrexApiOrderData.Order[]> { Data = new BittrexApiOrderData.Order[0] });
            mockExchange
                .Setup(e => e.GetMarketsData())
                .ReturnsAsync(new BittrexApiMarketData[0]);
```

Replace:

```csharp
                .ReturnsAsync(new OrderData() { Id = buyOrderId, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN })
```

with:

```csharp
                .ReturnsAsync(new OrderData() { Id = buyOrderId, ClientOrderId = buyOrderId, Direction = OrderDirection.BUY, Status = OrderStatus.OPEN })
```

Replace:

```csharp
                .ReturnsAsync(new OrderData() { Id = sellOrderId, Direction = OrderDirection.SELL, Status = OrderStatus.OPEN })
```

with:

```csharp
                .ReturnsAsync(new OrderData() { Id = sellOrderId, ClientOrderId = sellOrderId, Direction = OrderDirection.SELL, Status = OrderStatus.OPEN })
```

- [ ] **Step 4: Run it, 3 times (it is timing-based)**

Run: `dotnet test SpreadBot.sln` three times.
Expected each time: `Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1` on `(netcoreapp3.1)`.

- [ ] **Step 5: Commit**

```bash
git add SpreadBot.Tests/UnitTest1.cs
git commit -m "Fix BasicBotFlowTest mock drift" -m "The mock IExchange had no setups for GetOpenOrdersData/GetMarketsData (NullReferenceException during StartConsumingData), and the mocked orders had no ClientOrderId, which Bot and DataRepository use as the subscription key. Gives the .NET 10 upgrade a green baseline."
```

- [ ] **Step 6: Create `SpreadBot.Tests/ExtensionsTests.cs`**

These pin the Bittrex request-signing primitives. Task 4 changes how `Hash` creates its SHA-512 instance, and the output must not change.

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;

namespace SpreadBot.Tests
{
    [TestClass]
    public class ExtensionsTests
    {
        [TestMethod]
        public void Hash_EmptyString_ReturnsLowercaseSha512Hex()
        {
            Assert.AreEqual(
                "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e",
                string.Empty.Hash());
        }

        [TestMethod]
        public void Hash_JsonBody_ReturnsLowercaseSha512Hex()
        {
            Assert.AreEqual(
                "efb7a8298f905ae743dbe2152e162415f62a16d2d5ac5c78816dcd57114e7a574729b813988f1d0984cf6f38c4fcc9a37ea9fec3da351983536f72785d7ab707",
                "{\"a\":1}".Hash());
        }

        [TestMethod]
        public void Sign_ReturnsLowercaseHmacSha512Hex()
        {
            Assert.AreEqual(
                "b42af09057bac1e2d41708e48a902e09b5ff7f12ab428a4fe86653c73dd248fb82f948a549f7b791a5b41915ee4d1ec3935357e4e2317250d0372afa2ebeeb3a",
                "The quick brown fox jumps over the lazy dog".Sign("key"));
        }
    }
}
```

- [ ] **Step 7: Create `SpreadBot.Tests/AppSettingsBindingTests.cs`**

These pin `Microsoft.Extensions.Configuration.Binder` (5 → 10) and the Newtonsoft hot-reload path (11 → 13). Review Focus items 2, 3 and 4 live here.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SpreadBot.Tests
{
    [TestClass]
    public class AppSettingsBindingTests
    {
        private const string SettingsJson = @"{
  ""ApiKey"": ""key"",
  ""ApiSecret"": ""secret"",
  ""TryToRestoreSession"": true,
  ""MaxNumberOfBots"": 3,
  ""ResyncIntervalMs"": 60000,
  ""CoinMarketCapApiKey"": ""cmc"",
  ""SpreadConfigurations"": [
    {
      ""BaseMarket"": ""ETH"",
      ""MaxPercentChangeFromPreviousDay"": 40.0,
      ""MinimumSpreadPercentage"": 1.0,
      ""MinimumQuoteVolume"": 150.0,
      ""MinimumPrice"": 0.00000800,
      ""MinimumNegotiatedAmount"": 0.00100000,
      ""AllocatedAmountOfBaseCurrency"": 0.3,
      ""SpreadThresholdBeforeCancelingCurrentOrder"": 0.00000002,
      ""MinutesForLoss"": 90,
      ""MinimumProfitPercentage"": 0.6,
      ""AvoidTokenizedSecurities"": true
    },
    {
      ""BaseMarket"": ""BTC"",
      ""AllocatedAmountOfBaseCurrency"": 0.01
    }
  ]
}";

        private static AppSettings Bind(string json)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return new ConfigurationBuilder().AddJsonStream(stream).Build().Get<AppSettings>();
        }

        [TestMethod]
        public void Get_BindsTopLevelSettings()
        {
            var settings = Bind(SettingsJson);

            Assert.AreEqual("key", settings.ApiKey);
            Assert.AreEqual("secret", settings.ApiSecret);
            Assert.IsTrue(settings.TryToRestoreSession);
            Assert.AreEqual(3, settings.MaxNumberOfBots);
            Assert.AreEqual(60000, settings.ResyncIntervalMs);
            Assert.AreEqual("cmc", settings.CoinMarketCapApiKey);
        }

        [TestMethod]
        public void Get_BindsEverySpreadConfigurationField()
        {
            var eth = Bind(SettingsJson).SpreadConfigurations.First();

            Assert.AreEqual("ETH", eth.BaseMarket);
            Assert.AreEqual(40.0m, eth.MaxPercentChangeFromPreviousDay);
            Assert.AreEqual(1.0m, eth.MinimumSpreadPercentage);
            Assert.AreEqual(150.0m, eth.MinimumQuoteVolume);
            Assert.AreEqual(0.000008m, eth.MinimumPrice);
            Assert.AreEqual(0.001m, eth.MinimumNegotiatedAmount);
            Assert.AreEqual(0.3m, eth.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual(0.00000002m, eth.SpreadThresholdBeforeCancelingCurrentOrder);
            Assert.AreEqual(90, eth.MinutesForLoss);
            Assert.AreEqual(0.6m, eth.MinimumProfitPercentage);
            Assert.IsTrue(eth.AvoidTokenizedSecurities);
        }

        [TestMethod]
        public void Get_KeepsDefaultsForFieldsMissingFromJson()
        {
            var btc = Bind(SettingsJson).SpreadConfigurations.Last();

            Assert.AreEqual("BTC", btc.BaseMarket);
            Assert.AreEqual(0.01m, btc.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual(0.00000001m, btc.SpreadThresholdBeforeCancelingCurrentOrder);
            Assert.AreEqual(0m, btc.MinimumNegotiatedAmount);
            Assert.IsFalse(btc.AvoidTokenizedSecurities);
        }

        [TestMethod]
        public void Get_ParsesDecimalsWithInvariantCultureRegardlessOfCurrentCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("pt-BR");

                var eth = Bind(SettingsJson).SpreadConfigurations.First();

                Assert.AreEqual(0.000008m, eth.MinimumPrice);
                Assert.AreEqual(0.6m, eth.MinimumProfitPercentage);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [TestMethod]
        public void Reload_WithNewtonsoftDeserializedSettings_UpdatesBotLimitAndConfigurationsAndRaisesReloaded()
        {
            var settings = Bind(SettingsJson);
            var reloadedRaised = false;
            settings.Reloaded += (sender, e) => reloadedRaised = true;
            const string updatedJson = @"{ ""ApiKey"": ""other-key"", ""MaxNumberOfBots"": 7, ""SpreadConfigurations"": [ { ""BaseMarket"": ""USDT"", ""AllocatedAmountOfBaseCurrency"": 25.5 } ] }";

            settings.Reload(JsonConvert.DeserializeObject<AppSettings>(updatedJson));

            Assert.IsTrue(reloadedRaised);
            Assert.AreEqual(7, settings.MaxNumberOfBots);
            var usdt = settings.SpreadConfigurations.Single();
            Assert.AreEqual("USDT", usdt.BaseMarket);
            Assert.AreEqual(25.5m, usdt.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual("key", settings.ApiKey);
        }
    }
}
```

- [ ] **Step 8: Create `SpreadBot.Tests/DataConverterTests.cs`**

This pins websocket payload decoding: base64 → raw deflate → Newtonsoft with camelCase, string enums, decimal floats and UTC dates.

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Models;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SpreadBot.Tests
{
    [TestClass]
    public class DataConverterTests
    {
        private static string ToWireFormat(string json)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                deflate.Write(bytes, 0, bytes.Length);
            }
            return Convert.ToBase64String(output.ToArray());
        }

        [TestMethod]
        public void Decode_InflatesBase64DeflateJsonIntoOrderMessage()
        {
            const string json = "{\"accountId\":\"acc-1\",\"sequence\":7,\"delta\":{\"id\":\"order-1\",\"marketSymbol\":\"NMR-BTC\",\"direction\":\"SELL\",\"type\":\"LIMIT\",\"status\":\"CLOSED\",\"quantity\":1.5,\"limit\":0.00010101,\"fillQuantity\":1.5,\"commission\":0.00000030,\"proceeds\":0.00015151,\"closedAt\":\"2021-05-10T14:42:32.123Z\"}}";

            var decoded = DataConverter.Decode<BittrexApiOrderData>(ToWireFormat(json));

            Assert.AreEqual("acc-1", decoded.AccountId);
            Assert.AreEqual(7, decoded.Sequence);
            Assert.AreEqual("order-1", decoded.Delta.Id);
            Assert.AreEqual("NMR-BTC", decoded.Delta.MarketSymbol);
            Assert.AreEqual(OrderDirection.SELL, decoded.Delta.Direction);
            Assert.AreEqual(OrderType.LIMIT, decoded.Delta.Type);
            Assert.AreEqual(OrderStatus.CLOSED, decoded.Delta.Status);
            Assert.AreEqual(1.5m, decoded.Delta.Quantity);
            Assert.AreEqual(0.00010101m, decoded.Delta.Limit);
            Assert.AreEqual(0.0000003m, decoded.Delta.Commission);
            Assert.AreEqual(0.00015151m, decoded.Delta.Proceeds);
            Assert.AreEqual(new DateTime(2021, 5, 10, 14, 42, 32, 123, DateTimeKind.Utc), decoded.Delta.ClosedAt);
            Assert.AreEqual(DateTimeKind.Utc, decoded.Delta.ClosedAt.Kind);
        }
    }
}
```

- [ ] **Step 9: Create `SpreadBot.Tests/RestSharpContractTests.cs`**

These pin the two RestSharp outputs that go into Bittrex's request signature. They are what makes RestSharp 106.11.4 → 106.15.0 safe.

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RestSharp;
using System.Linq;

namespace SpreadBot.Tests
{
    // BittrexClient.ExecuteAuthenticatedRequest signs BuildUri(request) and the RequestBody parameter value,
    // so any change in how RestSharp produces either one breaks every authenticated Bittrex call.
    [TestClass]
    public class RestSharpContractTests
    {
        [TestMethod]
        public void BuildUri_AppendsResourceAndQueryParametersToBaseUrl()
        {
            var client = new RestClient("https://api.bittrex.com/v3");
            var request = new RestRequest("/orders/closed", Method.GET, DataFormat.Json);
            request.AddQueryParameter("pageSize", "200");
            request.AddQueryParameter("previousPageToken", "abc");

            Assert.AreEqual(
                "https://api.bittrex.com/v3/orders/closed?pageSize=200&previousPageToken=abc",
                client.BuildUri(request).ToString());
        }

        [TestMethod]
        public void AddParameter_RequestBody_ExposesRawBodyString()
        {
            const string body = "{\"marketSymbol\":\"NMR-BTC\"}";
            var request = new RestRequest("/orders", Method.POST, DataFormat.Json);
            request.AddParameter("application/json", body, ParameterType.RequestBody);

            var bodyParameter = request.Parameters.Single(p => p.Type == ParameterType.RequestBody);

            Assert.AreEqual(body, bodyParameter.Value as string);
            Assert.AreEqual("POST", request.Method.ToString());
        }
    }
}
```

- [ ] **Step 10: Run the suite**

Run: `dotnet test SpreadBot.sln`
Expected: `Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12` on `(netcoreapp3.1)`.
These tests describe current behavior, so they should pass immediately. **If any fail, STOP and report the failure verbatim. Do not change production code to make them pass.**

- [ ] **Step 11: Commit**

```bash
git add SpreadBot.Tests/ExtensionsTests.cs SpreadBot.Tests/AppSettingsBindingTests.cs SpreadBot.Tests/DataConverterTests.cs SpreadBot.Tests/RestSharpContractTests.cs
git commit -m "Add characterization tests for upgrade-sensitive seams" -m "Pins request hashing/signing, configuration binding (including culture and hot-reload), websocket payload decoding, and the RestSharp URI/body values that go into the Bittrex signature, so the .NET 10 and package upgrades can be verified without the (defunct) live exchange."
```

---

### Task 2: Fix session restore (`BotContext.Semaphore` serialized by Newtonsoft)

Bug found while planning. `BotContext` marks `Semaphore` with `[JsonIgnore]` from **System.Text.Json**, but sessions are written and read with **Newtonsoft**, which ignores that attribute. Newtonsoft therefore writes `"Semaphore": {"CurrentCount": n}` into `coordinatorContext.json`. On restore, `BotContext` is built through its parameterized constructor, so Newtonsoft has to construct a `SemaphoreQueue`. It can't (no default constructor), it throws, and `FileCoordinatorContext.GetPreviousSessionContext` catches the exception and returns `null`. The result: `TryToRestoreSession` never restores bots that were saved. The upgrade doesn't depend on this fix, but the round-trip test is the only guard on session files across the Newtonsoft 11 → 13 bump. If the user declines this task, drop it, and every later "14 tests" becomes "12 tests".

**Files:**
- Create: `SpreadBot.Tests/SessionContextSerializationTests.cs`
- Modify: `SpreadBot/Infrastructure/BotContext.cs:5` (one `using` line)

**Interfaces:**
- Consumes: `PreviousSessionContext { List<BotContext> BotContexts; Dictionary<string, decimal> DustPerMarket }`, `BotContext(SpreadConfiguration, MarketData, BotState, decimal)`, public field `BotContext.spreadConfiguration`, `SpreadBot.Logic.BotState` (`Buying=0, BuyOrderActive=1, Bought=2, SellOrderActive=3, FinishedWork=4`).
- Produces: suite grows to 14 tests. `BotContext.Semaphore` is no longer written to session files.

- [ ] **Step 1: Write the failing tests: create `SpreadBot.Tests/SessionContextSerializationTests.cs`**

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System.Collections.Generic;
using System.Linq;

namespace SpreadBot.Tests
{
    [TestClass]
    public class SessionContextSerializationTests
    {
        [TestMethod]
        public void PreviousSessionContext_RoundTripsBotStateThroughNewtonsoft()
        {
            var configuration = new SpreadConfiguration
            {
                BaseMarket = "BTC",
                AllocatedAmountOfBaseCurrency = 0.001m,
                MinimumNegotiatedAmount = 0.0005m,
                MinutesForLoss = 30,
                MinimumProfitPercentage = 0.5m
            };
            var market = new MarketData { Symbol = "NMR-BTC", BidRate = 0.00009999m, AskRate = 0.00010101m, Precision = 8 };
            var context = new BotContext(configuration, market, BotState.Buying, 0.25m)
            {
                Balance = 0.0004m,
                BotState = BotState.SellOrderActive,
                BoughtPrice = 0.0001m,
                CurrentOrderData = new OrderData
                {
                    Id = "order-1",
                    ClientOrderId = "client-1",
                    Direction = OrderDirection.SELL,
                    Status = OrderStatus.OPEN,
                    Limit = 0.000101m
                }
            };
            var session = new PreviousSessionContext
            {
                BotContexts = new List<BotContext> { context },
                DustPerMarket = new Dictionary<string, decimal> { ["XYZ-BTC"] = 0.5m }
            };

            var json = JsonConvert.SerializeObject(session, Formatting.Indented);
            var restored = JsonConvert.DeserializeObject<PreviousSessionContext>(json);

            var bot = restored.BotContexts.Single();
            Assert.AreEqual(configuration, bot.spreadConfiguration);
            Assert.AreEqual(0.0004m, bot.Balance);
            Assert.AreEqual(0.25m, bot.HeldAmount);
            Assert.AreEqual(BotState.SellOrderActive, bot.BotState);
            Assert.AreEqual(0.0001m, bot.BoughtPrice);
            Assert.AreEqual("NMR-BTC", bot.LatestMarketData.Symbol);
            Assert.AreEqual(0.00010101m, bot.LatestMarketData.AskRate.Value);
            Assert.AreEqual(8, bot.LatestMarketData.Precision.Value);
            Assert.AreEqual("client-1", bot.CurrentOrderData.ClientOrderId);
            Assert.AreEqual(OrderStatus.OPEN, bot.CurrentOrderData.Status);
            Assert.AreEqual(0.000101m, bot.CurrentOrderData.Limit);
            Assert.AreEqual(0.5m, restored.DustPerMarket["XYZ-BTC"]);
        }

        [TestMethod]
        public void PreviousSessionContext_RestoresFileThatStillContainsSerializedSemaphore()
        {
            const string savedByOlderBuild = @"{
  ""BotContexts"": [
    {
      ""spreadConfiguration"": { ""BaseMarket"": ""BTC"", ""AllocatedAmountOfBaseCurrency"": 0.001, ""MinimumNegotiatedAmount"": 0.0005 },
      ""buyStopwatch"": { ""IsRunning"": false, ""Elapsed"": ""00:00:00"", ""ElapsedMilliseconds"": 0, ""ElapsedTicks"": 0 },
      ""Semaphore"": { ""CurrentCount"": 1 },
      ""Guid"": ""0639511b-8edb-4063-81e3-5dbec8dbf69f"",
      ""SemaphoreCurrentCount"": 1,
      ""Balance"": 0.0004,
      ""HeldAmount"": 0.25,
      ""BotState"": 3,
      ""CurrentOrderData"": null,
      ""LatestMarketData"": { ""Symbol"": ""NMR-BTC"", ""BidRate"": 0.00009999, ""AskRate"": 0.00010101, ""Precision"": 8 },
      ""BoughtPrice"": 0.0001
    }
  ],
  ""DustPerMarket"": { ""XYZ-BTC"": 0.5 }
}";

            var restored = JsonConvert.DeserializeObject<PreviousSessionContext>(savedByOlderBuild);

            var bot = restored.BotContexts.Single();
            Assert.AreEqual("BTC", bot.spreadConfiguration.BaseMarket);
            Assert.AreEqual(0.001m, bot.spreadConfiguration.AllocatedAmountOfBaseCurrency);
            Assert.AreEqual(0.0004m, bot.Balance);
            Assert.AreEqual(0.25m, bot.HeldAmount);
            Assert.AreEqual(BotState.SellOrderActive, bot.BotState);
            Assert.AreEqual("NMR-BTC", bot.LatestMarketData.Symbol);
            Assert.IsNull(bot.CurrentOrderData);
            Assert.AreEqual(0.5m, restored.DustPerMarket["XYZ-BTC"]);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test SpreadBot.sln`
Expected: `Failed!  - Failed:     2, Passed:    12, Skipped:     0, Total:    14`. Both new tests fail with:
`Newtonsoft.Json.JsonSerializationException: Unable to find a constructor to use for type SpreadBot.Infrastructure.SemaphoreQueue.`

- [ ] **Step 3: Fix `SpreadBot/Infrastructure/BotContext.cs`**

Replace the line:

```csharp
using System.Text.Json.Serialization;
```

with:

```csharp
using Newtonsoft.Json;
```

Nothing else changes. The existing `[JsonIgnore]` on `Semaphore` now resolves to `Newtonsoft.Json.JsonIgnoreAttribute`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test SpreadBot.sln`
Expected: `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14` on `(netcoreapp3.1)`.

- [ ] **Step 5: Commit**

```bash
git add SpreadBot.Tests/SessionContextSerializationTests.cs SpreadBot/Infrastructure/BotContext.cs
git commit -m "Fix session restore failing on serialized BotContext.Semaphore" -m "BotContext marked Semaphore with System.Text.Json's [JsonIgnore], but coordinatorContext.json is written and read with Newtonsoft, which ignored it. Restoring then had to construct a SemaphoreQueue (no default constructor), threw, and GetPreviousSessionContext swallowed the exception and returned null, so TryToRestoreSession never restored saved bots. Using Newtonsoft's attribute fixes it, and files written by older builds still load because the extra Semaphore member is skipped."
```

---

### Task 3: Retarget to net10.0 and update NuGet packages

**Files:**
- Modify: `SpreadBot/SpreadBot.csproj` (full content below)
- Modify: `SpreadBot.Tests/SpreadBot.Tests.csproj` (full content below)
- Modify: `SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml` (3 lines)

**Interfaces:**
- Consumes: the 14-test suite from Tasks 1–2.
- Produces: both projects on `net10.0` with the Global Constraints package set. `PublishTrimmed` is still present here and is removed in Task 4. Non-portable RIDs (`win10-x64`, `ubuntu.16.10-x64`) are gone: the .NET 8+ SDK no longer recognizes them.

- [ ] **Step 1: Replace the contents of `SpreadBot/SpreadBot.csproj` with:**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifiers>win-x64;linux-x64</RuntimeIdentifiers>
    <PublishTrimmed>true</PublishTrimmed>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNet.SignalR.Client" Version="2.4.3" />
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.12" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.4" />
    <PackageReference Include="RestSharp" Version="106.15.0" />
    <PackageReference Include="Telegram.Bot" Version="15.7.1" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Never</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

Why `Newtonsoft.Json` is now explicit: the code uses it directly, but it used to arrive transitively and resolved to 11.0.2, which has a high-severity advisory (GHSA-5crp-9r3c-p9vr).

- [ ] **Step 2: Replace the contents of `SpreadBot.Tests/SpreadBot.Tests.csproj` with:**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>

    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageReference Include="Moq" Version="4.21.0" />
    <PackageReference Include="MSTest.TestAdapter" Version="4.4.1" />
    <PackageReference Include="MSTest.TestFramework" Version="4.4.1" />
    <PackageReference Include="coverlet.collector" Version="10.1.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\SpreadBot\SpreadBot.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Update `SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml`** (targeted edits; the file starts with a BOM, so keep it)

- `<PublishDir>bin\Release\netcoreapp3.1\publish\</PublishDir>` → `<PublishDir>bin\Release\net10.0\publish\</PublishDir>`
- `<TargetFramework>netcoreapp3.1</TargetFramework>` → `<TargetFramework>net10.0</TargetFramework>`
- `<RuntimeIdentifier>ubuntu.16.10-x64</RuntimeIdentifier>` → `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>`

Leave `<PublishTrimmed>True</PublishTrimmed>` alone. Task 4 removes it.

- [ ] **Step 4: Build**

Run: `dotnet build SpreadBot.sln --no-incremental`
Expected: `Build succeeded`, `0 Error(s)`. Warnings are expected and Task 4 handles them: about 30× `IL2026` (trim analysis on Newtonsoft calls and on `dynamic` in `SocketClient.cs`), plus `SYSLIB0014` (`SocketClient.cs:30`), `SYSLIB0021` (`Extensions.cs:25`), `CA2200` (`Coordinator.cs:283`) and `CS4014` (`Program.cs:53`). There must be **no `NU1903`** and **no `NETSDK1138`** warnings. If either appears, a version or the target framework is wrong.

- [ ] **Step 5: Run the suite on net10.0, 3 times**

Run: `dotnet test SpreadBot.sln` three times.
Expected each time: `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14` and the line ends with `(net10.0)`.

- [ ] **Step 6: Confirm no vulnerable packages remain**

Run: `dotnet list SpreadBot.sln package --vulnerable --include-transitive`
Expected, for both projects: `has no vulnerable packages given the current sources.` (Before this task, RestSharp, Newtonsoft.Json, System.Net.Http 4.3.0 and System.Text.RegularExpressions 4.3.0 were flagged. The last two are pruned automatically on net10.0.)

- [ ] **Step 7: Commit**

```bash
git add SpreadBot/SpreadBot.csproj SpreadBot.Tests/SpreadBot.Tests.csproj SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml
git commit -m "Retarget to .NET 10 and update NuGet packages" -m "netcoreapp3.1 has been out of support since Dec 2022. Moves both projects to net10.0, replaces the non-portable RIDs that the .NET 8+ SDK rejects with win-x64/linux-x64, and updates packages to clear every audit advisory: RestSharp 106.15.0 (last API-compatible 106.x, fixes GHSA-9pq7-rcxv-47vq) and an explicit Newtonsoft.Json 13.0.4 (was transitive 11.0.2, GHSA-5crp-9r3c-p9vr). RestSharp 107+, Telegram.Bot 16+ and the ASP.NET Core SignalR client are deliberately not adopted: each breaks the code (request signing, bot API, classic Bittrex hub)."
```

---

### Task 4: Disable trimming and make the build warning-free

**Files:**
- Modify: `SpreadBot/SpreadBot.csproj` (remove 1 line)
- Modify: `SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml` (remove 1 line)
- Modify: `SpreadBot/Infrastructure/Extensions.cs:25`
- Modify: `SpreadBot/Logic/Coordinator.cs:283`
- Modify: `SpreadBot/Program.cs:53`
- Modify: `SpreadBot/Infrastructure/Exchanges/Bittrex/SocketClient.cs:30`

**Interfaces:**
- Consumes: the Task 3 build. `ExtensionsTests` (Task 1) guards the `Hash` change.
- Produces: `dotnet build SpreadBot.sln --no-incremental -warnaserror` reports 0 warnings, and Task 5 publishes untrimmed.

- [ ] **Step 1: Remove trimming**

In `SpreadBot/SpreadBot.csproj`, delete this line:

```xml
    <PublishTrimmed>true</PublishTrimmed>
```

In `SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml`, delete this line:

```xml
    <PublishTrimmed>True</PublishTrimmed>
```

Both lines must go: the publish profile overrides the project file at publish time. Why: since .NET 7, trimming defaults to full mode, and Newtonsoft.Json 13, the configuration binder and SignalR's `dynamic` message handler all need reflection over types the trimmer would remove. That shows up as the ~30 `IL2026` warnings from Task 3 and would fail at runtime.

- [ ] **Step 2: Build to confirm the IL2026 warnings are gone**

Run: `dotnet build SpreadBot.sln --no-incremental`
Expected: `4 Warning(s)`, exactly `SYSLIB0014`, `SYSLIB0021`, `CA2200` and `CS4014`, with no `IL2026`.

- [ ] **Step 3: Fix SYSLIB0021 in `SpreadBot/Infrastructure/Extensions.cs`**

Replace:

```csharp
            using (SHA512 sha512 = new SHA512Managed())
```

with:

```csharp
            using (SHA512 sha512 = SHA512.Create())
```

- [ ] **Step 4: Fix CA2200 in `SpreadBot/Logic/Coordinator.cs`** (inside `ExecuteBalanceRelatedAction`)

Replace:

```csharp
                throw e;
```

with:

```csharp
                throw;
```

`e` is still used by the `LogError` line above it, so the `catch (Exception e)` stays.

- [ ] **Step 5: Fix CS4014 in `SpreadBot/Program.cs`**

Replace:

```csharp
            Task.Run(() => watcher.Changed += reloadAppSettings);
```

with:

```csharp
            watcher.Changed += reloadAppSettings;
```

The old line queued a thread-pool task whose only job was to attach the handler, and that task was never awaited. Attaching it directly gives the same result. Keep `using System.Threading.Tasks;` because `Main` returns `Task`.

- [ ] **Step 6: Fix SYSLIB0014 in `SpreadBot/Infrastructure/Exchanges/Bittrex/SocketClient.cs`**

Replace:

```csharp
            ServicePointManager.DefaultConnectionLimit = 10;
```

with:

```csharp
#pragma warning disable SYSLIB0014 // Kept for RestSharp 106, which still sends requests through HttpWebRequest
            ServicePointManager.DefaultConnectionLimit = 10;
#pragma warning restore SYSLIB0014
```

The `#pragma` lines start at column 0. The setting is suppressed rather than deleted so that behavior stays identical. Whether it still affects RestSharp 106's `HttpWebRequest` path on .NET 10 wasn't verified, and the exchange is unavailable to test against.

- [ ] **Step 7: Verify a warning-free build**

Run: `dotnet build SpreadBot.sln --no-incremental -warnaserror`
Expected: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 8: Run the suite**

Run: `dotnet test SpreadBot.sln`
Expected: `Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14` on `(net10.0)`. The three `ExtensionsTests` passing proves `SHA512.Create()` produces the same hashes.

- [ ] **Step 9: Commit**

```bash
git add SpreadBot/SpreadBot.csproj SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml SpreadBot/Infrastructure/Extensions.cs SpreadBot/Logic/Coordinator.cs SpreadBot/Program.cs SpreadBot/Infrastructure/Exchanges/Bittrex/SocketClient.cs
git commit -m "Disable trimming and clear .NET 10 build warnings" -m "Trimming has defaulted to full mode since .NET 7, and Newtonsoft.Json 13, the configuration binder and SignalR's dynamic handlers need reflection (about 30 IL2026 warnings), so PublishTrimmed is removed from the project and the publish profile. Also: SHA512.Create() replaces the obsolete SHA512Managed (same output, covered by ExtensionsTests), a bare throw keeps the stack trace (CA2200), the FileSystemWatcher handler is attached directly instead of from an unawaited Task.Run (CS4014), and ServicePointManager.DefaultConnectionLimit gets a scoped SYSLIB0014 suppression to keep behavior identical for RestSharp 106's HttpWebRequest path."
```

---

### Task 5: Verify publish and smoke-run, update launch config and CLAUDE.md

**Files:**
- Modify: `.vscode/launch.json` (1 line)
- Modify: `CLAUDE.md` (4 targeted edits)
- No production code changes.

**Interfaces:**
- Consumes: the Task 4 build (untrimmed, warning-free).
- Produces: confirmation that both publish targets work and that the app starts on .NET 10, plus docs that match the new state.

- [ ] **Step 1: Publish with the profile (linux-x64, self-contained, single-file)**

Run: `dotnet publish SpreadBot/SpreadBot.csproj -p:PublishProfile=FolderProfile`
Expected: no `warning`/`error` lines. `SpreadBot/bin/Release/net10.0/publish/` contains exactly `SpreadBot` (about 75 MB) and `SpreadBot.pdb`.
Check it's a Linux binary: `head -c 4 SpreadBot/bin/Release/net10.0/publish/SpreadBot | od -c | head -1` prints `177   E   L   F`.

- [ ] **Step 2: Publish a win-x64 single-file build to a temp directory**

```bash
SMOKE_DIR="$(mktemp -d)"
dotnet publish SpreadBot/SpreadBot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$SMOKE_DIR/app"
printf '{\n  "ApiKey": "dummy",\n  "ApiSecret": "dummy",\n  "MaxNumberOfBots": 1,\n  "ResyncIntervalMs": 60000,\n  "SpreadConfigurations": [ { "BaseMarket": "BTC", "AllocatedAmountOfBaseCurrency": 0.001 } ]\n}\n' > "$SMOKE_DIR/app/appsettings.json"
ls "$SMOKE_DIR/app"
```

Expected: `SpreadBot.exe`, `SpreadBot.pdb`, `appsettings.json`.

- [ ] **Step 3: Smoke-run from a different working directory (Review Focus 5)**

```bash
mkdir -p "$SMOKE_DIR/elsewhere" && cd "$SMOKE_DIR/elsewhere"
timeout 60 ../app/SpreadBot.exe < /dev/null 2>&1 | head -12
cd - >/dev/null
```

Expected, in order:
1. `Using config file appsettings.json`
2. a path ending in `\app\appsettings.json`: it resolved next to the exe, not in `elsewhere`
3. `... - Connecting..`
4. a websocket connection error (on a normal network, `No such host is known. (socket-v3.bittrex.com:443)`: Bittrex no longer exists)
5. `Unhandled exception. System.Exception: Websocket failed to connect`

Lines 1–3 plus 5 are the pass criteria: settings loaded, the SignalR client ran on .NET 10, and failure handling ran. A crash before `Connecting..` (for example `FileNotFoundException`, `TypeLoadException` or `MissingMethodException`) is a FAIL. Report it verbatim.

Clean up: `rm -rf "$SMOKE_DIR"` and `git status --short` (must not list any publish or settings files).

- [ ] **Step 4: Update `.vscode/launch.json`**

Replace:

```json
      "program": "${workspaceFolder}/SpreadBot/bin/Debug/netcoreapp3.1/SpreadBot.dll",
```

with:

```json
      "program": "${workspaceFolder}/SpreadBot/bin/Debug/net10.0/SpreadBot.dll",
```

- [ ] **Step 5: Confirm the debug output path exists**

Run: `dotnet build SpreadBot/SpreadBot.csproj && ls SpreadBot/bin/Debug/net10.0/SpreadBot.dll`
Expected: the file is listed.

- [ ] **Step 6: Update `CLAUDE.md`** (if it isn't in the checkout, see Prerequisites: stop and report)

Edit 1. Replace `Console app (.NET Core 3.1, C#)` with `Console app (.NET 10, C#)`.

Edit 2. Replace:

```
dotnet publish SpreadBot -p:PublishProfile=FolderProfile                      # self-contained, single-file, trimmed, ubuntu.16.10-x64
```

with:

```
dotnet publish SpreadBot -p:PublishProfile=FolderProfile                      # self-contained, single-file, linux-x64 (untrimmed)
```

Edit 3. Replace the whole bullet that starts `- Tests are MSTest + Moq and need the .NET Core 3.1 runtime.` (it ends `...during \`StartConsumingData()\`.`) with:

```
- Tests are MSTest 4 + Moq. Besides the end-to-end `BasicBotFlowTest` (`UnitTest1.cs`), characterization tests pin the seams package upgrades can silently break: request hashing/signing, config binding (culture, defaults, hot-reload), websocket payload decoding, the RestSharp URI/body values that are signed, and the `coordinatorContext.json` round-trip.
- The build is warning-free; keep it that way: `dotnet build SpreadBot.sln --no-incremental -warnaserror`.
```

Edit 4. Replace:

```
- Serialization is Newtonsoft.Json everywhere (it arrives transitively via the SignalR client). `System.Text.Json` attributes such as `[JsonIgnore]` on `BotContext.Semaphore` have no effect.
```

with:

```
- Serialization is Newtonsoft.Json everywhere. Use Newtonsoft attributes; `System.Text.Json` attributes are silently ignored.
- Trimming is off on purpose: Newtonsoft, the config binder and SignalR's `dynamic` handlers need reflection. Don't re-enable `PublishTrimmed`.
- Don't enable `ImplicitUsings`: several files use `System.Timers.Timer`, which would clash with `System.Threading.Timer`.
- Package pins are deliberate: RestSharp stays on 106.x (107+ rewrote the API that request signing uses), Telegram.Bot on 15.x, and the SignalR client is the classic ASP.NET one (the Bittrex hub isn't ASP.NET Core SignalR).
```

- [ ] **Step 7: Final whole-branch check**

Run:
```bash
dotnet build SpreadBot.sln --no-incremental -warnaserror
dotnet test SpreadBot.sln
git diff master --stat
```
Expected: 0 warnings / 0 errors; `Passed: 14` on `(net10.0)`. The diff touches only: `.vscode/launch.json`, `CLAUDE.md`, `SpreadBot.Tests/{UnitTest1,ExtensionsTests,AppSettingsBindingTests,DataConverterTests,RestSharpContractTests,SessionContextSerializationTests}.cs`, `SpreadBot.Tests/SpreadBot.Tests.csproj`, `SpreadBot/SpreadBot.csproj`, `SpreadBot/Properties/PublishProfiles/FolderProfile.pubxml`, `SpreadBot/Infrastructure/{BotContext,Extensions}.cs`, `SpreadBot/Infrastructure/Exchanges/Bittrex/SocketClient.cs`, `SpreadBot/Logic/Coordinator.cs`, `SpreadBot/Program.cs`. Anything else is a FAIL: revert it.

- [ ] **Step 8: Commit**

```bash
git add .vscode/launch.json CLAUDE.md
git commit -m "Point launch config and CLAUDE.md at .NET 10" -m "Debug launch path moves to bin/Debug/net10.0. CLAUDE.md now describes the net10.0 target, the untrimmed linux-x64 publish, the expanded test suite, the warning-free build, and why trimming, ImplicitUsings and the RestSharp/Telegram.Bot/SignalR versions stay as they are."
```
