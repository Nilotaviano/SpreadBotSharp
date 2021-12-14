using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using File = System.IO.File;

namespace SpreadBot.Infrastructure
{
    public class TelegramBot
    {
        private readonly TelegramSettings telegramSettings;

        private readonly TelegramBotClient telegramBotClient;
        private ChatId chatId;

        public event EventHandler OnStopReceived;

        private TelegramBot()
        {
            string telegramSettingsPath = "telegramSettings.json".ToLocalFilePath();
            if (File.Exists(telegramSettingsPath))
            {
                telegramSettings = JsonConvert.DeserializeObject<TelegramSettings>(File.ReadAllText(telegramSettingsPath));

                telegramBotClient = new TelegramBotClient(telegramSettings.BotToken);
                chatId = new ChatId(telegramSettings.ChatId);

                using var cts = new CancellationTokenSource();

                // StartReceiving does not block the caller thread. Receiving is done on the ThreadPool.
                telegramBotClient.StartReceiving(new[] { UpdateType.CallbackQuery, UpdateType.ChannelPost, UpdateType.ChosenInlineResult, UpdateType.EditedChannelPost, UpdateType.EditedMessage, UpdateType.InlineQuery, UpdateType.Message, UpdateType.Poll, UpdateType.PollAnswer, UpdateType.PreCheckoutQuery, UpdateType.ShippingQuery}, cts.Token);
                telegramBotClient.OnMessage += TelegramBotClient_OnMessage;
            }
        }


        private void TelegramBotClient_OnMessage(object sender, Telegram.Bot.Args.MessageEventArgs e)
        {
            if(e.Message.Text == "/stop")
                OnStopReceived?.Invoke(this, EventArgs.Empty);
        }

        public static TelegramBot Instance { get; } = new TelegramBot();

        public async Task<Message> SendTextMessageAsync(string text)
        {
            return await telegramBotClient.SendTextMessageAsync(chatId, text);
        }
    }

    public class TelegramSettings
    {
        public int ChatId { get; set; }
        public string BotToken { get; set; }
        public Dictionary<string, decimal> ChangeThresholdPerMarket { get; set; }
    }

}
