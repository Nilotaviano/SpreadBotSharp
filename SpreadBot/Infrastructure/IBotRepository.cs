using SpreadBot.Logic;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace SpreadBot.Infrastructure
{
    /// <summary>
    /// Responsible for managing bots current processing
    /// </summary>
    public interface IBotRepository
    {
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
        Task AddBot(Bot bot);

        /// <summary>
        /// Removes a bot by its id
        /// </summary>
        /// <param name="botId">The id of the bot to remove</param>
        /// <returns>True if a bot with the equivalent id has been removed, false otherwise</returns>
        Task<bool> RemoveBot(Guid botId);
    }
}
