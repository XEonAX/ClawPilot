using System.Collections.Concurrent;
using ClawPilot.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ClawPilot.Channels;

public class TelegramChannel : ITelegramChannel
{
    private readonly TelegramBotClient _bot;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly ClawPilotOptions _options;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastSendTimes = new();

    public event Func<IncomingMessage, Task>? OnMessage;

    public TelegramChannel(
        IOptions<ClawPilotOptions> options,
        ILogger<TelegramChannel> logger)
    {
        _options = options.Value;
        _logger = logger;
        _bot = new TelegramBotClient(_options.TelegramBotToken);
    }

    public Task StartAsync(CancellationToken ct)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true,
        };

        _bot.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            ct);

        _logger.LogInformation("Telegram polling started");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message)
            return;

        if (!IsAllowed(message))
        {
            _logger.LogDebug("Ignored message from unauthorized chat {ChatId}", message.Chat.Id);
            return;
        }

        var isGroup = message.Chat.Type is ChatType.Group or ChatType.Supergroup;

        if (isGroup && !ShouldRespondInGroup(message))
            return;

        var incoming = new IncomingMessage(
            ChatId: message.Chat.Id,
            MessageId: message.MessageId,
            Text: text,
            SenderName: message.From?.FirstName ?? "Unknown",
            SenderId: message.From?.Id.ToString() ?? "0",
            IsGroup: isGroup,
            GroupName: isGroup ? message.Chat.Title : null,
            Timestamp: message.Date
        );

        if (OnMessage is not null)
            await OnMessage.Invoke(incoming);
    }

    public bool ShouldRespondInGroup(Message message)
    {
        if (message.Chat.Type is ChatType.Private)
            return true;

        if (message.Entities?.Any(e => e.Type == MessageEntityType.Mention) == true)
        {
            var botUsername = _options.BotUsername;
            return message.Text?.Contains(botUsername, StringComparison.OrdinalIgnoreCase) == true;
        }

        if (message.ReplyToMessage?.From?.IsBot == true)
            return true;

        return false;
    }

    private bool IsAllowed(Message message)
    {
        if (_options.AllowedChatIds.Count == 0)
            return true;

        var chatId = message.Chat.Id.ToString();
        return _options.AllowedChatIds.Contains(chatId);
    }

    private bool IsAllowedChatId(long chatId)
    {
        if (_options.AllowedChatIds.Count == 0)
            return true;

        return _options.AllowedChatIds.Contains(chatId.ToString());
    }

    public async Task SendTextAsync(
        long chatId, string text, long? replyToMessageId = null, CancellationToken ct = default)
    {
        if (!IsAllowedChatId(chatId))
        {
            _logger.LogDebug("Ignored message to unauthorized chat {ChatId}", chatId);
            return;
        }
        try
        {
            foreach (var chunk in ChunkText(text, _options.MaxResponseLength))
            {
                await ThrottlePerChatAsync(chatId, ct);

                var replyParams = replyToMessageId.HasValue
                    ? new Telegram.Bot.Types.ReplyParameters { MessageId = (int)replyToMessageId.Value }
                    : null;

                await _bot.SendMessage(
                    chatId: chatId,
                    text: chunk,
                    parseMode: ParseMode.Markdown,
                    replyParameters: replyParams,
                    cancellationToken: ct);

                replyToMessageId = null;
            }
        }
        catch (ApiRequestException apiEx)
        {
            _logger.LogError(apiEx,
                "Telegram send failed for chat {ChatId}: {ErrorCode} {Description}",
                chatId, apiEx.ErrorCode, apiEx.Message);
        }
    }

    private async Task ThrottlePerChatAsync(long chatId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastSendTimes.TryGetValue(chatId, out var lastSend))
        {
            var elapsed = now - lastSend;
            if (elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, ct);
        }
        _lastSendTimes[chatId] = DateTimeOffset.UtcNow;
    }

    public async Task SendTypingAsync(long chatId, CancellationToken ct = default)
    {
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
    }

    private Task HandleErrorAsync(
        ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }

    internal static IEnumerable<string> ChunkText(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        if (text.Length <= maxLen)
        {
            yield return text;
            yield break;
        }

        var offset = 0;
        while (offset < text.Length)
        {
            var remaining = text.Length - offset;
            if (remaining <= maxLen)
            {
                yield return text[offset..];
                yield break;
            }

            var end = offset + maxLen;
            var slice = text[offset..end];

            var splitAt = slice.LastIndexOf("\n\n", StringComparison.Ordinal);
            if (splitAt < maxLen / 4)
                splitAt = slice.LastIndexOf('\n');
            if (splitAt < maxLen / 4)
                splitAt = maxLen;

            yield return text[offset..(offset + splitAt)];
            offset += splitAt;

            while (offset < text.Length && text[offset] == '\n')
                offset++;
        }
    }
}
