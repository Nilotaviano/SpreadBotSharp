# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Console app (.NET 10, C#) that runs a market-making "spread" bot on any exchange [CCXT](https://github.com/ccxt/ccxt) supports with websockets (official `ccxt` NuGet package, `ccxt.pro` streams); the first target is Binance spot (`ExchangeId`, `UseSandbox` for its testnet). It scans markets for wide bid/ask spreads, allocates a slice of quote-currency balance to a `Bot` per market, buys at bid + 1 tick and sells at ask − 1 tick with post-only orders.

## Commands

```sh
dotnet build SpreadBot.sln
dotnet test SpreadBot.sln
dotnet test SpreadBot.Tests --filter "FullyQualifiedName~BasicBotFlowTest"   # single test
dotnet run --project SpreadBot -- C:\absolute\path\to\appsettings.json
dotnet publish SpreadBot -p:PublishProfile=FolderProfile                      # self-contained, single-file, linux-x64 (untrimmed)
```

- Tests are MSTest 4 + Moq. Besides the end-to-end `BasicBotFlowTest` (`UnitTest1.cs`), tests pin config binding (culture, defaults, hot-reload), the CCXT → model mapping (`CcxtMappingTests`: orders, fees by currency, statuses, exception → `ApiErrorType`), and the `coordinatorContext.json` round-trip. Nothing talks to a real exchange; `MockExchange` captures the stream callbacks so tests push updates.
- The build is warning-free; keep it that way: `dotnet build SpreadBot.sln --no-incremental -warnaserror`.
- No linter/formatter config exists.

## Runtime files and config

- **Settings path**: first CLI arg (default `appsettings.json`), resolved with `ToLocalFilePath()` = relative to the *executable's* directory, not the cwd. `appsettings.json` is `CopyToOutputDirectory=Never`, and under `dotnet SpreadBot.dll` the "executable" is `dotnet.exe` — so pass an absolute path.
- `appsettings.json` is tracked in git. Never put real credentials in it; use a separate untracked file passed as the arg.
- Only properties declared on `AppSettings` / `SpreadConfiguration` bind; unknown keys are silently ignored. `MinimumPrice` / `MinimumNegotiatedAmount` live inside each `SpreadConfiguration`. The checked-in `appsettings.json` targets the Binance testnet with empty keys.
- Settings hot-reload via `FileSystemWatcher`, but `AppSettings.Reload` only applies `MaxNumberOfBots` and `SpreadConfigurations`, then raises `Reloaded` (Coordinator regroups configs by base market).
- Files the bot writes/reads at runtime:
  - `coordinatorContext.json` — **cwd-relative** (not `ToLocalFilePath`). Allocated `BotContext`s + dust per market, saved debounced (1 s) on every `BotContext` property change; restored on startup when `TryToRestoreSession` is true.
  - `profitPerMarket.json`, `profitPerSpreadConfiguration.json` — exe-dir, written by `NetProfitRecorder`.
  - `telegramSettings.json` — exe-dir, optional (`ChatId`, `BotToken`, `ChangeThresholdPerMarket`); if present, `BalanceReporter` posts balance changes to Telegram.

## Architecture

Wiring is manual in `Program.cs` (no DI container): `CcxtExchange` → `DataRepository` → `Coordinator` (with `FileCoordinatorContext`).

**Data flow (`Infrastructure/DataRepository.cs`)** — the hub everything subscribes to.
- Exchange stream callbacks (`OnBalance/OnMarketData/OnOrder`) push into one `BlockingCollection` per stream; each is drained by its own background task, which applies sequence checks, merges into the `BalancesData` / `MarketsData` / `OrdersData` caches, and invokes subscribers **synchronously on that consumer thread**.
- Market updates are partial (tickers vs. market metadata such as `PriceTick`/status); `MergeMarketData` null-coalesces field by field. A new `MarketData` field must be added there or it gets dropped on merge.
- A closed order is final: a late OPEN update for it is ignored.
- A REST resync — every `ResyncIntervalMs`, or right after any stream failed and recovered (`OnStreamInterrupted`) — pauses all consumers (`ManualResetEvent`), refetches balances, markets, tickers and open orders, and fetches each order a bot is tracking that the exchange no longer lists as open (it filled or was cancelled while the stream was down); only one runs at a time.
- Consumer loops run on dedicated threads (`TaskCreationOptions.LongRunning`); don't start blocking loops with `Task.Run`, they pin thread-pool threads.
- Subscriptions are keyed by `Guid` per handler (market symbol / currency / client order id). Subscribing fires the callback immediately if cached data exists.

**Coordinator (`Logic/Coordinator.cs`)** — subscribes to all market deltas; for each viable market (online, no notice; sorted by `GetMarketComparer`) and each `SpreadConfiguration` for that base market, checks spread/volume/price/pump/tokenized-security filters and available balance, then creates a `Bot` with `AllocatedAmountOfBaseCurrency`. Evaluation, allocation and unallocation are serialized by one lock (summaries and tickers arrive on different threads). On unallocation it records leftover held coins as "dust" for that market, which the next bot on that market inherits, capped at the coins no other bot claims minus the dust recorded on the coin's other markets; recorded dust beyond that isn't on the exchange and is dropped.

**BalanceLedger (`Logic/BalanceLedger.cs`)** — the only place that decides how much money is free. Exchange balances (`BalanceData.Amount` = Available, `Total`) are the source of truth; bots' `Balance`/`HeldAmount` say which part is theirs. **The exchange account is assumed to be dedicated to the bot** (the user's decision): every coin no bot claims counts as free, so it can be allocated, inherited as dust, or market-sold by a finishing bot. Don't run it on an account that also holds coins the bot must not touch. It keys on each bot's order (`CurrentOrderData`), not `BotState`: funds locked in an open order aren't in Available, and an open order's partial fills (bought coins, sell proceeds) are already in Available before the bot books them on close — so `Bot.ProcessOrderData` keeps `CurrentOrderData` updated on every OPEN update. Balances can run ahead of the bot's view, so it uses the newer of `CurrentOrderData` and `DataRepository.OrdersData` (an order the exchange reports closed counts as booked), and while `Bot.OrderRequestInFlight` it treats the order's locked funds as possibly unlocked (a cancel frees them before the bot sees the answer; a failed request keeps the flag set until the order's state is next applied). A currency is claimed both as a base market (bots' funds) and as a traded coin (bots' coins), e.g. BTC with BTC and USDT bases. Used for allocation, inherited dust, balance reporting, and a bot's reconcile after a rejected sell.

**Bot (`Logic/Bot.cs`)** — one per allocated market, state in `BotContext` (`Buying → BuyOrderActive → Bought → SellOrderActive → … → FinishedWork`).
- `ProcessMessage` is `async void` and serialized per bot by `BotContext.Semaphore` (FIFO `SemaphoreQueue`); a 1-minute timer logs if processing hangs.
- Market data goes to the strategy; **order-state transitions live in `Bot.ProcessOrderData`** (OPEN → `*OrderActive`; CLOSED → update balance/held amount, then `Bought` / `Buying` / `FinishWork` depending on `MinimumNegotiatedAmount`).
- `ApiException`s are handled per `ApiErrorType` in `HandleApiException`. CCXT exception → `ApiErrorType` mapping is `CcxtExchange.ToApiException`; order the arms most-specific first (e.g. `RateLimitExceeded` derives from `NetworkError` in this ccxt version, not `DDoSProtection`).
- `FinishWork` is idempotent: it unsubscribes, market-sells leftover holdings if worth more than `2 × FeeRate × MinimumNegotiatedAmount`, calls back to the Coordinator to unallocate, and records profit; each step runs even if an earlier one fails. Messages queued behind it see `FinishedWork` and return.
- `Start` and `ProcessMessage` are `async void` entry points: keep all their work inside the `Task`-returning `StartAsync` / `ProcessMessageAsync`, which sit behind a catch-all — anything escaping an `async void` terminates the process.
- A sell rejected with `INSUFFICIENT_FUNDS` reconciles `HeldAmount` against a fresh REST balance (minus other bots' coins): within 2% it sells what's there; beyond that it retries, since the coins may not be settled yet. After 3 consecutive rejections it finishes, market-selling only what the exchange shows (`FinishWork(maxCoinsToSell)`); the rest stays held and is recorded as dust.

**Strategies (`Logic/BotStrategies`)** — `BotStrategiesFactory` always returns `SpreadStrategy`, which dispatches on `BotState` to an `IBotStateStrategy` (`Spread*StateStrategy`). State strategies only react to market data and place/cancel orders through the `executeOrderFunction` callback (which updates `CurrentOrderData` and feeds the result back into `ProcessOrderData`). Sell price is floored at `BoughtPrice × (1 + MinimumProfitPercentage)` until `MinutesForLoss` has elapsed since the buy filled.

**Exchange abstraction (`Infrastructure/Exchanges`)** — `IExchange` uses only the app's models (`MarketData`, `OrderData`, `BalanceData`); symbols are CCXT's `BASE/QUOTE` (SpreadBot calls the quote currency the "base market": BTC in `NMR/BTC`). Orders are addressed by `(id, symbol)` because many exchanges require the symbol. `CcxtExchange` holds the base `ccxt.Exchange` built from `ccxt.pro.{ExchangeId}`; its watch loops reconnect with a delay and raise `OnStreamInterrupted` on recovery. Limit orders are post-only (a spread bot must never take). A cancel re-fetches the order, so a cancel that races a fill reports the real final state. Fees are split by currency: `Commission` (quote) and `CoinCommission` (taken from the coin, e.g. Binance buys without BNB), and `HeldAmount` is booked net of `CoinCommission`.

**Singletons with background consumers** — `Logger`, `NetProfitRecorder`, `BalanceReporter` each own a `BlockingCollection` drained by a background task. Use `Logger.Instance.LogMessage / LogError / LogUnexpectedError`, not `Console`.

## Gotchas

- `SpreadConfiguration` overrides `Equals`/`GetHashCode` over its value fields (all but `AvoidTokenizedSecurities`) and is used as a dictionary key (`Coordinator.AllocatedMarketsPerSpreadConfiguration`) and to match profit records. Editing a config's values makes it a *different* configuration (profit tracked separately; the old one is marked inactive). `NetProfitRecorder.SpreadConfigurationNetProfit` duplicates these fields and equality — keep the two in sync when adding fields.
- Serialization is Newtonsoft.Json everywhere. Use Newtonsoft attributes; `System.Text.Json` attributes are silently ignored.
- Trimming is off on purpose: Newtonsoft, the config binder and CCXT need reflection (`CcxtExchange` creates the exchange class by name). Don't re-enable `PublishTrimmed`.
- Don't enable `ImplicitUsings`: several files use `System.Timers.Timer`, which would clash with `System.Threading.Timer`.
- Telegram.Bot is pinned on 15.x. CCXT's C# models use `double`; convert to `decimal` at the adapter boundary (`CcxtExchange.ToDecimal`) and never let doubles into the bot's accounting.
- `BotContext` round-trips through `coordinatorContext.json` for session restore; renaming its public members or constructor parameters breaks restoring an existing file.
- Prices are `decimal`; move prices by `MarketData.PriceTick` (from CCXT market precision) and round with `CeilToTick`. A market without a tick isn't quoted. Amounts are left to CCXT, which truncates them to the market's step size.
