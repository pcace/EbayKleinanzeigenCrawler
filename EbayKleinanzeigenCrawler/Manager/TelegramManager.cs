using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EbayKleinanzeigenCrawler.Interfaces;
using EbayKleinanzeigenCrawler.Models;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using DotNetEnv;

namespace EbayKleinanzeigenCrawler.Manager;

public class TelegramManager : StatefulManagerBase
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger _logger;

    public TelegramManager(ILogger logger, ISubscriptionPersistence subscriptionManager, IParserProvider parserProvider) : base(logger, subscriptionManager, parserProvider)
    {
        Env.Load();
        var telegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(telegramBotToken))
        {
            throw new InvalidOperationException("Telegram Bot token was not specified. Please first create a bot and specify its token.");
        }
        _botClient = new TelegramBotClient(telegramBotToken);

        _logger = logger;

        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandlePollingErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            },
            cancellationToken: new CancellationTokenSource().Token
        );
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        // Handle callback queries (button clicks)
        if (update.CallbackQuery is { } callbackQuery)
        {
            var data = callbackQuery.Data ?? "";
            var chatId = callbackQuery.Message!.Chat.Id.ToString();

            await botClient.AnswerCallbackQuery(callbackQuery.Id);

            // Handle subscription action callbacks (delete/toggle/edit by ID)
            if (data.StartsWith("action:"))
            {
                await HandleSubscriptionAction(chatId, data);
                return;
            }

            // All other callbacks flow through the regular state machine
            await ProcessCommand(chatId, data);
            return;
        }

        // Only process Message updates
        if (update.Message is not { } message)
        {
            return;
        }

        // Only process text messages
        if (message.Text is not { } messageText)
        {
            return;
        }

        await ProcessCommand(message.Chat.Id.ToString(), messageText);
    }

    /// <summary>
    /// Handles subscription action callbacks like "action:delete:guid", "action:toggle:guid", "action:edit:guid".
    /// </summary>
    private async Task HandleSubscriptionAction(string chatId, string callbackData)
    {
        var parts = callbackData.Split(':');
        if (parts.Length < 3 || !Guid.TryParse(parts[2], out var subscriptionId))
        {
            await ProcessCommand(chatId, callbackData);
            return;
        }

        var subscribers = SubscriptionPersistence.GetSubscribers();
        var subscriber = subscribers.FirstOrDefault(s => s.Id == chatId);
        if (subscriber is null)
        {
            await ProcessCommand(chatId, callbackData);
            return;
        }

        switch (parts[1])
        {
            case "delete":
                await DeleteSubscriptionById(subscriber, subscriptionId);
                break;
            case "toggle":
                await ToggleSubscriptionById(subscriber, subscriptionId);
                break;
            case "edit":
                await EditSubscriptionById(subscriber, subscriptionId);
                break;
            default:
                await ProcessCommand(chatId, callbackData);
                break;
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken __)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}", 
            _ => exception.ToString()
        };

        _logger.Error(errorMessage);
        return Task.CompletedTask;
    }

    protected override object? GetSkipKeyboard() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("⏭️ Skip", "/skip") }
        });

    protected override object? GetYesNoKeyboard() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ Yes", "yes"), InlineKeyboardButton.WithCallbackData("❌ No", "no") }
        });

    protected override object? GetMainMenuKeyboard() =>
        new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("➕ Add", "/add"), InlineKeyboardButton.WithCallbackData("📋 List", "/list") },
            new[] { InlineKeyboardButton.WithCallbackData("❓ Help", "/help") }
        });

    protected override async Task SendMessage(Subscriber subscriber, string message, bool enablePreview = true, object? replyMarkup = null)
    {
        await SendMessageTelegram(subscriber, message, enablePreview, ParseMode.Html, replyMarkup as InlineKeyboardMarkup);
    }

    private async Task SendMessageTelegram(Subscriber subscriber, string message, bool enablePreview, ParseMode parseMode, InlineKeyboardMarkup? replyMarkup = null)
    {
        var parts = SplitString(message);

        bool firstPart = true;
        foreach (string part in parts)
        {
            await _botClient.SendMessage(
                chatId: subscriber.Id,
                text: part,
                parseMode: parseMode,
                linkPreviewOptions: new LinkPreviewOptions
                {
                    IsDisabled = !enablePreview
                },
                replyMarkup: firstPart ? replyMarkup : null
            );

            Logger.Information($"Recipient: {subscriber.Id}, Message: \"{message}\"");
            firstPart = false;
        }
    }

    private static string[] SplitString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return Array.Empty<string>();
        }

        int chunkSize = 4096;
        int inputLength = input.Length;
        int numChunks = (int)Math.Ceiling((double)inputLength / chunkSize);

        var chunks = new List<string>(numChunks);

        for (int i = 0; i < numChunks; i++)
        {
            int startIndex = i * chunkSize;
            int endIndex = Math.Min(startIndex + chunkSize, inputLength);
            int chunkLength = endIndex - startIndex;

            string chunk = input.Substring(startIndex, chunkLength);
            chunks.Add(chunk);
        }

        return chunks.ToArray();
    }

    /// <summary>
    /// Builds an inline keyboard with action buttons for each subscription.
    /// Callback data format: "action:{action}:{subscriptionId}"
    /// </summary>
    private InlineKeyboardMarkup? GetSubscriptionActionKeyboard(Subscriber subscriber)
    {
        if (subscriber.Subscriptions.Count == 0) return null;

        var rows = new List<List<InlineKeyboardButton>>();
        foreach (var sub in subscriber.Subscriptions)
        {
            var toggleLabel = sub.Enabled ? "🔇" : "✅";
            var id = sub.Id.ToString();
            rows.Add(new List<InlineKeyboardButton>
            {
                InlineKeyboardButton.WithCallbackData($"{toggleLabel} {sub.Title}", $"action:toggle:{id}"),
                InlineKeyboardButton.WithCallbackData("✏️", $"action:edit:{id}"),
                InlineKeyboardButton.WithCallbackData("🗑️", $"action:delete:{id}")
            });
        }
        return new InlineKeyboardMarkup(rows);
    }

    protected override async Task DisplaySubscriptionList(Subscriber subscriber)
    {
        if (subscriber.Subscriptions.Count == 0)
        {
            await SendMessage(subscriber, "You have no subscriptions yet. Use /add to create one.", replyMarkup: GetMainMenuKeyboard());
            return;
        }

        var message = "Your subscriptions:";
        foreach (var subscription in subscriber.Subscriptions)
        {
            var statusIcon = subscription.Enabled ? "✅" : "❌";
            message += "\n\n" +
                       $"*{EscapeMarkdownV2Characters(statusIcon + " " + subscription.Title)}* \n" +
                       $"`{EscapeMarkdownV2Characters(subscription.QueryUrl.ToString())}` \n" +
                       $"Include: {EscapeMarkdownV2Characters(string.Join(", ", subscription.IncludeKeywords))} \n" +
                       $"Exclude: {EscapeMarkdownV2Characters(string.Join(", ", subscription.ExcludeKeywords))}";
        }

        message += "\n\n_Tap the buttons below to toggle, edit or delete:_";
        await SendMessageTelegram(subscriber, message, enablePreview: false, parseMode: ParseMode.MarkdownV2, replyMarkup: GetSubscriptionActionKeyboard(subscriber));
    }

    private string EscapeMarkdownV2Characters(string text)
    {
        const string markdownEscapeCharacters = @"([_\*\[\]\(\)~`>#\+\-=\|\{\}\.!])";
        return Regex.Replace(text, markdownEscapeCharacters, @"\$1");
    }
}
