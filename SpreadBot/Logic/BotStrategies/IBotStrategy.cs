﻿using SpreadBot.Infrastructure;
using SpreadBot.Infrastructure.Exchanges;
using SpreadBot.Models.Repository;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies
{
    public interface IBotStrategy
    {
        Task ProcessMarketData(BotContext botContext, Func<Func<Task<OrderData>>, Task> executeOrderFunctionCallback, Action finishWorkCallBack);
    }
}