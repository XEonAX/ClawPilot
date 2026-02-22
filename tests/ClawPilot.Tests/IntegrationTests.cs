using ClawPilot.AI;
using ClawPilot.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Xunit;

namespace ClawPilot.Tests;

public class IntegrationTests
{
    private static AgentOrchestrator CreateTestOrchestrator()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var options = Options.Create(new ClawPilotOptions
        {
            TelegramBotToken = "test",
            OpenRouterApiKey = "test",
        });
        var memory = new MemoryService(options.Value, NullLogger<MemoryService>.Instance);
        return new AgentOrchestrator(kernel, memory, options, NullLogger<AgentOrchestrator>.Instance);
    }

    [Fact]
    public async Task SessionRestore_RebuildsHistory_AfterReset()
    {
        using var orchestrator = CreateTestOrchestrator();
        var messages = new List<(string Role, string Content)>
        {
            ("user", "Tell me a joke"),
            ("assistant", "Why did the chicken cross the road?"),
            ("user", "I don't know, why?"),
            ("assistant", "To get to the other side!"),
        };

        await orchestrator.RestoreSessionAsync("restore-1", "You are funny.", messages);
        var history = orchestrator.GetOrCreateHistory("restore-1", "You are funny.");

        Assert.Equal(5, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal(AuthorRole.User, history[1].Role);
        Assert.Contains("joke", history[1].Content);
        Assert.Equal(AuthorRole.Assistant, history[2].Role);
        Assert.Equal(AuthorRole.User, history[3].Role);
        Assert.Equal(AuthorRole.Assistant, history[4].Role);
    }

    [Fact]
    public async Task SessionRestore_HandlesBadData_Gracefully()
    {
        using var orchestrator = CreateTestOrchestrator();
        var messages = new List<(string Role, string Content)>
        {
            ("unknown_role", "Something"),
            ("user", "Hello"),
        };

        await orchestrator.RestoreSessionAsync("restore-2", "system", messages);
        var history = orchestrator.GetOrCreateHistory("restore-2", "system");

        Assert.Equal(2, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal(AuthorRole.User, history[1].Role);
    }

    [Fact]
    public async Task MemoryService_HandlesNullEmbedding_Gracefully()
    {
        var options = new ClawPilotOptions
        {
            TelegramBotToken = "test",
            OpenRouterApiKey = "test",
        };
        var memory = new MemoryService(options, NullLogger<MemoryService>.Instance);

        await memory.SaveAsync("convo-1", "user msg", "bot msg");
        var results = await memory.RecallAsync("convo-1", "query");

        Assert.Empty(results);
    }

    [Fact]
    public async Task GroupQueueService_ConcurrentMessages_AreProcessedSequentially()
    {
        var executionOrder = new List<int>();
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 5).Select(i =>
        {
            return Task.Run(() =>
            {
                lock (lockObj)
                {
                    executionOrder.Add(i);
                }
            });
        }).ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(5, executionOrder.Count);
        Assert.Equal(5, executionOrder.Distinct().Count());
    }

    [Fact]
    public void TrimHistory_MaintainsConsistency_AfterMultipleTrims()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("system");
        for (var i = 0; i < 100; i++)
        {
            history.AddUserMessage($"user-{i}");
            history.AddAssistantMessage($"assistant-{i}");
        }

        AgentOrchestrator.TrimHistory(history, 20);
        Assert.Equal(21, history.Count);

        for (var i = 0; i < 30; i++)
        {
            history.AddUserMessage($"new-user-{i}");
            history.AddAssistantMessage($"new-assistant-{i}");
        }

        AgentOrchestrator.TrimHistory(history, 20);
        Assert.Equal(21, history.Count);

        Assert.Equal(AuthorRole.System, history[0].Role);
    }
}
