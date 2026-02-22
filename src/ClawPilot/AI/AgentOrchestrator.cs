using System.ClientModel;
using System.Collections.Concurrent;
using ClawPilot.AI.Filters;
using ClawPilot.AI.Plugins;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Skills;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI;

namespace ClawPilot.AI;

public class AgentOrchestrator : IDisposable
{
    private readonly Kernel _kernel;
    private readonly MemoryService _memory;
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly ClawPilotOptions _options;
    private IChatCompletionService? _chatService;

    private readonly ConcurrentDictionary<string, ChatHistory> _histories = new();

    /// <summary>
    /// Production constructor — builds the SK Kernel internally (§2.2) using the endpoint parameter (§2.3).
    /// Registers plugins, security filter, and imports MCP servers from loaded skills (§3.7).
    /// </summary>
    public AgentOrchestrator(
        IOptions<ClawPilotOptions> options,
        MemoryService memory,
        ITelegramChannel telegram,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        SkillLoaderService skillLoader,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _memory = memory;
        _logger = loggerFactory.CreateLogger<AgentOrchestrator>();

        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(_options.OpenRouterApiKey),
            new OpenAIClientOptions { Endpoint = new Uri("https://openrouter.ai/api/v1") });

        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddOpenAIChatCompletion(
            modelId: _options.Model,
            openAIClient: openAiClient);

        kernelBuilder.Plugins.AddFromObject(new MessagingPlugin(telegram, scopeFactory, _options));
        kernelBuilder.Plugins.AddFromObject(new SchedulerPlugin(scopeFactory));
        kernelBuilder.Plugins.AddFromObject(new UtilityPlugin(memory));
        kernelBuilder.Plugins.AddFromObject(new WebPlugin(scopeFactory, httpClientFactory));

        _kernel = kernelBuilder.Build();
        _kernel.FunctionInvocationFilters.Add(
            new SecurityFilter(loggerFactory.CreateLogger<SecurityFilter>()));

        // §3.7: Import MCP servers from enabled skills
        _ = ImportMcpPluginsAsync(skillLoader);
    }

    /// <summary>
    /// Internal constructor for unit tests — accepts a pre-built Kernel.
    /// </summary>
    internal AgentOrchestrator(
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

    public bool HasHistory(string conversationId) => _histories.ContainsKey(conversationId);

    private async Task ImportMcpPluginsAsync(SkillLoaderService skillLoader)
    {
        foreach (var skill in skillLoader.LoadedSkills.Where(s => s.Enabled))
        {
            foreach (var (name, config) in skill.McpServers)
            {
                try
                {
                    _logger.LogInformation(
                        "MCP server '{Name}' from skill '{Skill}': {Command} {Args} (type: {Type})",
                        name, skill.Name, config.Command, string.Join(" ", config.Args), config.Type);

                    // TODO: Wire actual MCP import when Microsoft.SemanticKernel.Connectors.MCP is available:
                    // await _kernel.ImportPluginFromMcpServerAsync(name, config.Command, config.Args, config.Env);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to import MCP plugin '{Name}' from skill '{Skill}'", name, skill.Name);
                }
            }
        }
    }

    public void Dispose()
    {
        _histories.Clear();
    }
}
