using Newtonsoft.Json;
using System.Linq;
using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Timers;

namespace SpreadBot.Infrastructure
{
    internal class FileCoordinatorContext : ICoordinatorContext
    {
        private const string FILE_NAME = "coordinatorContext.json";

        private readonly SemaphoreQueue contextSemaphore = new SemaphoreQueue(1, 1);
        private readonly Timer saveDataTPSController;

        public FileCoordinatorContext()
        {
            saveDataTPSController = new Timer(TimeSpan.FromSeconds(1).TotalMilliseconds);
            saveDataTPSController.AutoReset = false;
            saveDataTPSController.Stop();
            saveDataTPSController.Elapsed += (sender, args) => SaveData();
        }

        private ConcurrentDictionary<Guid, Bot> AllocatedBotsByGuid { get; } = new ConcurrentDictionary<Guid, Bot>();
        private ConcurrentDictionary<string, decimal> DustPerMarket { get; set; } = new ConcurrentDictionary<string, decimal>();

        public PreviousSessionContext GetPreviousSessionContext()
        {
            if (File.Exists(FILE_NAME))
            {
                contextSemaphore.Wait();
                try
                {
                    var savedData = JsonConvert.DeserializeObject<PreviousSessionContext>(File.ReadAllText(FILE_NAME));

                    return savedData;
                }
                catch (Exception e)
                {
                    Logger.Instance.LogError($"Could not read coordinator context file. Exception: {e}");
                }
                finally
                {
                    contextSemaphore.Release();
                }
            }

            return null;
        }

        public void AddDustForMarket(string marketSymbol, decimal dust)
        {
            UpdateDustValue(marketSymbol, dust);
            RequestSaveData();
        }

        public void AddDustForMarkets(Dictionary<string, decimal> dustPerMarket)
        {
            if (dustPerMarket == null)
                return;

            foreach (var dustEntry in dustPerMarket)
                UpdateDustValue(dustEntry.Key, dustEntry.Value);
            
            RequestSaveData();
        }

        public decimal RemoveDustForMarket(string marketSymbol)
        {
            DustPerMarket.TryRemove(marketSymbol, out var existingDust);

            RequestSaveData();

            return existingDust;
        }

        public void AddBot(Bot bot)
        {
            AllocatedBotsByGuid[bot.Guid] = bot;
            ListenBotEvents(bot);

            RequestSaveData();
        }

        public int GetBotCount()
        {
            return AllocatedBotsByGuid.Count;
        }

        public IEnumerable<Bot> GetBots()
        {
            return AllocatedBotsByGuid.Values;
        }

        public bool RemoveBot(Guid botId)
        {
            var removed = AllocatedBotsByGuid.TryRemove(botId, out var bot);

            if (removed)
            {
                RequestSaveData();
                StopListeningBotEvents(bot);
            }

            return removed;
        }

        private void UpdateDustValue(string marketSymbol, decimal dust)
        {
            DustPerMarket.AddOrUpdate(marketSymbol, dust, (key, existingData) => existingData + dust);
        }

        private void ListenBotEvents(Bot bot)
        {
            bot.botContext.ContextChanged += BotContext_ContextChanged;
        }

        private void StopListeningBotEvents(Bot bot)
        {
            bot.botContext.ContextChanged -= BotContext_ContextChanged;
        }

        private void BotContext_ContextChanged(object sender, EventArgs e)
        {
            RequestSaveData();
        }

        private void RequestSaveData()
        {
            // If the timer is enabled it's about to save the whole data so instead of delaying the save we just wait for it
            if (saveDataTPSController.Enabled)
                return;

            saveDataTPSController.Stop();
            saveDataTPSController.Start();
        }

        private void SaveData()
        {
            contextSemaphore.Wait();
            try
            {
                var savedData = new PreviousSessionContext
                {
                    BotContexts = new List<BotContext>(AllocatedBotsByGuid.Values.Select(b => b.botContext)),
                    DustPerMarket = new Dictionary<string, decimal>(this.DustPerMarket)
                };

                File.WriteAllText(FILE_NAME, JsonConvert.SerializeObject(savedData, Formatting.Indented));
            } catch (Exception e)
            {
                Logger.Instance.LogError($"Could not save coordinator context. Exception: {e}");
            } finally
            {
                contextSemaphore.Release();
            }
        }
    }
}
