using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges.Bittrex.Models;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SpreadBot.Tests
{
    [TestClass]
    public class SellInsufficientFundsTests
    {
        private const string Symbol = "NMR-BTC";

        private static (BotContext context, MockExchange exchange, Func<bool> unallocated) BoughtBotWhoseSellIsRejected(decimal exchangeAvailable)
        {
            var exchange = new MockExchange(new BalanceData { CurrencyAbbreviation = "NMR", Amount = exchangeAvailable, Total = exchangeAvailable });
            exchange.Mock
                .Setup(e => e.SellLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>(), null))
                .ThrowsAsync(new ApiException(ApiErrorType.InsufficientFunds, "INSUFFICIENT_FUNDS"));

            var dataRepository = new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 });
            dataRepository.StartConsumingData();

            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumNegotiatedAmount = 0.0001m, MinutesForLoss = 30 };
            var context = new BotContext(configuration, new MarketData { Symbol = Symbol, Precision = 8 }, BotState.Bought, 10)
            {
                BoughtPrice = 0.0001m,
                BuyFilledAt = DateTime.UtcNow
            };

            var unallocated = false;
            new Bot(dataRepository, context, _ => unallocated = true, new BotStrategiesFactory()).Start();

            SendTicker(exchange, 1);

            return (context, exchange, () => unallocated);
        }

        private static void SendTicker(MockExchange exchange, int sequence) => exchange.OnTickers(new BittrexApiTickersData
        {
            Sequence = sequence,
            Deltas = new[] { new BittrexApiTickersData.Ticker { Symbol = Symbol, AskRate = 0.00010101m + sequence * 1.Satoshi(), BidRate = 0.00009999m, LastTradeRate = 0.0001m } }
        });

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

        private static int BalanceLookups(MockExchange exchange) =>
            exchange.Mock.Invocations.Count(i => i.Method.Name == nameof(exchange.Object.GetBalanceData));

        [TestMethod]
        public void SmallShortfall_AdjustsHeldAmountToTheExchangeBalance()
        {
            var (context, _, unallocated) = BoughtBotWhoseSellIsRejected(exchangeAvailable: 9.9m);

            WaitUntil(() => context.HeldAmount == 9.9m, "the held amount is reconciled");

            Assert.AreEqual(BotState.Bought, context.BotState);
            Assert.IsFalse(unallocated());
        }

        [TestMethod]
        public void LargeShortfall_FinishesTheBot()
        {
            var (context, exchange, unallocated) = BoughtBotWhoseSellIsRejected(exchangeAvailable: 5m);

            WaitUntil(() => unallocated(), "the bot is unallocated");

            Assert.AreEqual(BotState.FinishedWork, context.BotState);
            Assert.AreEqual(2, BalanceLookups(exchange), "one lookup at startup, one to reconcile");
            Assert.AreEqual(5m, context.HeldAmount, "only the coins the exchange holds remain to clean up or record as dust");
            exchange.Mock.Verify(e => e.SellMarket(Symbol, 5m), Times.Once);
        }

        [TestMethod]
        public void RepeatedRejectionDespiteEnoughBalance_FinishesAfterBoundedRetries()
        {
            var (context, exchange, unallocated) = BoughtBotWhoseSellIsRejected(exchangeAvailable: 10m);

            WaitUntil(() => BalanceLookups(exchange) == 2, "the first rejection is reconciled");
            Assert.AreEqual(BotState.Bought, context.BotState);

            SendTicker(exchange, 2);
            WaitUntil(() => BalanceLookups(exchange) == 3, "the second rejection is reconciled");
            Assert.AreEqual(BotState.Bought, context.BotState);

            SendTicker(exchange, 3);
            WaitUntil(() => unallocated(), "the bot gives up and is unallocated");

            Assert.AreEqual(BotState.FinishedWork, context.BotState);
            Assert.AreEqual(10m, context.HeldAmount);
            exchange.Mock.Verify(e => e.SellLimit(Symbol, It.IsAny<decimal>(), It.IsAny<decimal>(), null), Times.Exactly(3));
        }
    }
}
