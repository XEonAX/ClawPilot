using ClawPilot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClawPilot.Tests;

public class GroupQueueTests
{
    [Fact]
    public async Task EnqueueAsync_SerializesWithinGroup()
    {
        var service = new GroupQueueService(NullLogger<GroupQueueService>.Instance);
        var results = new List<int>();

        await service.EnqueueAsync("group1", async () =>
        {
            await Task.Delay(50);
            results.Add(1);
        });

        await service.EnqueueAsync("group1", async () =>
        {
            results.Add(2);
            await Task.CompletedTask;
        });

        await Task.Delay(200);

        Assert.Equal([1, 2], results);
    }

    [Fact]
    public async Task EnqueueAsync_ParallelAcrossGroups()
    {
        var service = new GroupQueueService(NullLogger<GroupQueueService>.Instance);
        var startTimes = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();

        await service.EnqueueAsync("groupA", async () =>
        {
            startTimes["groupA"] = DateTimeOffset.UtcNow;
            await Task.Delay(100);
        });

        await service.EnqueueAsync("groupB", async () =>
        {
            startTimes["groupB"] = DateTimeOffset.UtcNow;
            await Task.Delay(100);
        });

        await Task.Delay(300);

        Assert.True(startTimes.ContainsKey("groupA"));
        Assert.True(startTimes.ContainsKey("groupB"));

        var diff = Math.Abs((startTimes["groupA"] - startTimes["groupB"]).TotalMilliseconds);
        Assert.True(diff < 80, $"Groups should start near-simultaneously, diff was {diff}ms");
    }
}
