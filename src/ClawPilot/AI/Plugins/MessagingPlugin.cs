using System.ComponentModel;
using System.Text.Json;
using ClawPilot.Channels;
using ClawPilot.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Plugins;

public class MessagingPlugin
{
    private readonly ITelegramChannel _telegram;
    private readonly IServiceScopeFactory _scopeFactory;

    public MessagingPlugin(ITelegramChannel telegram, IServiceScopeFactory scopeFactory)
    {
        _telegram = telegram;
        _scopeFactory = scopeFactory;
    }

    [KernelFunction("send_message")]
    [Description("Send a text message to a Telegram chat by chat ID.")]
    public async Task<string> SendMessageAsync(
        [Description("The numeric Telegram chat ID")] long chatId,
        [Description("The message text to send")] string text,
        CancellationToken ct = default)
    {
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
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new { m.Role, Sender = m.SenderName ?? "System", m.Content, m.CreatedAt })
            .ToListAsync(ct);

        return results.Count == 0
            ? "No messages found."
            : JsonSerializer.Serialize(results);
    }
}
