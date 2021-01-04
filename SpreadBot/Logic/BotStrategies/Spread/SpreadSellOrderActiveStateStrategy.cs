﻿using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies.Spread
{
    public class SpreadSellOrderActiveStateStrategy : IBotStateStrategy
    {
        public async Task ProcessMarketData(BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack)
        {
            if (botContext.currentOrderData.Limit - botContext.latestMarketData.AskRate >= botContext.spreadConfiguration.SpreadThresholdBeforeCancelingCurrentOrder)
            {
                //cancel order and switch to BotState.Sell
                //TODO: I think we should rate limit how often we cancel orders here
                bool canSellAtLoss = botContext.buyStopwatch.Elapsed.TotalMinutes > botContext.spreadConfiguration.MinutesForLoss;
                bool currentAskAboveMinimumProfitTarget = botContext.latestMarketData.AskRate > botContext.boughtPrice * (1 + botContext.spreadConfiguration.MinimumProfitPercentage / 100);

                if (canSellAtLoss || currentAskAboveMinimumProfitTarget)
                    await executeOrderFunctionCallback(async () => await botContext.exchange.CancelOrder(botContext.currentOrderData.Id));
            }
        }
    }
}
