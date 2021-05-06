using SpreadBot.Infrastructure;
using SpreadBot.Models.Repository;
using System;
using System.Threading.Tasks;

namespace SpreadBot.Logic.BotStrategies
{
    public interface IBotStateStrategy
    {
        Task ProcessMarketData(DataRepository dataRepository, BotContext botContext, Func<Func<Task<Order>>, Task> executeOrderFunctionCallback, Func<Task> finishWorkCallBack);
    }
}
