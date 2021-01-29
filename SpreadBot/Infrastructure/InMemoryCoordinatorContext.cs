using SpreadBot.Logic;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    internal class InMemoryCoordinatorContext : ICoordinatorContext
    {
        private ConcurrentDictionary<Guid, Bot> AllocatedBotsByGuid { get; } = new ConcurrentDictionary<Guid, Bot>();
        private ConcurrentDictionary<string, decimal> DustPerMarket { get; } = new ConcurrentDictionary<string, decimal>();

        public void AddDustForMarket(string marketSymbol, decimal dust)
        {
            DustPerMarket.AddOrUpdate(marketSymbol, dust, (key, existingData) => existingData + dust);
        }

        public decimal RemoveDustForMarket(string marketSymbol)
        {
            DustPerMarket.TryRemove(marketSymbol, out var existingDust);

            return existingDust;
        }

        public Task AddBot(Bot bot)
        {
            AllocatedBotsByGuid[bot.Guid] = bot;
            return Task.CompletedTask;
        }

        public int GetBotCount()
        {
            return AllocatedBotsByGuid.Count;
        }

        public IEnumerable<Bot> GetBots()
        {
            return AllocatedBotsByGuid.Values;
        }

        public Task<bool> RemoveBot(Guid botId)
        {
            return Task.FromResult(AllocatedBotsByGuid.TryRemove(botId, out _));
        }
    }
}
