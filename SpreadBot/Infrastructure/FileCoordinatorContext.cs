using Newtonsoft.Json;
using System.Linq;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    internal class FileCoordinatorContext : ICoordinatorContext
    {
        private const string FILE_NAME = "coordinatorContext.json";

        private ConcurrentDictionary<Guid, Bot> AllocatedBotsByGuid { get; } = new ConcurrentDictionary<Guid, Bot>();
        private ConcurrentDictionary<string, decimal> DustPerMarket { get; set; } = new ConcurrentDictionary<string, decimal>();

        public async Task<IEnumerable<Bot>> Initialize(DataRepository dataRepository, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory)
        {
            if (File.Exists(FILE_NAME))
            {
                var savedData = JsonConvert.DeserializeObject<SavedData>(File.ReadAllText(FILE_NAME));

                if (savedData.BotContexts != null)
                {
                    foreach (var botContext in savedData.BotContexts)
                    {
                        var bot = new Bot(dataRepository, botContext, unallocateBotCallback, botStrategiesFactory);
                        AllocatedBotsByGuid.TryAdd(bot.Guid, bot);
                    }
                }

                if (savedData.DustPerMarket != null)
                    DustPerMarket = new ConcurrentDictionary<string, decimal>(savedData.DustPerMarket);
            }

            return AllocatedBotsByGuid.Values;
        }

        public async Task AddDustForMarket(string marketSymbol, decimal dust)
        {
            DustPerMarket.AddOrUpdate(marketSymbol, dust, (key, existingData) => existingData + dust);
            await SaveData();
        }

        public async Task<decimal> RemoveDustForMarket(string marketSymbol)
        {
            DustPerMarket.TryRemove(marketSymbol, out var existingDust);

            await SaveData();

            return existingDust;
        }

        public async Task AddBot(Bot bot)
        {
            AllocatedBotsByGuid[bot.Guid] = bot;

            await SaveData();
        }

        public int GetBotCount()
        {
            return AllocatedBotsByGuid.Count;
        }

        public IEnumerable<Bot> GetBots()
        {
            return AllocatedBotsByGuid.Values;
        }

        public async Task<bool> RemoveBot(Guid botId)
        {
            var removed = AllocatedBotsByGuid.TryRemove(botId, out _);

            if (removed)
                await SaveData();

            return removed;
        }

        private Task SaveData()
        {
            var savedData = new SavedData
            {
                BotContexts = new List<BotContext>(AllocatedBotsByGuid.Values.Select(b => b.botContext)),
                DustPerMarket = new Dictionary<string, decimal>(this.DustPerMarket)
            };

            return File.WriteAllTextAsync(FILE_NAME, JsonConvert.SerializeObject(savedData, Formatting.Indented));
        }

        public class SavedData
        {
            public List<BotContext> BotContexts { get; set; }
            public Dictionary<string, decimal> DustPerMarket { get; set; }
        }
    }
}
