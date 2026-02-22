using System.Threading.Channels;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using Microsoft.Extensions.Options;

namespace ClawPilot.Services;

public class TelegramHostedService : BackgroundService
{
    private readonly ITelegramChannel _telegram;
    private readonly Channel<IncomingMessage> _queue;
    private readonly ClawPilotOptions _options;
    private readonly ILogger<TelegramHostedService> _logger;

    public TelegramHostedService(
        ITelegramChannel telegram,
        Channel<IncomingMessage> queue,
        IOptions<ClawPilotOptions> options,
        ILogger<TelegramHostedService> logger)
    {
        _telegram = telegram;
        _queue = queue;
        _options = options.Value;
        _logger = logger;

        _telegram.OnMessage += async msg =>
            await _queue.Writer.WriteAsync(msg);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _telegram.StartAsync(ct);

        foreach (var chatIdStr in _options.AllowedChatIds)
        {
            if (long.TryParse(chatIdStr, out var chatId))
            {
                try
                {
                    await _telegram.SendTextAsync(chatId, "🟢 ClawPilot is up and running!", ct: ct);
                    _logger.LogInformation("Sent startup notification to chat {ChatId}", chatId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send startup notification to chat {ChatId}", chatId);
                }
            }
        }

        await Task.Delay(Timeout.Infinite, ct);
    }
}
