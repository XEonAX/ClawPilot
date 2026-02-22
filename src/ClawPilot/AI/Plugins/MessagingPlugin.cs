using System.ComponentModel;
using System.Text.Json;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Plugins;

public class MessagingPlugin
{
    private readonly ITelegramChannel _telegram;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClawPilotOptions _options;

    public MessagingPlugin(ITelegramChannel telegram, IServiceScopeFactory scopeFactory, ClawPilotOptions options)
    {
        _telegram = telegram;
        _scopeFactory = scopeFactory;
        _options = options;
    }

    [KernelFunction("send_message")]
    [Description("Send a text message to a Telegram chat by chat ID. Only use the chat ID from the current conversation context.")]
    public async Task<string> SendMessageAsync(
        [Description("The numeric Telegram chat ID")] long chatId,
        [Description("The message text to send")] string text,
        CancellationToken ct = default)
    {
        // Validate that the bot is allowed to send to this chat
        if (_options.AllowedChatIds.Count > 0 &&
            !_options.AllowedChatIds.Contains(chatId.ToString()))
        {
            return $"Error: Chat {chatId} is not in the allowed chat list. Do not attempt to send messages to chats not in the current conversation.";
        }

        await _telegram.SendTextAsync(chatId, text, ct: ct);
        return "Message sent successfully.";
    }

    [KernelFunction("search_messages")]
    [Description("Search conversation history for messages containing a keyword.")]
    public async Task<string> SearchMessagesAsync(
        [Description("The keyword to search for")] string keyword,
        [Description("Maximum number of results to return")] int limit = 10,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

        var results = await db.Messages
            .Where(m => EF.Functions.Like(m.Content, $"%{keyword}%"))
            .OrderByDescending(m => m.Id)
            .Take(limit)
            .Select(m => new { m.Role, Sender = m.SenderName ?? "System", m.Content, m.CreatedAt })
            .ToListAsync(ct);

        return results.Count == 0
            ? "No messages found."
            : JsonSerializer.Serialize(results);
    }
}
