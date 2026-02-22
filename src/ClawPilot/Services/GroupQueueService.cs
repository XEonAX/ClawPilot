using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClawPilot.Services;

public class GroupQueueService
{
    private readonly ConcurrentDictionary<string, Channel<Func<Task>>> _queues = new();
    private readonly ConcurrentDictionary<string, Task> _processors = new();
    private readonly ILogger<GroupQueueService> _logger;

    public GroupQueueService(ILogger<GroupQueueService> logger)
    {
        _logger = logger;
    }

    public async Task EnqueueAsync(string groupId, Func<Task> work)
    {
        var channel = _queues.GetOrAdd(groupId, _ =>
            Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true }));

        await channel.Writer.WriteAsync(work);

        _ = _processors.GetOrAdd(groupId, id => ProcessQueueAsync(id, channel));
    }

    private async Task ProcessQueueAsync(string groupId, Channel<Func<Task>> channel)
    {
        try
        {
            await foreach (var work in channel.Reader.ReadAllAsync())
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing queued work for group {GroupId}", groupId);
                }
            }
        }
        finally
        {
            _processors.TryRemove(groupId, out _);
        }
    }
}
