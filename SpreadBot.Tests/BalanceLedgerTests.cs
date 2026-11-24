using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class BalanceLedgerTests
    {
        private static (BalanceLedger ledger, List<Bot> bots, DataRepository repository) Ledger(params BalanceData[] balances)
        {
            var repository = new DataRepository(new MockExchange(balances).Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var bots = new List<Bot>();
            var context = new Mock<ICoordinatorContext>();
            context.Setup(c => c.GetBots()).Returns(() => bots);

            return (new BalanceLedger(repository, context.Object), bots, repository);
        }

        private static Bot RunningBot(DataRepository repository, BalanceLedger ledger, string symbol, decimal balance, decimal held, OrderData openOrder = null)
        {
            var market = new MarketData { Symbol = symbol, LastTradeRate = 0.0001m };
            var configuration = new SpreadConfiguration { BaseMarket = market.BaseMarket, AllocatedAmountOfBaseCurrency = 0.3m };
            var state = openOrder == null ? BotState.Buying : openOrder.Direction == OrderDirection.BUY ? BotState.BuyOrderActive : BotState.SellOrderActive;
            var context = new BotContext(configuration, market, state, held) { Balance = balance, CurrentOrderData = openOrder };

            return new Bot(repository, context, _ => { }, new BotStrategiesFactory(), ledger);
        }

        private static OrderData OpenOrder(OrderDirection direction, decimal fillQuantity = 0, decimal proceeds = 0, decimal commission = 0) =>
            new OrderData { Id = $"{direction}-{fillQuantity}", Direction = direction, Status = OrderStatus.OPEN, FillQuantity = fillQuantity, Proceeds = proceeds, Commission = commission };

        [TestMethod]
        public void UnallocatedBaseBalance_SkipsFundsLockedInOpenBuyOrders()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "BTC", Amount = 1m, Total = 1.5m });
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", balance: 0.3m, held: 0));
            bots.Add(RunningBot(repository, ledger, "XYZ-BTC", balance: 0.5m, held: 0, OpenOrder(OrderDirection.BUY)));
            bots.Add(RunningBot(repository, ledger, "ABC-ETH", balance: 0.2m, held: 0));

            Assert.AreEqual(0.7m, ledger.UnallocatedBaseBalance("BTC"));
        }

        [TestMethod]
        public void UnallocatedBaseBalance_CountsProceedsOfAPartiallyFilledSell()
        {
            // 0.05 BTC of proceeds (minus 0.001 fee) reached Available before the sell closes and the bot books them
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "BTC", Amount = 1m, Total = 1m });
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", balance: 0.01m, held: 10m, OpenOrder(OrderDirection.SELL, fillQuantity: 5m, proceeds: 0.05m, commission: 0.001m)));

            Assert.AreEqual(1m - 0.01m - 0.049m, ledger.UnallocatedBaseBalance("BTC"));
        }

        [TestMethod]
        public void ClaimedByOtherBots_SkipsTheAskingBotAndCoinsLockedInSellOrders()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "NMR", Amount = 8m, Total = 18m });
            var asking = RunningBot(repository, ledger, "NMR-BTC", balance: 0, held: 3m);
            bots.Add(asking);
            bots.Add(RunningBot(repository, ledger, "NMR-ETH", balance: 0, held: 5m));
            bots.Add(RunningBot(repository, ledger, "NMR-USDT", balance: 0, held: 10m, OpenOrder(OrderDirection.SELL)));

            Assert.AreEqual(5m, ledger.ClaimedByOtherBots("NMR", asking));
            Assert.AreEqual(0m, ledger.UnclaimedCoins("NMR"));
        }

        [TestMethod]
        public void ClaimedByOtherBots_CountsCoinsFromAPartiallyFilledBuy()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "NMR", Amount = 6m, Total = 6m });
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", balance: 0.3m, held: 1m, OpenOrder(OrderDirection.BUY, fillQuantity: 4m)));

            Assert.AreEqual(5m, ledger.ClaimedByOtherBots("NMR"));
            Assert.AreEqual(1m, ledger.UnclaimedCoins("NMR"));
        }

        [TestMethod]
        public void ACurrencyThatIsBothABaseMarketAndATradedCoin_IsClaimedBothWays()
        {
            // 5 ETH were bought by a BTC-base bot; 2 ETH are the funds of an ETH-base bot
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "ETH", Amount = 9m, Total = 9m });
            bots.Add(RunningBot(repository, ledger, "ETH-BTC", balance: 0.3m, held: 5m));
            bots.Add(RunningBot(repository, ledger, "NMR-ETH", balance: 2m, held: 0));

            Assert.AreEqual(7m, ledger.ClaimedByOtherBots("ETH"));
            Assert.AreEqual(2m, ledger.UnallocatedBaseBalance("ETH"));
            Assert.AreEqual(2m, ledger.UnclaimedCoins("ETH"));
        }

        [TestMethod]
        public void TotalValue_CountsCoinsThatAreAnotherBasesCurrencyOnlyOnce()
        {
            // 0.5 of the 1 BTC were bought by a USDT-base bot: they're worth counting toward USDT, not BTC
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "BTC", Amount = 1m, Total = 1m });
            bots.Add(RunningBot(repository, ledger, "BTC-USDT", balance: 10m, held: 0.5m));

            Assert.AreEqual(0.5m, ledger.TotalValue("BTC"));
        }

        [TestMethod]
        public void Claims_FollowAFillTheExchangeReportedBeforeTheBotBookedIt()
        {
            // The stream reported the sell filled and its proceeds are in Available; the bot hasn't processed the update yet
            var (ledger, bots, repository) = Ledger(
                new BalanceData { CurrencyAbbreviation = "BTC", Amount = 1m, Total = 1m },
                new BalanceData { CurrencyAbbreviation = "NMR", Amount = 0m, Total = 0m });
            var sell = new OrderData { Id = "sell-1", ClientOrderId = "sell-1", Direction = OrderDirection.SELL, Status = OrderStatus.OPEN };
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", balance: 0.01m, held: 10m, sell));
            repository.OrdersData["sell-1"] = new OrderData { Id = "sell-1", ClientOrderId = "sell-1", Direction = OrderDirection.SELL, Status = OrderStatus.CLOSED, FillQuantity = 10m, Proceeds = 0.05m, Commission = 0.001m };

            Assert.AreEqual(1m - 0.01m - 0.049m, ledger.UnallocatedBaseBalance("BTC"));
            Assert.AreEqual(0m, ledger.ClaimedByOtherBots("NMR"), "the sold coins are gone");
        }

        [TestMethod]
        public void UnallocatedBaseBalance_CountsABuyWhoseCancelIsInFlightAsUnlocked()
        {
            // The exchange frees a cancelled buy's funds before the bot sees the cancel's answer
            var exchange = new MockExchange(new BalanceData { CurrencyAbbreviation = "BTC", Amount = 1m, Total = 1m });
            var cancel = new TaskCompletionSource<OrderData>();
            exchange.Mock.Setup(e => e.CancelOrder("buy-1")).Returns(cancel.Task);
            var repository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            repository.StartConsumingData();

            var bots = new List<Bot>();
            var coordinatorContext = new Mock<ICoordinatorContext>();
            coordinatorContext.Setup(c => c.GetBots()).Returns(() => bots);
            var ledger = new BalanceLedger(repository, coordinatorContext.Object);

            var configuration = new SpreadConfiguration { BaseMarket = "BTC", AllocatedAmountOfBaseCurrency = 0.3m, MinimumNegotiatedAmount = 0.001m, MinimumSpreadPercentage = 1 };
            var botContext = new BotContext(configuration, new MarketData { Symbol = "NMR-BTC", Precision = 8 }, BotState.BuyOrderActive, 0)
            {
                Balance = 0.3m,
                CurrentOrderData = new OrderData { Id = "buy-1", ClientOrderId = "buy-1", Direction = OrderDirection.BUY, Status = OrderStatus.OPEN, Limit = 0.00009000m }
            };
            var bot = new Bot(repository, botContext, _ => { }, new BotStrategiesFactory(), ledger);
            bots.Add(bot);
            bot.Start();

            Assert.AreEqual(1m, ledger.UnallocatedBaseBalance("BTC"), "the open buy's funds are locked, so all of Available is free");

            // The bid moved well above the buy limit, so the strategy cancels the buy
            exchange.OnTickers(new BittrexApiTickersData
            {
                Sequence = 1,
                Deltas = new[] { new BittrexApiTickersData.Ticker { Symbol = "NMR-BTC", AskRate = 0.00010101m, BidRate = 0.00009999m } }
            });
            WaitUntil(() => bot.OrderRequestInFlight, "the cancel is sent");

            Assert.AreEqual(0.7m, ledger.UnallocatedBaseBalance("BTC"), "the cancel may already have returned the funds to Available");

            cancel.SetResult(new OrderData { Id = "buy-1", ClientOrderId = "buy-1", Direction = OrderDirection.BUY, Status = OrderStatus.CLOSED, Limit = 0.00009000m });
            WaitUntil(() => !bot.OrderRequestInFlight && botContext.CurrentOrderData == null && botContext.SemaphoreCurrentCount == 1, "the cancel is booked");

            Assert.AreEqual(0.7m, ledger.UnallocatedBaseBalance("BTC"));
        }

        private static void WaitUntil(Func<bool> condition, string description)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!condition())
            {
                if (stopwatch.ElapsedMilliseconds > 2000)
                    Assert.Fail($"Timed out waiting until {description}");
                Thread.Sleep(10);
            }
        }

        [TestMethod]
        public void TotalValue_CountsCoinsOwnedThroughPartialFills()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "BTC", Amount = 0.5m, Total = 1m });
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", balance: 0.3m, held: 1m, OpenOrder(OrderDirection.BUY, fillQuantity: 4m)));
            bots.Add(RunningBot(repository, ledger, "XYZ-BTC", balance: 0, held: 10m, OpenOrder(OrderDirection.SELL, fillQuantity: 6m)));

            Assert.AreEqual(1m + (5m + 4m) * 0.0001m, ledger.TotalValue("BTC"));
        }
    }
}
