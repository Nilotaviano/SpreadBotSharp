# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Console app (.NET 10, C#) that runs a market-making "spread" bot against the **Bittrex v3** API (REST via RestSharp, websocket via the legacy SignalR client, hub `c3`). It scans markets for wide bid/ask spreads, allocates a slice of base-currency balance to a `Bot` per market, buys at bid + 1 satoshi and sells at ask − 1 satoshi. Endpoints are hardcoded in `BittrexClient`; Bittrex Global shut down in Dec 2023, so live runs cannot succeed — work is effectively offline/test-only.

## Commands

```sh
dotnet build SpreadBot.sln
dotnet test SpreadBot.sln
dotnet test SpreadBot.Tests --filter "FullyQualifiedName~BasicBotFlowTest"   # single test
dotnet run --project SpreadBot -- C:\absolute\path\to\appsettings.json
dotnet publish SpreadBot -p:PublishProfile=FolderProfile                      # self-contained, single-file, linux-x64 (untrimmed)
```

- Tests are MSTest 4 + Moq. Besides the end-to-end `BasicBotFlowTest` (`UnitTest1.cs`), characterization tests pin the seams package upgrades can silently break: request hashing/signing, config binding (culture, defaults, hot-reload), websocket payload decoding, the RestSharp URI/body values that are signed, and the `coordinatorContext.json` round-trip.
- The build is warning-free; keep it that way: `dotnet build SpreadBot.sln --no-incremental -warnaserror`.
- No linter/formatter config exists.

## Runtime files and config

- **Settings path**: first CLI arg (default `appsettings.json`), resolved with `ToLocalFilePath()` = relative to the *executable's* directory, not the cwd. `appsettings.json` is `CopyToOutputDirectory=Never`, and under `dotnet SpreadBot.dll` the "executable" is `dotnet.exe` — so pass an absolute path.
- `appsettings.json` is tracked in git. Never put real credentials in it; use a separate untracked file passed as the arg.
- Only properties declared on `AppSettings` / `SpreadConfiguration` bind. The checked-in `appsettings.json` has stale keys that are silently ignored (top-level `MinimumPrice` / `MinimumNegotiatedAmount`, per-config `Guid`); those two minimums must be set inside each `SpreadConfiguration`.
- Settings hot-reload via `FileSystemWatcher`, but `AppSettings.Reload` only applies `MaxNumberOfBots` and `SpreadConfigurations`, then raises `Reloaded` (Coordinator regroups configs by base market).
- Files the bot writes/reads at runtime:
  - `coordinatorContext.json` — **cwd-relative** (not `ToLocalFilePath`). Allocated `BotContext`s + dust per market, saved debounced (1 s) on every `BotContext` property change; restored on startup when `TryToRestoreSession` is true.
  - `profitPerMarket.json`, `profitPerSpreadConfiguration.json` — exe-dir, written by `NetProfitRecorder`.
  - `telegramSettings.json` — exe-dir, optional (`ChatId`, `BotToken`, `ChangeThresholdPerMarket`); if present, `BalanceReporter` posts balance changes to Telegram.

## Architecture

Wiring is manual in `Program.cs` (no DI container): `BittrexClient` → `DataRepository` → `Coordinator` (with `FileCoordinatorContext`).

**Data flow (`Infrastructure/DataRepository.cs`)** — the hub everything subscribes to.
- Exchange websocket callbacks (`OnBalance/OnSummaries/OnTickers/OnOrder`) push into one `BlockingCollection` per stream; each is drained by its own background task, which applies sequence checks, merges into the `BalancesData` / `MarketsData` / `OrdersData` caches, and invokes subscribers **synchronously on that consumer thread**.
- Market updates are partial deltas (summaries vs. tickers vs. market metadata vs. CoinMarketCap quotes); `MergeMarketData` null-coalesces field by field. A new `MarketData` field must be added there or it gets dropped on merge.
- Order messages require strictly consecutive sequence numbers (gaps are dropped); a REST resync every `ResyncIntervalMs` pauses all consumers (`ManualResetEvent`), refetches everything, resets `lastOrderSequence`, then resumes.
- Subscriptions are keyed by `Guid` per handler (market symbol / currency / client order id). Subscribing fires the callback immediately if cached data exists.

**Coordinator (`Logic/Coordinator.cs`)** — subscribes to all market deltas; for each viable market (online, no notice; sorted by `GetMarketComparer`) and each `SpreadConfiguration` for that base market, checks spread/volume/price/pump/tokenized-security filters and available balance, then creates a `Bot` with `AllocatedAmountOfBaseCurrency`. Tracks available balance per base market; allocation/unallocation/balance reporting are serialized by a `SemaphoreQueue`. On unallocation it reclaims the bot's balance and records leftover held coins as "dust" for that market, which the next bot on that market inherits.

**Bot (`Logic/Bot.cs`)** — one per allocated market, state in `BotContext` (`Buying → BuyOrderActive → Bought → SellOrderActive → … → FinishedWork`).
- `ProcessMessage` is `async void` and serialized per bot by `BotContext.Semaphore` (FIFO `SemaphoreQueue`); a 1-minute timer logs if processing hangs.
- Market data goes to the strategy; **order-state transitions live in `Bot.ProcessOrderData`** (OPEN → `*OrderActive`; CLOSED → update balance/held amount, then `Bought` / `Buying` / `FinishWork` depending on `MinimumNegotiatedAmount`).
- `ApiException`s are handled per `ApiErrorType` in `ProcessMessage`. Bittrex error codes → `ApiErrorType` mapping is in `BittrexClient` (the `switch` on the error code).
- `FinishWork` unsubscribes, market-sells leftover holdings if worth more than `2 × FeeRate × MinimumNegotiatedAmount`, calls back to the Coordinator to unallocate, and records profit.

**Strategies (`Logic/BotStrategies`)** — `BotStrategiesFactory` always returns `SpreadStrategy`, which dispatches on `BotState` to an `IBotStateStrategy` (`Spread*StateStrategy`). State strategies only react to market data and place/cancel orders through the `executeOrderFunction` callback (which updates `CurrentOrderData` and feeds the result back into `ProcessOrderData`). Sell price is floored at `BoughtPrice × (1 + MinimumProfitPercentage)` until `MinutesForLoss` has elapsed since the buy filled.

**Exchange abstraction** — `IExchange` is the seam for tests/mocks, but its signatures use Bittrex DTOs (`BittrexApi*Data`); another exchange would have to adapt to those.

**Singletons with background consumers** — `Logger`, `NetProfitRecorder`, `BalanceReporter` each own a `BlockingCollection` drained by a background task. Use `Logger.Instance.LogMessage / LogError / LogUnexpectedError`, not `Console`.

## Gotchas

- `SpreadConfiguration` overrides `Equals`/`GetHashCode` over its value fields and is used as a dictionary key (`Coordinator.AllocatedMarketsPerSpreadConfiguration`) and to match profit records. Editing a config's values makes it a *different* configuration (profit tracked separately; the old one is marked inactive). `NetProfitRecorder.SpreadConfigurationNetProfit` duplicates these fields and equality — keep the two in sync when adding fields.
- Serialization is Newtonsoft.Json everywhere. Use Newtonsoft attributes; `System.Text.Json` attributes are silently ignored.
- Trimming is off on purpose: Newtonsoft, the config binder and SignalR's `dynamic` handlers need reflection. Don't re-enable `PublishTrimmed`.
- Don't enable `ImplicitUsings`: several files use `System.Timers.Timer`, which would clash with `System.Threading.Timer`.
- Package pins are deliberate: RestSharp stays on 106.x (107+ rewrote the API that request signing uses), Telegram.Bot on 15.x, and the SignalR client is the classic ASP.NET one (the Bittrex hub isn't ASP.NET Core SignalR).
- `BotContext` round-trips through `coordinatorContext.json` for session restore; renaming its public members or constructor parameters breaks restoring an existing file.
- Prices are `decimal`; use `int.Satoshi()` for tick offsets and `CeilToPrecision(market.Precision)` before sending prices/quantities to the exchange.
