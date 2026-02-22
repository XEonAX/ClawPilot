namespace ClawPilot.Channels;

public interface ITelegramChannel
{
    Task StartAsync(CancellationToken ct);
    Task SendTextAsync(long chatId, string text, long? replyToMessageId = null, CancellationToken ct = default);
    Task SendTypingAsync(long chatId, CancellationToken ct = default);
    event Func<IncomingMessage, Task>? OnMessage;
}
