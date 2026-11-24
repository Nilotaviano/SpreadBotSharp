using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System.Collections.Generic;

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
        public void CoinsHeldByOtherBots_SkipsTheAskingBotAndCoinsLockedInSellOrders()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "NMR", Amount = 8m, Total = 18m });
            var asking = RunningBot(repository, ledger, "NMR-BTC", balance: 0, held: 3m);
            bots.Add(asking);
            bots.Add(RunningBot(repository, ledger, "NMR-ETH", balance: 0, held: 5m));
            bots.Add(RunningBot(repository, ledger, "NMR-USDT", balance: 0, held: 10m, OpenOrder(OrderDirection.SELL)));

            Assert.AreEqual(5m, ledger.CoinsHeldByOtherBots("NMR", asking));
            Assert.AreEqual(0m, ledger.UnclaimedCoins("NMR"));
        }

        [TestMethod]
        public void CoinsHeldByOtherBots_CountsCoinsFromAPartiallyFilledBuy()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "NMR", Amount = 6m, Total = 6m });
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", balance: 0.3m, held: 1m, OpenOrder(OrderDirection.BUY, fillQuantity: 4m)));

            Assert.AreEqual(5m, ledger.CoinsHeldByOtherBots("NMR"));
            Assert.AreEqual(1m, ledger.UnclaimedCoins("NMR"));
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
