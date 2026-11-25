using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SpreadBot.Infrastructure;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies.Spread;
using SpreadBot.Models;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Tests
{
    [TestClass]
    public class StrategyQuotingTests
    {
        private const string Symbol = "NMR/BTC";
        private const decimal Bid = 0.00000100m;

        private static (DataRepository repository, MockExchange exchange) Repository(decimal feeRate = 0)
        {
            var exchange = new MockExchange();
            exchange.Mock.Setup(e => e.FeeRate).Returns(feeRate);
            return (new DataRepository(exchange.Object, new AppSettings { ResyncIntervalMs = 600000 }), exchange);
        }

        private static MarketData Market(decimal ask) =>
            new MarketData { Symbol = Symbol, BidRate = Bid, AskRate = ask, PriceTick = MockExchange.Tick };

        private static Task PlaceOrder(Func<Task<OrderData>> placeOrder) => placeOrder();

        [TestMethod]
        public async Task Buy_ImprovesTheBidByOneTick_WhenTheSpreadAllowsIt()
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 1 };
            var context = new BotContext(configuration, Market(ask: Bid + 3 * MockExchange.Tick), BotState.Buying, 0) { Balance = 0.001m };

            await new SpreadBuyStateStrategy().ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), Bid + MockExchange.Tick), Times.Once);
        }

        [TestMethod]
        public async Task Buy_JoinsTheBid_WhenTheSpreadIsOneTick()
        {
            // One tick on a 100-tick price is a 1% spread, which passes MinimumSpreadPercentage
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinimumSpreadPercentage = 0.99m };
            var context = new BotContext(configuration, Market(ask: Bid + MockExchange.Tick), BotState.Buying, 0) { Balance = 0.001m };

            await new SpreadBuyStateStrategy().ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.BuyLimit(Symbol, It.IsAny<decimal>(), Bid), Times.Once);
        }

        [TestMethod]
        public async Task Sell_JoinsTheAsk_WhenTheSpreadIsOneTick()
        {
            var (repository, exchange) = Repository();
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 0 };
            var ask = Bid + MockExchange.Tick;
            var context = new BotContext(configuration, Market(ask), BotState.Bought, 10) { BoughtPrice = Bid, BuyFilledAt = DateTime.UtcNow.AddMinutes(-1) };

            await new SpreadSellStateStrategy().ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.SellLimit(Symbol, 10, ask), Times.Once);
        }

        [TestMethod]
        public async Task Sell_KeepsTheBreakEvenFloor_WhenItIsAboveAJoinedAsk()
        {
            var (repository, exchange) = Repository(feeRate: 0.01m);
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 30 };
            var ask = Bid + MockExchange.Tick;
            var context = new BotContext(configuration, Market(ask), BotState.Bought, 10) { BoughtPrice = Bid, BuyFilledAt = DateTime.UtcNow };

            await new SpreadSellStateStrategy().ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            // 100 ticks / 0.99² = 102.03 ticks, rounded up to the tick
            exchange.Mock.Verify(e => e.SellLimit(Symbol, 10, 103 * MockExchange.Tick), Times.Once);
        }

        /// <summary>
        /// The ask fell below the bot's sell order, still above what the coins were bought at.
        /// </summary>
        [TestMethod]
        [DataRow(0.01, false)] // 101 ticks is below the 102.03-tick break-even: re-quoting would sell at a loss
        [DataRow(0.0, true)]
        public async Task SellOrderActive_RequotesBelowItsOrder_OnlyAboveBreakEven(double feeRate, bool expectCancel)
        {
            var (repository, exchange) = Repository((decimal)feeRate);
            var configuration = new SpreadConfiguration { BaseMarket = "BTC", MinutesForLoss = 30 };
            var ask = Bid + MockExchange.Tick;
            var context = new BotContext(configuration, Market(ask), BotState.SellOrderActive, 10)
            {
                BoughtPrice = Bid,
                BuyFilledAt = DateTime.UtcNow,
                CurrentOrderData = new OrderData { Id = "sell-1", Status = OrderStatus.OPEN, Limit = Bid + 5 * MockExchange.Tick }
            };

            await new SpreadSellOrderActiveStateStrategy().ProcessMarketData(repository, context, PlaceOrder, () => Task.CompletedTask);

            exchange.Mock.Verify(e => e.CancelOrder("sell-1", Symbol), expectCancel ? Times.Once() : Times.Never());
        }
    }
}
