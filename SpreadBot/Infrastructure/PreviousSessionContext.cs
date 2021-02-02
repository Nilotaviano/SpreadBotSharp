using System.Collections.Generic;

namespace SpreadBot.Infrastructure
{
    public class PreviousSessionContext
    {
        public List<BotContext> BotContexts { get; set; }
        public Dictionary<string, decimal> DustPerMarket { get; set; }
    }
}
