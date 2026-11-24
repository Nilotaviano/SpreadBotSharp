using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
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

        private static Bot RunningBot(DataRepository repository, BalanceLedger ledger, string symbol, BotState state, decimal balance, decimal held)
        {
            var market = new MarketData { Symbol = symbol };
            var configuration = new SpreadConfiguration { BaseMarket = market.BaseMarket, AllocatedAmountOfBaseCurrency = 0.3m };
            var context = new BotContext(configuration, market, state, held) { Balance = balance };
            return new Bot(repository, context, _ => { }, new BotStrategiesFactory(), ledger);
        }

        [TestMethod]
        public void UnallocatedBaseBalance_SubtractsBotsExceptThoseWithAnOpenBuyOrder()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "BTC", Amount = 1m, Total = 1.5m });
            bots.Add(RunningBot(repository, ledger, "NMR-BTC", BotState.Buying, 0.3m, 0));
            bots.Add(RunningBot(repository, ledger, "XYZ-BTC", BotState.BuyOrderActive, 0.5m, 0));
            bots.Add(RunningBot(repository, ledger, "ABC-ETH", BotState.Buying, 0.2m, 0));

            Assert.AreEqual(0.7m, ledger.UnallocatedBaseBalance("BTC"));
        }

        [TestMethod]
        public void CoinsHeldByOtherBots_SkipsTheAskingBotAndCoinsLockedInSellOrders()
        {
            var (ledger, bots, repository) = Ledger(new BalanceData { CurrencyAbbreviation = "NMR", Amount = 8m, Total = 18m });
            var asking = RunningBot(repository, ledger, "NMR-BTC", BotState.Bought, 0, 3m);
            bots.Add(asking);
            bots.Add(RunningBot(repository, ledger, "NMR-ETH", BotState.Bought, 0, 5m));
            bots.Add(RunningBot(repository, ledger, "NMR-USDT", BotState.SellOrderActive, 0, 10m));

            Assert.AreEqual(5m, ledger.CoinsHeldByOtherBots("NMR", asking));
            Assert.AreEqual(0m, ledger.UnclaimedCoins("NMR"));
        }
    }
}
