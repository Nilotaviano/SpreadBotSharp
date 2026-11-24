using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
                    //A file that can't be read (e.g. it is locked) stops startup rather than skip a valid session
                    var json = File.ReadAllText(FILE_NAME);

                    //A file that can't be turned into bots is set aside: the next save would overwrite the only record of the previous session's positions
                    try
                    {
                        var savedData = JsonConvert.DeserializeObject<PreviousSessionContext>(json);

                        if (savedData?.BotContexts?.Any(c => c?.spreadConfiguration == null || c.LatestMarketData?.Symbol == null || c.CurrentOrderData is { ClientOrderId: null }) == true)
                            throw new InvalidDataException("A saved bot has no configuration, no market, or an order without a client order id");

                        return savedData;
                    }
                    catch (Exception e)
                    {
                        var preservedFile = $"{FILE_NAME}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                        Logger.Instance.LogUnexpectedError($"Could not restore the coordinator context file; moving it to {preservedFile} and starting without the previous session. Exception: {e}");
                        File.Move(FILE_NAME, preservedFile);
                    }
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

        public IEnumerable<KeyValuePair<string, decimal>> GetDustPerMarket()
        {
            return DustPerMarket.ToArray();
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
                var serializer = JsonSerializer.CreateDefault();
                var botContexts = new JArray();

                foreach (var bot in AllocatedBotsByGuid.Values)
                {
                    //Under the bot's own semaphore, so a save never catches a bot halfway through a change (e.g. SellOrderActive without its order)
                    bot.botContext.Semaphore.Wait();
                    try
                    {
                        //Finished while this save waited: it is already unallocated and its coins recorded as dust, which a restore would add again
                        if (bot.botContext.BotState != BotState.FinishedWork)
                            botContexts.Add(JToken.FromObject(bot.botContext, serializer));
                    }
                    finally
                    {
                        bot.botContext.Semaphore.Release();
                    }
                }

                var savedData = new JObject
                {
                    [nameof(PreviousSessionContext.BotContexts)] = botContexts,
                    [nameof(PreviousSessionContext.DustPerMarket)] = JToken.FromObject(new Dictionary<string, decimal>(this.DustPerMarket), serializer)
                };

                Extensions.WriteAllTextAtomic(FILE_NAME, savedData.ToString(Formatting.Indented));
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
