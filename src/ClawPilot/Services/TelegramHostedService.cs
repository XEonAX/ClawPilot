using System.Threading.Channels;
using ClawPilot.Channels;

namespace ClawPilot.Services;

public class TelegramHostedService : BackgroundService
{
    private readonly ITelegramChannel _telegram;
    private readonly Channel<IncomingMessage> _queue;

    public TelegramHostedService(
        ITelegramChannel telegram,
        Channel<IncomingMessage> queue)
    {
        _telegram = telegram;
        _queue = queue;

        _telegram.OnMessage += async msg =>
            await _queue.Writer.WriteAsync(msg);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _telegram.StartAsync(ct);
        await Task.Delay(Timeout.Infinite, ct);
    }
}
