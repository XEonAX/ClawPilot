using System.Collections.Concurrent;
using ClawPilot.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace ClawPilot.AI;

public class AgentOrchestrator : IDisposable
{
    private readonly Kernel _kernel;
    private readonly MemoryService _memory;
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly ClawPilotOptions _options;
    private IChatCompletionService? _chatService;

    private readonly ConcurrentDictionary<string, ChatHistory> _histories = new();

    public AgentOrchestrator(
        Kernel kernel,
        MemoryService memory,
        IOptions<ClawPilotOptions> options,
        ILogger<AgentOrchestrator> logger)
    {
        _kernel = kernel;
        _memory = memory;
        _options = options.Value;
        _logger = logger;
    }

    private IChatCompletionService ChatService =>
        _chatService ??= _kernel.GetRequiredService<IChatCompletionService>();

    public ChatHistory GetOrCreateHistory(string conversationId, string systemPrompt)
    {
        return _histories.GetOrAdd(conversationId, _ =>
        {
            var history = new ChatHistory();
            history.AddSystemMessage(systemPrompt);
            _logger.LogInformation("Created chat history for conversation {Id}", conversationId);
            return history;
        });
    }

    internal static void TrimHistory(ChatHistory history, int maxMessages = 50)
    {
        var nonSystem = history.Where(m => m.Role != AuthorRole.System).ToList();
        if (nonSystem.Count <= maxMessages)
            return;

        var toRemove = nonSystem.Take(nonSystem.Count - maxMessages).ToList();
        foreach (var msg in toRemove)
            history.Remove(msg);
    }

    public virtual async Task<string> SendMessageAsync(
        string conversationId,
        string userMessage,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var history = GetOrCreateHistory(conversationId, systemPrompt);

        var memories = await _memory.RecallAsync(conversationId, userMessage, limit: 5, ct);
        if (memories.Count > 0)
        {
            var memoryContext = string.Join("\n", memories.Select(m => $"- {m}"));
            history.AddSystemMessage($"Relevant context from memory:\n{memoryContext}");
        }

        history.AddUserMessage(userMessage);

        TrimHistory(history);

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            MaxTokens = _options.MaxResponseTokens,
        };

        var result = await ChatService.GetChatMessageContentAsync(
            history, settings, _kernel, ct);

        var response = result.Content ?? "[No response]";
        history.AddAssistantMessage(response);

        await _memory.SaveAsync(conversationId, userMessage, response, ct);

        return response;
    }

    public void ResetConversation(string conversationId)
    {
        if (_histories.TryRemove(conversationId, out _))
            _logger.LogInformation("Reset history for conversation {Id}", conversationId);
    }

    public async Task RestoreSessionAsync(string conversationId, string systemPrompt, IReadOnlyList<(string Role, string Content)> messages)
    {
        try
        {
            var history = GetOrCreateHistory(conversationId, systemPrompt);
            foreach (var (role, content) in messages)
            {
                if (role == "user") history.AddUserMessage(content);
                else if (role == "assistant") history.AddAssistantMessage(content);
            }
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore session for {ConversationId}, starting with empty history", conversationId);
        }
    }

    public void Dispose()
    {
        _histories.Clear();
    }
}
