using ClawPilot.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ClawPilot.Tests;

public class AgentOrchestratorTests
{
    [Fact]
    public void GetOrCreateHistory_ReturnsSameHistory_ForSameConversation()
    {
        using var orchestrator = CreateTestOrchestrator();

        var history1 = orchestrator.GetOrCreateHistory("chat-1", "prompt");
        var history2 = orchestrator.GetOrCreateHistory("chat-1", "prompt");

        Assert.Same(history1, history2);
    }

    [Fact]
    public void GetOrCreateHistory_CreatesDifferentHistories_ForDifferentConversations()
    {
        using var orchestrator = CreateTestOrchestrator();

        var history1 = orchestrator.GetOrCreateHistory("chat-1", "prompt");
        var history2 = orchestrator.GetOrCreateHistory("chat-2", "prompt");

        Assert.NotSame(history1, history2);
    }

    [Fact]
    public void ResetConversation_RemovesHistory()
    {
        using var orchestrator = CreateTestOrchestrator();
        orchestrator.GetOrCreateHistory("chat-1", "prompt");

        orchestrator.ResetConversation("chat-1");

        var newHistory = orchestrator.GetOrCreateHistory("chat-1", "prompt");
        Assert.Single(newHistory);
    }

    [Fact]
    public async Task RestoreSession_RebuildsHistory()
    {
        using var orchestrator = CreateTestOrchestrator();
        var messages = new List<(string Role, string Content)>
        {
            ("user", "Hello"),
            ("assistant", "Hi there!"),
            ("user", "How are you?"),
        };

        await orchestrator.RestoreSessionAsync("chat-1", "system", messages);
        var history = orchestrator.GetOrCreateHistory("chat-1", "system");

        Assert.Equal(4, history.Count);
    }

    [Fact]
    public void TrimHistory_CapsAtMaxMessages()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("system");
        for (var i = 0; i < 60; i++)
        {
            history.AddUserMessage($"user {i}");
            history.AddAssistantMessage($"assistant {i}");
        }

        Assert.Equal(121, history.Count);

        AgentOrchestrator.TrimHistory(history, 50);

        var nonSystem = history.Where(m => m.Role != AuthorRole.System).ToList();
        Assert.Equal(50, nonSystem.Count);
        Assert.Contains("user 35", nonSystem.First().Content);
    }

    [Fact]
    public void TrimHistory_PreservesSystemMessages()
    {
        var history = new ChatHistory();
        history.AddSystemMessage("system");
        for (var i = 0; i < 10; i++)
            history.AddUserMessage($"msg {i}");

        AgentOrchestrator.TrimHistory(history, 50);

        Assert.Equal(11, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
    }

    private static AgentOrchestrator CreateTestOrchestrator()
    {
        var kernel = Kernel.CreateBuilder().Build();
        var options = Microsoft.Extensions.Options.Options.Create(
            new Configuration.ClawPilotOptions
            {
                TelegramBotToken = "test",
                OpenRouterApiKey = "test",
            });
        var memory = new MemoryService(options.Value, NullLogger<MemoryService>.Instance);
        return new AgentOrchestrator(kernel, memory, options, NullLogger<AgentOrchestrator>.Instance);
    }
}
