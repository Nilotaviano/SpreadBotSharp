using SpreadBot.Logic;
using SpreadBot.Logic.BotStrategies;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    /// <summary>
    /// Responsible for managing bots current processing
    /// </summary>
    public interface ICoordinatorContext
    {
        /// <summary>
        /// Initialize stored bots with correct parameters. This function MUST be called between program executions.
        /// </summary>
        /// <param name="dataRepository"></param>
        /// <param name="unallocateBotCallback"></param>
        /// <param name="botStrategiesFactory"></param>
        /// <returns></returns>
        IEnumerable<Bot> Initialize(DataRepository dataRepository, Action<Bot> unallocateBotCallback, BotStrategiesFactory botStrategiesFactory);

        /// <summary>
        /// Associates an amount of dust to some currency
        /// </summary>
        /// <param name="marketSymbol">The currency of the accumulated dust</param>
        /// <param name="dust">The dust value</param>
        void AddDustForMarket(string marketSymbol, decimal dust);

        /// <summary>
        /// Removes the dust from the associated symbol
        /// </summary>
        /// <param name="marketSymbol">The currency of the accumulated dust</param>
        /// <returns>The accumulated dust value for the currency</returns>
        decimal RemoveDustForMarket(string marketSymbol);

        /// <summary>
        /// Gets the number of bots stored
        /// </summary>
        int GetBotCount();

        /// <summary>
        /// Gets all stored bots
        /// </summary>
        /// <returns></returns>
        IEnumerable<Bot> GetBots();

        /// <summary>
        /// Adds a new bot
        /// </summary>
        /// <param name="bot">The bot about to start processing</param>
        void AddBot(Bot bot);

        /// <summary>
        /// Removes a bot by its id
        /// </summary>
        /// <param name="botId">The id of the bot to remove</param>
        /// <returns>True if a bot with the equivalent id has been removed, false otherwise</returns>
        bool RemoveBot(Guid botId);
    }
}
