# ClawPilot — Implementation Plan

> **Fork of NanoClaw**: .NET 9 rewrite using **Semantic Kernel + OpenRouter** (instead of Claude Agent SDK), **Telegram** (instead of WhatsApp), and **SQLite with vector memory** for persistence.

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Framework & LLM Provider Evaluation](#2-framework--llm-provider-evaluation)
3. [Component Mapping](#3-component-mapping)
4. [Project Structure](#4-project-structure)
5. [Telegram Channel Layer](#5-telegram-channel-layer)
6. [LLM Integration (Semantic Kernel + OpenRouter)](#6-llm-integration-semantic-kernel--openrouter)
7. [Database Layer & Vector Memory](#7-database-layer--vector-memory)
8. [Session & Conversation Management](#8-session--conversation-management)
9. [Tool System (IPC Replacement)](#9-tool-system-ipc-replacement)
10. [Hooks & Security](#10-hooks--security)
11. [Group Chat Support](#11-group-chat-support)
12. [Configuration System](#12-configuration-system)
13. [Skills Engine](#13-skills-engine)
14. [Container Strategy](#14-container-strategy)
15. [Logging & Observability](#15-logging--observability)
16. [Testing Strategy](#16-testing-strategy)
17. [Deployment](#17-deployment)
18. [Detailed Implementation Todo List](#18-detailed-implementation-todo-list)
19. [Open Questions & Risks](#19-open-questions--risks)

---

## 1. Architecture Overview

### NanoClaw (Original)

```
WhatsApp (Baileys)
    ↓
index.ts  (polling loop, 2s interval)
    ↓
SQLite  (message queue + conversation state)
    ↓
container-runner.ts  (Docker container per agent)
    ↓
agent-runner  (Claude Agent SDK inside container)
    ↓
IPC (file-based stdin/stdout + MCP stdio)
    ↓
response → SQLite → WhatsApp
```

### ClawPilot (Target)

```
Telegram (Telegram.Bot polling / webhook)
    ↓
Program.cs  (hosted service, event-driven)
    ↓
SQLite  (EF Core — messages + state + vector memory via sqlite-vec)
    ↓
SemanticKernelAgent  (in-process orchestration, no containers)
    ↓
OpenRouter API  (unified LLM access — Claude, GPT, Gemini, etc.)
    ↓
SK Plugins + Custom Tools + MCP servers
    ↓
response → SQLite → Telegram
```

**Key architectural differences**:
- NanoClaw spawns Docker containers per agent run with Claude SDK inside each. ClawPilot runs **in-process** using Semantic Kernel for orchestration, eliminating the container-runner, agent-runner, and file-based IPC layers entirely.
- NanoClaw is locked to Claude models. ClawPilot uses **OpenRouter** as a provider-agnostic gateway, giving access to Claude, GPT-4o, Gemini, Llama, and 200+ other models through a single API.
- ClawPilot adds **vector memory** (sqlite-vec) for semantic retrieval across conversation history — a capability NanoClaw lacks.

---

## 2. Framework & LLM Provider Evaluation

The deep research identified four viable approaches for the .NET + Telegram + OpenRouter stack. Here is our evaluation:

### Option A: Full Framework — BotSharp

[BotSharp](https://github.com/SciSharp/BotSharp) is an open-source C#/.NET Core AI bot platform with multi-agent conversation, state management, RAG support, and a built-in Telegram plugin (uses `Telegram.Bot` under the hood).

| Pros | Cons |
|---|---|
| Batteries-included: Telegram, RAG, multi-agent | Heavy — brings entire framework with its own conventions |
| OpenRouter via BotSharp's OpenAI plugin (custom base URL) | Less control over orchestration loop |
| SK plugin for memory | Learning curve for BotSharp's plugin system |
| Production-tested | May be overkill for a personal assistant |

**Verdict**: Good for rapid prototyping, but too opinionated. We'd fight the framework for custom behavior.

### Option B: Full Framework — LLM Tornado

[LLM Tornado](https://github.com/lofcz/LLMTornado) is a provider-agnostic .NET SDK with 30+ built-in connectors (including native OpenRouter support) and orchestration primitives.

| Pros | Cons |
|---|---|
| Native OpenRouter connector — no adapter needed | Newer project, smaller community |
| Works with Semantic Kernel and `Microsoft.Extensions.AI` | Less documentation than SK |
| Local inference support (Ollama, etc.) | Agent loop still needs custom code |
| Lightweight compared to BotSharp | |

**Verdict**: Strong contender for the LLM client layer. Could be used alongside SK, but adds a dependency where SK alone suffices.

### Option C: Semantic Kernel + OpenRouter (Recommended) ✅

Use [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel) for orchestration with the **OpenAI .NET SDK** pointed at OpenRouter's base URL. SK provides:
- Chat completion with function calling (tool use)
- Pluggable memory with SQLite Vector Store connector (`Microsoft.SemanticKernel.Connectors.SqliteVec`)
- Prompt templates, filters (hooks), and auto function calling
- First-party Microsoft support, large community, stable API

OpenRouter is accessed by configuring the OpenAI connector with `baseUrl = "https://openrouter.ai/api/v1"` and an OpenRouter API key — no custom SDK needed.

| Pros | Cons |
|---|---|
| Most mature .NET AI orchestration library | Slightly more boilerplate than a full framework |
| OpenRouter works via OpenAI-compatible endpoint | SK's vector store connectors are still "Preview" |
| Native SQLite vector memory (sqlite-vec) | |
| Full control over the agent loop | |
| `Microsoft.Extensions.AI` integration | |

**Verdict**: Best balance of flexibility, maturity, and control. This is our primary stack.

### Option D: Manual Stack (Telegram.Bot + HttpClient + SQLite)

Roll everything by hand: `Telegram.Bot` for messaging, `HttpClient` or `OpenRouter.NET` for LLM calls, raw SQLite for state.

| Pros | Cons |
|---|---|
| Maximum control, minimal dependencies | Must implement context management, tool calling, prompt building |
| Lightest possible footprint | No built-in function calling or memory |
| Easy to understand | Significant engineering effort for agent capabilities |

**Verdict**: Too low-level. We'd end up reimplementing what SK already provides.

### Decision

**Primary stack: Semantic Kernel + OpenRouter** (Option C), with `Telegram.Bot` for messaging and EF Core + sqlite-vec for persistence.

---

## 3. Component Mapping

| NanoClaw Component | File(s) | ClawPilot Equivalent | Technology |
|---|---|---|---|
| WhatsApp channel | `channels/whatsapp.ts` | Telegram channel | `Telegram.Bot` NuGet |
| Claude Agent SDK | `container/agent-runner/` | SK + OpenRouter orchestration | `Microsoft.SemanticKernel` + OpenAI SDK |
| Docker containers | `container-runner.ts`, `container-runtime.ts` | **Eliminated** — in-process | SK Kernel manages LLM calls |
| File-based IPC | `ipc.ts`, `ipc-mcp-stdio.ts` | **Eliminated** — native SK plugins | SK KernelFunction + MCP |
| SQLite (better-sqlite3) | `db.ts` | SQLite (EF Core) + vector memory | `EF Core SQLite` + `sqlite-vec` |
| Polling loop | `index.ts` (2s setInterval) | Hosted service + event-driven | `IHostedService` |
| Config (.env) | `config.ts`, `env.ts` | appsettings.json + env | `IConfiguration` |
| Group queue | `group-queue.ts` | Channel-based queue | `System.Threading.Channels` |
| Router | `router.ts` | MediatR or direct dispatch | Pattern matching |
| Logging (pino) | `logger.ts` | Serilog | `Serilog.Extensions.Hosting` |
| Mount security | `mount-security.ts` | **Eliminated** | No containers = no mounts |
| Skills engine | `skills-engine/` | **Deferred** (Phase 2) | Port or redesign |
| Task scheduler | `task-scheduler.ts` | Quartz.NET or Timer-based | `Quartz` NuGet |
| Auth (QR pairing) | `whatsapp-auth.ts` | Bot token (no auth flow) | Single env var |
| launchd plist | `launchd/` | systemd unit or launchd | Platform-specific |
| — (no equivalent) | — | Vector memory / RAG | `SK.Connectors.SqliteVec` + `sqlite-vec` |

---

## 4. Project Structure

```
ClawPilot/
├── ClawPilot.sln
├── src/
│   └── ClawPilot/
│       ├── ClawPilot.csproj
│       ├── Program.cs                  # Entry point, DI, hosted services
│       ├── appsettings.json            # Configuration
│       ├── appsettings.Development.json
│       │
│       ├── Configuration/
│       │   └── ClawPilotOptions.cs     # Strongly-typed config
│       │
│       ├── Database/
│       │   ├── ClawPilotDbContext.cs    # EF Core DbContext
│       │   ├── Entities/
│       │   │   ├── Conversation.cs
│       │   │   ├── Message.cs
│       │   │   ├── MemoryRecord.cs     # Vector memory entity
│       │   │   └── ScheduledTask.cs
│       │   └── Migrations/
│       │
│       ├── Channels/
│       │   ├── ITelegramChannel.cs     # Interface
│       │   └── TelegramChannel.cs      # Telegram.Bot integration
│       │
│       ├── AI/
│       │   ├── AgentOrchestrator.cs      # SK Kernel setup & agent loop
│       │   ├── OpenRouterConfig.cs       # OpenRouter provider config
│       │   ├── MemoryService.cs          # sqlite-vec vector memory
│       │   ├── Plugins/
│       │   │   ├── MessagingPlugin.cs    # send_message, search_messages
│       │   │   ├── SchedulerPlugin.cs    # schedule_task, list_tasks
│       │   │   └── UtilityPlugin.cs      # get_datetime, etc.
│       │   └── Filters/
│       │       └── SecurityFilter.cs     # Pre/post function call filtering
│       │
│       ├── Services/
│       │   ├── MessageProcessorService.cs  # Background worker
│       │   ├── GroupQueueService.cs         # Serialized group processing
│       │   └── TaskSchedulerService.cs      # Scheduled tasks
│       │
│       └── Logging/
│           └── SerilogConfig.cs
│
├── tests/
│   └── ClawPilot.Tests/
│       ├── ClawPilot.Tests.csproj
│       ├── DatabaseTests.cs
│       ├── AgentOrchestratorTests.cs
│       ├── TelegramChannelTests.cs
│       └── GroupQueueTests.cs
│
└── docs/
    ├── research.md
    ├── deep-research-report.md
    └── plan.md
```

### ClawPilot.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- Telegram -->
    <PackageReference Include="Telegram.Bot" Version="22.*" />

    <!-- Semantic Kernel (orchestration + function calling) -->
    <PackageReference Include="Microsoft.SemanticKernel" Version="1.*" />

    <!-- OpenRouter via OpenAI-compatible connector -->
    <PackageReference Include="Microsoft.SemanticKernel.Connectors.OpenAI" Version="1.*" />

    <!-- Vector memory (sqlite-vec for semantic search) -->
    <PackageReference Include="Microsoft.SemanticKernel.Connectors.SqliteVec" Version="1.*-preview" />

    <!-- Database -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.*" />

    <!-- Logging -->
    <PackageReference Include="Serilog.Extensions.Hosting" Version="9.*" />
    <PackageReference Include="Serilog.Settings.Configuration" Version="10.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.*" />

    <!-- Configuration -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.*" />

    <!-- Optional: OpenRouter.NET for advanced OpenRouter features -->
    <!-- <PackageReference Include="OpenRouter.NET" Version="*" /> -->

  </ItemGroup>

</Project>
```

---

## 5. Telegram Channel Layer

NanoClaw's WhatsApp layer (`channels/whatsapp.ts`) uses Baileys with a complex QR-code pairing flow, phone number normalization, and media handling. Telegram is significantly simpler — a single bot token from @BotFather.

### TelegramChannel.cs

```csharp
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ClawPilot.Channels;

public interface ITelegramChannel
{
    Task StartAsync(CancellationToken ct);
    Task SendTextAsync(long chatId, string text, long? replyToMessageId = null, CancellationToken ct = default);
    Task SendTypingAsync(long chatId, CancellationToken ct = default);
    event Func<IncomingMessage, Task> OnMessage;
}

public record IncomingMessage(
    long ChatId,
    long MessageId,
    string Text,
    string SenderName,
    string SenderId,
    bool IsGroup,
    string? GroupName,
    DateTimeOffset Timestamp
);

public class TelegramChannel : ITelegramChannel
{
    private readonly TelegramBotClient _bot;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly ClawPilotOptions _options;

    public event Func<IncomingMessage, Task>? OnMessage;

    public TelegramChannel(
        IOptions<ClawPilotOptions> options,
        ILogger<TelegramChannel> logger)
    {
        _options = options.Value;
        _logger = logger;
        _bot = new TelegramBotClient(_options.TelegramBotToken);
    }

    public Task StartAsync(CancellationToken ct)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true,
        };

        _bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: ct
        );

        _logger.LogInformation("Telegram bot started polling");
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(
        ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { } text } message)
            return;

        // Filter: only respond to allowed users/groups
        if (!IsAllowed(message))
            return;

        var incoming = new IncomingMessage(
            ChatId: message.Chat.Id,
            MessageId: message.MessageId,
            Text: text,
            SenderName: message.From?.FirstName ?? "Unknown",
            SenderId: message.From?.Id.ToString() ?? "0",
            IsGroup: message.Chat.Type is ChatType.Group or ChatType.Supergroup,
            GroupName: message.Chat.Title,
            Timestamp: message.Date
        );

        if (OnMessage is not null)
            await OnMessage.Invoke(incoming);
    }

    private bool IsAllowed(Message message)
    {
        // NanoClaw equivalent: PHONE_ID / group JID filtering
        // When AllowedChatIds is empty, allow all chats ("open mode" for development)
        if (_options.AllowedChatIds.Count == 0)
            return true;
        var chatId = message.Chat.Id.ToString();
        return _options.AllowedChatIds.Contains(chatId);
    }

    public async Task SendTextAsync(
        long chatId, string text, long? replyToMessageId = null, CancellationToken ct = default)
    {
        // Telegram has a 4096 char limit per message — chunk if needed
        const int maxLen = 4096;
        foreach (var chunk in ChunkText(text, maxLen))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: chunk,
                replyParameters: replyToMessageId.HasValue
                    ? new ReplyParameters { MessageId = (int)replyToMessageId.Value }
                    : null,
                parseMode: ParseMode.Markdown,
                cancellationToken: ct
            );
        }
    }

    public async Task SendTypingAsync(long chatId, CancellationToken ct = default)
    {
        await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
    }

    private Task HandleErrorAsync(
        ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }

    private static IEnumerable<string> ChunkText(string text, int maxLen)
    {
        for (int i = 0; i < text.Length; i += maxLen)
            yield return text[i..Math.Min(i + maxLen, text.Length)];
    }
}
```

### Key Differences from NanoClaw's WhatsApp Layer

| Aspect | NanoClaw (WhatsApp) | ClawPilot (Telegram) |
|---|---|---|
| Auth | QR code → pairing code → creds store | Single bot token from @BotFather |
| Connection | Persistent WebSocket (Baileys) | Long-polling or webhook |
| Identity | Phone JID (`12345@s.whatsapp.net`) | Numeric chat ID (`123456789`) |
| Groups | JID-based (`g.us`), participant lookup | Chat ID, native group support |
| Media | Baileys `downloadMediaMessage` | `GetFile()` + download URL |
| Rate limits | Unofficial, ban risk | Official API, 30 msg/s |
| Message length | ~65k chars | 4096 chars per message |
| Typing indicator | `sendPresenceUpdate('composing')` | `SendChatAction(Typing)` |

---

## 6. LLM Integration (Semantic Kernel + OpenRouter)

This is the core replacement for NanoClaw's Docker container + Claude Agent SDK pipeline. Instead of spawning containers, we use **Semantic Kernel** for orchestration with **OpenRouter** as the LLM provider — giving access to 200+ models (Claude, GPT-4o, Gemini, Llama, etc.) through a single OpenAI-compatible API.

### AgentOrchestrator.cs

```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace ClawPilot.AI;

/// <summary>
/// Manages the Semantic Kernel lifecycle and per-conversation chat history.
/// Replaces NanoClaw's container-runner.ts + agent-runner entirely.
///
/// Uses OpenRouter as the LLM provider via the OpenAI-compatible connector.
/// This gives access to Claude, GPT-4o, Gemini, Llama, and 200+ other models
/// through a single API key and endpoint.
/// </summary>
public class AgentOrchestrator : IDisposable
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;
    private readonly MemoryService _memory;
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly ClawPilotOptions _options;

    // Per-conversation chat history (replaces NanoClaw's per-container sessions)
    private readonly ConcurrentDictionary<string, ChatHistory> _histories = new();
    private readonly SemaphoreSlim _historyLock = new(1, 1);

    public AgentOrchestrator(
        MemoryService memory,
        IOptions<ClawPilotOptions> options,
        ILogger<AgentOrchestrator> logger)
    {
        _memory = memory;
        _options = options.Value;
        _logger = logger;

        // Build SK Kernel with OpenRouter as the chat completion backend.
        // OpenRouter exposes an OpenAI-compatible API, so we use the
        // standard OpenAI connector with a custom base URL.
        var builder = Kernel.CreateBuilder();

        builder.AddOpenAIChatCompletion(
            modelId: _options.Model ?? "anthropic/claude-sonnet-4-20250514",
            apiKey: _options.OpenRouterApiKey,
            endpoint: new Uri("https://openrouter.ai/api/v1")
        );

        // Register plugins (replaces NanoClaw's IPC MCP tools)
        builder.Plugins.AddFromType<MessagingPlugin>();
        builder.Plugins.AddFromType<SchedulerPlugin>();
        builder.Plugins.AddFromType<UtilityPlugin>();

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();
    }

    /// <summary>
    /// Get or create chat history for a conversation.
    /// Maps to NanoClaw's per-container agent lifecycle.
    /// </summary>
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

    /// <summary>
    /// Send a message and get the complete response.
    /// This is the primary entry point — replaces NanoClaw's
    /// container spawn → IPC write → IPC read pipeline.
    /// </summary>
    public async Task<string> SendMessageAsync(
        string conversationId,
        string userMessage,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var history = GetOrCreateHistory(conversationId, systemPrompt);

        // Retrieve relevant memories for RAG context injection
        var memories = await _memory.RecallAsync(conversationId, userMessage, limit: 5, ct);
        if (memories.Any())
        {
            var memoryContext = string.Join("\n", memories.Select(m => $"- {m}"));
            history.AddSystemMessage($"Relevant context from memory:\n{memoryContext}");
        }

        history.AddUserMessage(userMessage);

        // Enable auto function calling (SK handles tool invocation automatically)
        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            MaxTokens = _options.MaxResponseTokens,
        };

        var result = await _chatService.GetChatMessageContentAsync(
            history, settings, _kernel, ct);

        var response = result.Content ?? "[No response]";
        history.AddAssistantMessage(response);

        // Store interaction in vector memory for future retrieval
        await _memory.SaveAsync(conversationId, userMessage, response, ct);

        return response;
    }

    /// <summary>
    /// Reset a conversation's history.
    /// Equivalent to NanoClaw's container cleanup.
    /// </summary>
    public void ResetConversation(string conversationId)
    {
        if (_histories.TryRemove(conversationId, out _))
            _logger.LogInformation("Reset history for conversation {Id}", conversationId);
    }

    public void Dispose()
    {
        _histories.Clear();
    }
}
```

### How Agent Execution Changes

| NanoClaw | ClawPilot |
|---|---|
| `spawnContainer()` → Docker run | `Kernel.CreateBuilder()` → in-process SK kernel |
| Write to IPC file → agent reads stdin | `chatService.GetChatMessageContentAsync()` → HTTP to OpenRouter |
| Agent runs Claude SDK in container | SK orchestrator calls OpenRouter API |
| Read IPC file for response | `ChatMessageContent.Content` returned directly |
| Container dies after timeout | Chat history persists in memory, key facts in sqlite-vec |
| ~5-10s container startup overhead | ~instant (HTTP request to OpenRouter) |
| Locked to Claude models only | Any model via OpenRouter (Claude, GPT, Gemini, Llama, etc.) |

### OpenRouter Configuration

OpenRouter works as a drop-in replacement for the OpenAI API. The key benefit: **one API key, 200+ models**.

```csharp
// The SK OpenAI connector works with OpenRouter out of the box
// by simply overriding the endpoint URL.
builder.AddOpenAIChatCompletion(
    modelId: "anthropic/claude-sonnet-4-20250514",    // or "openai/gpt-4o", "google/gemini-2.0-flash", etc.
    apiKey: config.OpenRouterApiKey,
    endpoint: new Uri("https://openrouter.ai/api/v1")
);
```

Alternatively, use `OpenRouter.NET` for richer OpenRouter-specific features (model listing, cost tracking):

```csharp
// Optional: OpenRouter.NET for advanced features
var openRouter = new OpenRouterClient(config.OpenRouterApiKey);
var completion = await openRouter.CreateChatCompletionAsync(new ChatCompletionRequest
{
    Model = "anthropic/claude-sonnet-4-20250514",
    Messages = [new ChatMessage { Role = "user", Content = "Hello!" }],
});
```

---

## 7. Database Layer & Vector Memory

NanoClaw uses raw `better-sqlite3` with hand-written SQL. ClawPilot uses **EF Core with SQLite** for type safety and migrations.

### Entities

```csharp
namespace ClawPilot.Database.Entities;

public class Conversation
{
    public int Id { get; set; }

    /// <summary>Telegram chat ID (string for flexibility)</summary>
    public required string ChatId { get; set; }

    /// <summary>Display name of the chat or user</summary>
    public string? DisplayName { get; set; }

    /// <summary>Whether this is a group chat</summary>
    public bool IsGroup { get; set; }

    /// <summary>Session identifier for LLM context resume</summary>
    public string? SessionId { get; set; }

    /// <summary>Custom system prompt override</summary>
    public string? SystemPrompt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Message> Messages { get; set; } = [];
}

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    /// <summary>"user", "assistant", or "system"</summary>
    public required string Role { get; set; }

    public required string Content { get; set; }

    /// <summary>Telegram message ID for reply threading</summary>
    public long? TelegramMessageId { get; set; }

    /// <summary>Sender info (for group chats)</summary>
    public string? SenderName { get; set; }
    public string? SenderId { get; set; }

    /// <summary>Processing state: "pending", "processing", "done", "error"</summary>
    public string Status { get; set; } = "pending";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ScheduledTask
{
    public int Id { get; set; }
    public required string ChatId { get; set; }
    public required string Description { get; set; }
    public required string CronExpression { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Persists skill enabled/disabled state across restarts.</summary>
public class SkillState
{
    public int Id { get; set; }
    public required string SkillName { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### DbContext

```csharp
using Microsoft.EntityFrameworkCore;

namespace ClawPilot.Database;

public class ClawPilotDbContext : DbContext
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<SkillState> SkillStates => Set<SkillState>();

    public ClawPilotDbContext(DbContextOptions<ClawPilotDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasIndex(c => c.ChatId).IsUnique();
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasIndex(m => new { m.ConversationId, m.Status });
            e.HasIndex(m => m.CreatedAt);
        });

        modelBuilder.Entity<ScheduledTask>(e =>
        {
            e.HasIndex(t => t.ChatId);
        });

        modelBuilder.Entity<SkillState>(e =>
        {
            e.HasIndex(s => s.SkillName).IsUnique();
        });
    }
}
```

### Comparison with NanoClaw's DB

NanoClaw has these tables: `conversations`, `messages`, `conversation_permissions`, `scheduled_tasks`, `auth_sessions`. ClawPilot drops `auth_sessions` (Telegram needs no auth flow) and `conversation_permissions` (simplified — use `AllowedChatIds` config). The core `conversations` + `messages` + `scheduled_tasks` carry over directly.

### Vector Memory with sqlite-vec

A key addition from the deep research: ClawPilot uses **sqlite-vec** for semantic retrieval — something NanoClaw entirely lacks. This allows the agent to recall relevant past conversations, facts, and context via embedding similarity search, all within the same SQLite database file.

The [sqlite-vec extension](https://github.com/asg017/sqlite-vec) adds native vector types and SIMD-accelerated kNN distance functions to SQLite. Semantic Kernel's `SqliteVec` connector provides a high-level .NET API on top.

#### MemoryService.cs

```csharp
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Connectors.SqliteVec;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace ClawPilot.AI;

/// <summary>
/// Vector memory service using sqlite-vec for semantic search.
/// Stores conversation summaries and key facts as embeddings
/// in the same SQLite database used for relational data.
///
/// This is a new capability — NanoClaw has no equivalent.
/// Based on findings from the deep research report:
///   - SK provides SqliteVectorStore connector (preview)
///   - sqlite-vec adds VIRTUAL TABLE for kNN search
///   - Embeddings generated via OpenRouter or a dedicated embedding model
/// </summary>
public class MemoryService : IAsyncDisposable
{
    private readonly ISemanticTextMemory _memory;
    private const string CollectionName = "conversations";

    public MemoryService(ClawPilotOptions options)
    {
        // Build memory store backed by sqlite-vec
        // The SqliteVec connector creates vec_ virtual tables automatically
        var memoryBuilder = new MemoryBuilder();

        memoryBuilder.WithSqliteVecMemoryStore(options.DatabasePath);

        // Use OpenRouter for embeddings too (or a local model)
        memoryBuilder.WithOpenAITextEmbeddingGeneration(
            modelId: "openai/text-embedding-3-small",
            apiKey: options.OpenRouterApiKey,
            endpoint: new Uri("https://openrouter.ai/api/v1")
        );

        _memory = memoryBuilder.Build();
    }

    /// <summary>
    /// Save a conversation exchange as a searchable memory.
    /// </summary>
    public async Task SaveAsync(
        string conversationId, string userMessage, string assistantResponse,
        CancellationToken ct = default)
    {
        var id = $"{conversationId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var text = $"User: {userMessage}\nAssistant: {assistantResponse}";

        await _memory.SaveInformationAsync(
            CollectionName, text, id,
            description: $"Chat {conversationId}",
            additionalMetadata: conversationId,
            cancellationToken: ct);
    }

    /// <summary>
    /// Recall relevant memories for a given query (RAG).
    /// </summary>
    public async Task<List<string>> RecallAsync(
        string conversationId, string query, int limit = 5,
        CancellationToken ct = default)
    {
        var results = new List<string>();
        await foreach (var result in _memory.SearchAsync(
            CollectionName, query, limit, minRelevanceScore: 0.7,
            cancellationToken: ct))
        {
            results.Add(result.Metadata.Text);
        }
        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (_memory is IAsyncDisposable disposable)
            await disposable.DisposeAsync();
    }
}
```

#### How Vector Memory Enhances the Agent

| Scenario | Without vector memory | With sqlite-vec |
|---|---|---|
| "What did I ask about last week?" | Agent has no recall beyond current session | Semantic search finds relevant past exchanges |
| Long conversations (context overflow) | History truncated, context lost | Key facts persist as embeddings, recalled via RAG |
| Cross-conversation knowledge | Each chat is fully isolated | Shared memory collection enables knowledge transfer |
| User preferences | Must be re-stated each session | Stored as embeddings, recalled when relevant |

---

## 8. Session & Conversation Management

NanoClaw's `processMessages()` loop polls SQLite every 2 seconds for pending messages. ClawPilot uses an **event-driven** approach — Telegram.Bot fires events immediately, which are enqueued into a `System.Threading.Channels.Channel` for ordered processing.

### MessageProcessorService.cs

```csharp
using System.Threading.Channels;

namespace ClawPilot.Services;

/// <summary>
/// Background service that processes incoming messages.
/// Replaces NanoClaw's setInterval(processMessages, 2000) polling loop.
/// </summary>
public class MessageProcessorService : BackgroundService
{
    private readonly Channel<IncomingMessage> _messageQueue;
    private readonly AgentOrchestrator _agent;
    private readonly ITelegramChannel _telegram;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageProcessorService> _logger;
    private readonly ClawPilotOptions _options;

    // Per-chat semaphores to serialize processing within a chat
    // (like NanoClaw's container lock per conversation)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatLocks = new();

    public MessageProcessorService(
        Channel<IncomingMessage> messageQueue,
        AgentOrchestrator agent,
        ITelegramChannel telegram,
        IServiceScopeFactory scopeFactory,
        IOptions<ClawPilotOptions> options,
        ILogger<MessageProcessorService> logger)
    {
        _messageQueue = messageQueue;
        _agent = agent;
        _telegram = telegram;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var message in _messageQueue.Reader.ReadAllAsync(ct))
        {
            // Fire and forget per message, but serialize per chat
            _ = ProcessMessageAsync(message, ct);
        }
    }

    private async Task ProcessMessageAsync(IncomingMessage message, CancellationToken ct)
    {
        var chatKey = message.ChatId.ToString();
        var chatLock = _chatLocks.GetOrAdd(chatKey, _ => new SemaphoreSlim(1, 1));

        await chatLock.WaitAsync(ct);
        try
        {
            // Show typing indicator (like NanoClaw's sendPresenceUpdate)
            await _telegram.SendTypingAsync(message.ChatId, ct);

            // Persist incoming message
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.ChatId == chatKey, ct)
                ?? CreateConversation(db, message);

            db.Messages.Add(new Message
            {
                ConversationId = conversation.Id,
                Role = "user",
                Content = message.Text,
                SenderName = message.SenderName,
                SenderId = message.SenderId,
                TelegramMessageId = message.MessageId,
                Status = "processing",
            });
            await db.SaveChangesAsync(ct);

            // Build system prompt (NanoClaw reads from groups/main/CLAUDE.md)
            var systemPrompt = BuildSystemPrompt(conversation, message);

            // Send to SK agent (OpenRouter) and get response
            var response = await _agent.SendMessageAsync(
                chatKey, message.Text, systemPrompt, ct);

            // Persist assistant response
            db.Messages.Add(new Message
            {
                ConversationId = conversation.Id,
                Role = "assistant",
                Content = response,
                Status = "done",
            });
            conversation.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            // Send response to Telegram
            await _telegram.SendTextAsync(
                message.ChatId, response, message.MessageId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message in chat {ChatId}", chatKey);
            await _telegram.SendTextAsync(
                message.ChatId, "⚠️ Sorry, something went wrong.", ct: ct);
        }
        finally
        {
            chatLock.Release();
        }
    }

    private string BuildSystemPrompt(Conversation conv, IncomingMessage msg)
    {
        // Base prompt — equivalent to NanoClaw's groups/main/CLAUDE.md
        var prompt = _options.SystemPrompt ?? "You are a helpful personal assistant.";

        if (msg.IsGroup)
        {
            prompt += $"\n\nYou are in a group chat named '{msg.GroupName}'. " +
                      $"The message is from {msg.SenderName}. " +
                      "Only respond when directly addressed or when you can add value.";
        }

        if (conv.SystemPrompt is not null)
        {
            prompt += $"\n\nAdditional context:\n{conv.SystemPrompt}";
        }

        return prompt;
    }

    private Conversation CreateConversation(ClawPilotDbContext db, IncomingMessage msg)
    {
        var conv = new Conversation
        {
            ChatId = msg.ChatId.ToString(),
            DisplayName = msg.IsGroup ? msg.GroupName : msg.SenderName,
            IsGroup = msg.IsGroup,
        };
        db.Conversations.Add(conv);
        return conv;
    }
}
```

### Session Persistence / Resume

NanoClaw tracks conversation state in SQLite and passes full history to each container run.

ClawPilot uses a **two-tier persistence** approach:

1. **In-memory `ChatHistory`**: SK's `ChatHistory` object holds the rolling conversation window per chat. Fast, supports function calling context.
2. **sqlite-vec vector memory**: Key exchanges are embedded and stored for long-term semantic retrieval (RAG). Survives process restarts.

```csharp
// On startup: rebuild ChatHistory from recent DB messages
public async Task RestoreSessionAsync(string conversationId, ClawPilotDbContext db)
{
    var recentMessages = await db.Messages
        .Where(m => m.Conversation.ChatId == conversationId)
        .OrderByDescending(m => m.CreatedAt)
        .Take(50)  // last 50 messages as rolling window
        .OrderBy(m => m.CreatedAt)
        .ToListAsync();

    var history = GetOrCreateHistory(conversationId, "...");
    foreach (var msg in recentMessages)
    {
        if (msg.Role == "user") history.AddUserMessage(msg.Content);
        else if (msg.Role == "assistant") history.AddAssistantMessage(msg.Content);
    }
}
```

For older context beyond the rolling window, the vector memory service automatically injects relevant past exchanges via RAG (see section 7).

---

## 9. Tool System (IPC Replacement)

NanoClaw exposes tools to Claude via a custom MCP stdio server (`ipc-mcp-stdio.ts`) inside each container. The agent-runner pipes tool calls through file-based IPC. ClawPilot replaces this entirely with **Semantic Kernel plugins** — native C# classes decorated with `[KernelFunction]` attributes that SK's auto function calling invokes automatically.

### MessagingPlugin.cs

```csharp
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ClawPilot.AI.Plugins;

/// <summary>
/// SK plugin for messaging tools.
/// Replaces NanoClaw's "send_message" and "search_messages" IPC MCP tools.
///
/// SK plugins use [KernelFunction] attributes — the Kernel automatically
/// exposes these as tool definitions to the LLM and handles invocation.
/// No AIFunctionFactory or manual registration needed.
/// </summary>
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
    [Description("Send a message to a Telegram chat. Use this to proactively message the user.")]
    public async Task<string> SendMessageAsync(
        [Description("The Telegram chat ID to send to")] long chatId,
        [Description("The message text")] string message)
    {
        await _telegram.SendTextAsync(chatId, message);
        return $"Message sent to {chatId}";
    }

    [KernelFunction("search_messages")]
    [Description("Search past conversation messages by keyword. Returns matching messages with sender and timestamp.")]
    public async Task<string> SearchMessagesAsync(
        [Description("The search query")] string query,
        [Description("Max results to return")] int limit = 20)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

        var messages = await db.Messages
            .Where(m => EF.Functions.Like(m.Content, $"%{query}%"))
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new { m.Role, m.Content, m.CreatedAt, m.SenderName })
            .ToListAsync();

        return System.Text.Json.JsonSerializer.Serialize(messages);
    }
}
```

### SchedulerPlugin.cs

```csharp
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ClawPilot.AI.Plugins;

public class SchedulerPlugin
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SchedulerPlugin(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    [KernelFunction("schedule_task")]
    [Description("Schedule a recurring task. Takes a description and cron expression.")]
    public async Task<string> ScheduleTaskAsync(
        [Description("Chat ID to run task for")] long chatId,
        [Description("What the task should do")] string description,
        [Description("Cron expression for scheduling")] string cronExpression)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

        db.ScheduledTasks.Add(new ScheduledTask
        {
            ChatId = chatId.ToString(),
            Description = description,
            CronExpression = cronExpression,
        });
        await db.SaveChangesAsync();

        return $"Task scheduled: {description} ({cronExpression})";
    }
}
```

### UtilityPlugin.cs

```csharp
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace ClawPilot.AI.Plugins;

public class UtilityPlugin
{
    [KernelFunction("get_current_datetime")]
    [Description("Get the current date and time in UTC.")]
    public string GetCurrentDateTime()
    {
        var utcNow = DateTimeOffset.UtcNow;
        return $"UTC: {utcNow:yyyy-MM-dd HH:mm:ss}\nUnix: {utcNow.ToUnixTimeSeconds()}";
    }

    [KernelFunction("recall_memory")]
    [Description("Search long-term memory for relevant past conversations and facts.")]
    public async Task<string> RecallMemoryAsync(
        [Description("What to search for")] string query,
        Kernel kernel)
    {
        // Use the MemoryService registered in the kernel
        var memory = kernel.GetRequiredService<MemoryService>();
        var results = await memory.RecallAsync("global", query, limit: 5);
        return results.Any()
            ? string.Join("\n---\n", results)
            : "No relevant memories found.";
    }
}
```

### MCP Server Integration (Optional)

For complex tool ecosystems, SK supports external MCP servers. This is useful for integrating third-party tools without writing C# plugins:

```csharp
// SK supports MCP via the Microsoft.SemanticKernel.Connectors.MCP package
// Example: add a filesystem MCP server
var mcpPlugin = await kernel.ImportMcpPluginAsync("filesystem", new McpStdioConfig
{
    Command = "npx",
    Args = ["-y", "@modelcontextprotocol/server-filesystem", "/data"],
});

// Example: add a GitHub MCP server
var githubPlugin = await kernel.ImportMcpPluginAsync("github", new McpStdioConfig
{
    Command = "npx",
    Args = ["-y", "@modelcontextprotocol/server-github"],
    Env = new Dictionary<string, string>
    {
        ["GITHUB_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_TOKEN")!,
    },
});
```

---

## 10. Hooks & Security

NanoClaw has a specific `PreToolUse` bash command sanitization hook in the container-runner that checks for shell command patterns. Semantic Kernel provides an equivalent system via **function invocation filters** — middleware that runs before/after every tool call.

### SecurityFilter.cs

```csharp
using Microsoft.SemanticKernel;

namespace ClawPilot.AI.Filters;

/// <summary>
/// SK function invocation filter for security.
/// Replaces NanoClaw's PreToolUse bash sanitization hook.
///
/// They intercept function calls before and after execution.
/// </summary>
public class SecurityFilter : IFunctionInvocationFilter
{
    private readonly ILogger<SecurityFilter> _logger;

    // Dangerous tool patterns to block
    // (NanoClaw blocks bash commands with 'rm -rf', 'sudo', etc.)
    private static readonly string[] BlockedToolPatterns =
        ["shell", "bash", "exec", "run_command"];

    private static readonly string[] DangerousArgPatterns =
        ["rm -rf", "sudo", "chmod 777", "mkfs", "> /dev/"];

    public SecurityFilter(ILogger<SecurityFilter> logger)
    {
        _logger = logger;
    }

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var functionName = context.Function.Name;
        var args = context.Arguments?.ToString() ?? "";

        _logger.LogDebug("Pre-invocation: {Function} args={Args}", functionName, args);

        // Block dangerous tools entirely
        if (BlockedToolPatterns.Any(p =>
            functionName.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Blocked tool: {Function}", functionName);
            context.Result = new FunctionResult(context.Function,
                $"Tool '{functionName}' is not permitted in ClawPilot");
            return; // Skip execution
        }

        // Check for dangerous argument patterns
        if (DangerousArgPatterns.Any(p =>
            args.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Blocked dangerous args in {Function}: {Args}",
                functionName, args);
            context.Result = new FunctionResult(context.Function,
                "Arguments contain blocked patterns");
            return;
        }

        // Allow execution
        await next(context);

        // Post-invocation: log result length, could sanitize output
        var resultLen = context.Result?.ToString()?.Length ?? 0;
        _logger.LogDebug("Post-invocation: {Function} result length={Len}",
            functionName, resultLen);
    }
}
```

### Registering the Filter

```csharp
// In AgentOrchestrator or Program.cs
var builder = Kernel.CreateBuilder();
builder.Services.AddSingleton<IFunctionInvocationFilter, SecurityFilter>();
```

### Security Model Comparison

| NanoClaw | ClawPilot |
|---|---|
| Docker isolation per agent | In-process (less isolation, but no shell access) |
| Mount allow-list (`mount-security.ts`) | Not needed — no filesystem mounts |
| PreToolUse bash sanitization | SK `IFunctionInvocationFilter` with deny patterns |
| Network namespace per container | Process-level (consider restricting MCP servers) |
| `PHONE_ID` env whitelist | `AllowedChatIds` config array |
| File-based IPC (temp files) | In-memory function calls (no temp files) |
| — | SK `IFunctionInvocationFilter` (native filter pipeline) |

---

## 11. Group Chat Support

NanoClaw has `group-queue.ts` — a `Map<groupJid, Promise>` that serializes concurrent messages within the same group to avoid race conditions. ClawPilot uses `System.Threading.Channels`.

### GroupQueueService.cs

```csharp
using System.Threading.Channels;

namespace ClawPilot.Services;

/// <summary>
/// Ensures messages within the same group chat are processed sequentially.
/// NanoClaw equivalent: group-queue.ts with chained promises per group JID.
/// </summary>
public class GroupQueueService
{
    private readonly ConcurrentDictionary<string, Channel<Func<Task>>> _groupQueues = new();
    private readonly ConcurrentDictionary<string, Task> _processors = new();
    private readonly ILogger<GroupQueueService> _logger;

    public GroupQueueService(ILogger<GroupQueueService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Enqueue work for a specific group. Work items within the same group
    /// are processed sequentially; different groups run in parallel.
    /// </summary>
    public async Task EnqueueAsync(string groupId, Func<Task> work)
    {
        var channel = _groupQueues.GetOrAdd(groupId, _ =>
            Channel.CreateUnbounded<Func<Task>>());

        // Start processor if not running
        _processors.GetOrAdd(groupId, id => ProcessQueueAsync(id, channel));

        await channel.Writer.WriteAsync(work);
    }

    private async Task ProcessQueueAsync(
        string groupId, Channel<Func<Task>> channel)
    {
        await foreach (var work in channel.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing group queue for {GroupId}", groupId);
            }
        }
    }
}
```

### Telegram-Specific Group Behavior

```csharp
// In TelegramChannel.HandleUpdateAsync — detect @mentions for bot
private bool ShouldRespondInGroup(Message message)
{
    if (message.Chat.Type is ChatType.Private)
        return true;

    // Respond if bot is explicitly mentioned
    if (message.Entities?.Any(e => e.Type == MessageEntityType.Mention) == true)
    {
        var botUsername = _options.BotUsername; // e.g., "@ClawPilotBot"
        return message.Text?.Contains(botUsername, StringComparison.OrdinalIgnoreCase) == true;
    }

    // Respond if it's a reply to one of our messages
    if (message.ReplyToMessage?.From?.IsBot == true)
        return true;

    return false;
}
```

---

## 12. Configuration System

NanoClaw uses `.env` files parsed by `env.ts` with manual `Bun.env` reads. ClawPilot uses the standard .NET configuration system.

### appsettings.json

```json
{
  "ClawPilot": {
    "TelegramBotToken": "",
    "BotUsername": "@ClawPilotBot",
    "AllowedChatIds": ["123456789", "-1001234567890"],
    "OpenRouterApiKey": "",
    "Model": "anthropic/claude-sonnet-4-20250514",
    "EmbeddingModel": "openai/text-embedding-3-small",
    "SystemPrompt": "You are ClawPilot, a personal AI assistant on Telegram. You are helpful, concise, and proactive.",
    "DatabasePath": "clawpilot.db",
    "MaxResponseTokens": 4096,
    "MaxResponseLength": 4096,
    "SessionTimeoutMinutes": 60
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    },
    "WriteTo": [
      { "Name": "Console" },
      { "Name": "File", "Args": { "path": "logs/clawpilot-.log", "rollingInterval": "Day" } }
    ]
  }
}
```

### ClawPilotOptions.cs

```csharp
namespace ClawPilot.Configuration;

public class ClawPilotOptions
{
    public const string SectionName = "ClawPilot";

    public required string TelegramBotToken { get; set; }
    public string BotUsername { get; set; } = "@ClawPilotBot";
    public HashSet<string> AllowedChatIds { get; set; } = [];

    // OpenRouter — primary LLM provider
    public required string OpenRouterApiKey { get; set; }
    public string Model { get; set; } = "anthropic/claude-sonnet-4-20250514";
    public string EmbeddingModel { get; set; } = "openai/text-embedding-3-small";

    public string? SystemPrompt { get; set; }
    public string DatabasePath { get; set; } = "clawpilot.db";
    public int MaxResponseTokens { get; set; } = 4096;
    public int MaxResponseLength { get; set; } = 4096;
    public int SessionTimeoutMinutes { get; set; } = 60;
}
```

Environment variable override (for secrets):
```bash
export ClawPilot__TelegramBotToken="123456:ABC-DEF..."
export ClawPilot__OpenRouterApiKey="sk-or-v1-..."
```

---

## 13. Skills Engine

NanoClaw's skills engine is a sophisticated overlay system (900+ lines) that manages file merging, backups, rebasing, and conflict resolution for "skills" — bundles of CLAUDE.md instructions, MCP configs, and container customizations.

### Phase 1: Skip

The skills engine is the most complex subsystem and is **not needed for initial launch**. The core loop (Telegram → SK + OpenRouter → respond) works without it.

### Phase 2: Simplified Port

For ClawPilot, skills become simpler because there are no containers:

| NanoClaw Skill Capability | ClawPilot Equivalent |
|---|---|
| Custom `CLAUDE.md` system prompts | Append to `ChatHistory` system message |
| MCP server configs (`.mcp.json`) | Import as SK MCP plugins |
| Container `Dockerfile` customizations | N/A — no containers |
| File overlays with merge/rebase | Config file merging (simpler) |

A minimal skill in ClawPilot would be a JSON/YAML file:

```json
{
  "name": "web-search",
  "version": "1.0.0",
  "systemPromptAppend": "You have access to web search. Use it to answer questions about current events.",
  "mcpServers": {
    "web-search": {
      "type": "local",
      "command": "npx",
      "args": ["-y", "@anthropic/mcp-server-web-search"],
      "tools": ["*"]
    }
  },
  "plugins": []
}
```

---

## 14. Container Strategy

### Decision: Eliminate Containers

NanoClaw runs each agent invocation inside a Docker container for isolation. This makes sense for Claude Agent SDK (which has shell access, file system access, etc.). 

ClawPilot **does not need containers** because:

1. **Semantic Kernel runs in-process** — LLM calls are HTTP requests to OpenRouter; no local subprocess or container needed.
2. **Tools are registered in-process** — SK plugins run as C# delegates, not shell commands.
3. **Security is handled by SK filters** — `IFunctionInvocationFilter` can deny dangerous operations.
4. **MCP servers are sandboxed** — each MCP server runs as its own subprocess with defined capabilities.

### If Container Isolation Is Desired Later

For paranoid-mode operation, MCP servers could run in containers:

```csharp
McpServers = new Dictionary<string, object>
{
    ["sandboxed-shell"] = new McpLocalServerConfig
    {
        Type = "local",
        Command = "docker",
        Args = new List<string>
        {
            "run", "--rm", "-i",
            "--network=none",       // no network
            "--read-only",          // read-only filesystem
            "--memory=256m",        // memory limit
            "clawpilot-sandbox",
            "node", "/app/mcp-server.js"
        },
        Tools = new List<string> { "*" },
    },
},
```

---

## 15. Logging & Observability

NanoClaw uses `pino` with JSON output. ClawPilot uses Serilog.

### Program.cs (logging setup)

```csharp
using Serilog;

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "ClawPilot")
    .CreateLogger();

builder.Host.UseSerilog();
```

### Structured Logging Equivalents

| NanoClaw (pino) | ClawPilot (Serilog) |
|---|---|
| `log.info({ chatId }, "message")` | `_logger.LogInformation("msg {ChatId}", chatId)` |
| `log.error({ err }, "failed")` | `_logger.LogError(ex, "failed")` |
| `log.child({ module: "ipc" })` | `ILogger<IpcService>` (auto-scoped) |
| JSON stdout | JSON file + console (configurable) |

---

## 16. Testing Strategy

NanoClaw uses `vitest` with in-memory SQLite. ClawPilot uses **xUnit** with similar patterns.

### Example Test: Database

```csharp
using Microsoft.EntityFrameworkCore;
using Xunit;

public class DatabaseTests : IDisposable
{
    private readonly ClawPilotDbContext _db;

    public DatabaseTests()
    {
        var options = new DbContextOptionsBuilder<ClawPilotDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new ClawPilotDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task CreateConversation_SetsDefaults()
    {
        var conv = new Conversation { ChatId = "123" };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();

        var saved = await _db.Conversations.FirstAsync();
        Assert.Equal("123", saved.ChatId);
        Assert.False(saved.IsGroup);
        Assert.NotEqual(default, saved.CreatedAt);
    }

    [Fact]
    public async Task Messages_LinkedToConversation()
    {
        var conv = new Conversation { ChatId = "456" };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();

        _db.Messages.Add(new Message
        {
            ConversationId = conv.Id,
            Role = "user",
            Content = "Hello",
        });
        await _db.SaveChangesAsync();

        var messages = await _db.Messages
            .Where(m => m.ConversationId == conv.Id)
            .ToListAsync();
        Assert.Single(messages);
        Assert.Equal("user", messages[0].Role);
    }

    public void Dispose() => _db.Dispose();
}
```

### Example Test: AgentOrchestrator (Mocked)

```csharp
using Moq;
using Xunit;

public class AgentOrchestratorTests
{
    [Fact]
    public void GetOrCreateHistory_ReturnsSameHistory_ForSameConversation()
    {
        var orchestrator = CreateTestOrchestrator();

        var history1 = orchestrator.GetOrCreateHistory("chat-1", "prompt");
        var history2 = orchestrator.GetOrCreateHistory("chat-1", "prompt");

        Assert.Same(history1, history2);
    }

    [Fact]
    public void GetOrCreateHistory_CreatesDifferentHistories_ForDifferentConversations()
    {
        var orchestrator = CreateTestOrchestrator();

        var history1 = orchestrator.GetOrCreateHistory("chat-1", "prompt");
        var history2 = orchestrator.GetOrCreateHistory("chat-2", "prompt");

        Assert.NotSame(history1, history2);
    }

    [Fact]
    public void ResetConversation_RemovesHistory()
    {
        var orchestrator = CreateTestOrchestrator();
        orchestrator.GetOrCreateHistory("chat-1", "prompt");

        orchestrator.ResetConversation("chat-1");

        // Should create a new history after reset
        var newHistory = orchestrator.GetOrCreateHistory("chat-1", "prompt");
        Assert.Single(newHistory); // Only system message
    }
}
```

---

## 17. Deployment

### Program.cs (Full Entry Point)

```csharp
using ClawPilot.AI;
using ClawPilot.AI.Filters;
using ClawPilot.AI.Plugins;
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Database;
using ClawPilot.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Threading.Channels;

var builder = Host.CreateApplicationBuilder(args);

// Logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Services.AddSerilog();

// Configuration
builder.Services.Configure<ClawPilotOptions>(
    builder.Configuration.GetSection(ClawPilotOptions.SectionName));

var config = builder.Configuration
    .GetSection(ClawPilotOptions.SectionName)
    .Get<ClawPilotOptions>()!;

// Database
builder.Services.AddDbContext<ClawPilotDbContext>(options =>
    options.UseSqlite($"Data Source={config.DatabasePath}"));

// Message queue (Telegram → processor)
builder.Services.AddSingleton(
    Channel.CreateUnbounded<IncomingMessage>(
        new UnboundedChannelOptions { SingleReader = true }));

// Services
builder.Services.AddSingleton<ITelegramChannel, TelegramChannel>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddSingleton<GroupQueueService>();

// Hosted services (background workers)
builder.Services.AddHostedService<MessageProcessorService>();
builder.Services.AddHostedService<TelegramHostedService>();

var host = builder.Build();

// Ensure database is created/migrated
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();
    await db.Database.MigrateAsync();
}

await host.RunAsync();
```

### TelegramHostedService.cs

```csharp
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

        // Wire Telegram events into the processing queue
        _telegram.OnMessage += async msg =>
            await _queue.Writer.WriteAsync(msg);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _telegram.StartAsync(ct);

        // Keep alive until cancellation
        await Task.Delay(Timeout.Infinite, ct);
    }
}
```

### Prerequisites

1. **OpenRouter API key** from [openrouter.ai](https://openrouter.ai) (free tier available)
2. **Telegram Bot Token** from @BotFather
3. **.NET 9 SDK** installed

### Running

```bash
cd src/ClawPilot
dotnet run
```

### systemd Service (Linux)

```ini
[Unit]
Description=ClawPilot Telegram AI Assistant
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/dotnet /opt/clawpilot/ClawPilot.dll
WorkingDirectory=/opt/clawpilot
Restart=always
RestartSec=10
Environment=DOTNET_ENVIRONMENT=Production
EnvironmentFile=-/opt/clawpilot/.env

[Install]
WantedBy=multi-user.target
```

### launchd Plist (macOS)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.clawpilot</string>
    <key>ProgramArguments</key>
    <array>
        <string>dotnet</string>
        <string>/opt/clawpilot/ClawPilot.dll</string>
    </array>
    <key>WorkingDirectory</key>
    <string>/opt/clawpilot</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/var/log/clawpilot/stdout.log</string>
    <key>StandardErrorPath</key>
    <string>/var/log/clawpilot/stderr.log</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>DOTNET_ENVIRONMENT</key>
        <string>Production</string>
    </dict>
</dict>
</plist>
```

---

## 18. Detailed Implementation Todo List

> Each task maps to a specific file or class from the plan. Complete in order — later phases depend on earlier ones.

---

### Phase 1: Project Scaffold & Configuration

#### 1.1 Solution & Project Setup

- [x] Create solution: `dotnet new sln -n ClawPilot`
- [x] Create worker project: `dotnet new worker -n ClawPilot -o src/ClawPilot`
- [x] Add project to solution: `dotnet sln add src/ClawPilot/ClawPilot.csproj`
- [x] Create test project: `dotnet new xunit -n ClawPilot.Tests -o tests/ClawPilot.Tests`
- [x] Add test project to solution: `dotnet sln add tests/ClawPilot.Tests/ClawPilot.Tests.csproj`
- [x] Add project reference from tests → src
- [x] Set `TargetFramework` to `net9.0`, enable `Nullable` and `ImplicitUsings` in csproj
- [x] Verify solution builds: `dotnet build`

#### 1.2 NuGet Packages

- [x] Add `Telegram.Bot` v22.* to src project
- [x] Add `Microsoft.SemanticKernel` v1.* to src project
- [x] Add `Microsoft.SemanticKernel.Connectors.OpenAI` v1.* to src project
- [x] Add `Microsoft.EntityFrameworkCore.Sqlite` v9.* to src project
- [x] Add `Microsoft.EntityFrameworkCore.Design` v9.* to src project
- [x] Add `Serilog.Extensions.Hosting` v9.* to src project
- [x] Add `Serilog.Settings.Configuration` v10.* to src project
- [x] Add `Serilog.Sinks.Console` v6.* to src project
- [x] Add `Serilog.Sinks.File` v6.* to src project
- [x] Add `Microsoft.Extensions.Hosting` v9.* to src project
- [x] Add `Moq` and `Microsoft.EntityFrameworkCore.InMemory` to test project
- [x] Verify all packages restore: `dotnet restore`

#### 1.3 Configuration

- [x] Create `src/ClawPilot/Configuration/` directory
- [x] Create `ClawPilotOptions.cs` — strongly-typed config class (section 12)
  - [x] `TelegramBotToken` (required)
  - [x] `BotUsername` (default `@ClawPilotBot`)
  - [x] `AllowedChatIds` (HashSet\<string\>)
  - [x] `OpenRouterApiKey` (required)
  - [x] `Model` (default `anthropic/claude-sonnet-4-20250514`)
  - [x] `EmbeddingModel` (default `openai/text-embedding-3-small`)
  - [x] `SystemPrompt` (nullable)
  - [x] `DatabasePath` (default `clawpilot.db`)
  - [x] `MaxResponseTokens` (default 4096)
  - [x] `MaxResponseLength` (default 4096)
  - [x] `SessionTimeoutMinutes` (default 60)
- [x] Create `appsettings.json` with `ClawPilot` section and `Serilog` section (section 12)
- [x] Create `appsettings.Development.json` with development overrides
- [x] Wire `IOptions<ClawPilotOptions>` binding in DI (to be done in Program.cs later)

#### 1.4 Logging Setup

- [x] Create `src/ClawPilot/Logging/` directory
- [x] Create `SerilogConfig.cs` — helper for Serilog configuration (section 15)
- [x] Configure Serilog: `ReadFrom.Configuration`, `Enrich.FromLogContext`, `Enrich.WithProperty("Application", "ClawPilot")`
- [x] Configure console + rolling file sinks

---

### Phase 2: Database Layer

#### 2.1 Entity Classes

- [x] Create `src/ClawPilot/Database/` directory
- [x] Create `src/ClawPilot/Database/Entities/` directory
- [x] Create `Conversation.cs` entity (section 7)
  - [x] `Id`, `ChatId`, `DisplayName`, `IsGroup`, `SessionId`, `SystemPrompt`, `CreatedAt`, `UpdatedAt`
  - [x] Navigation: `ICollection<Message> Messages`
- [x] Create `Message.cs` entity (section 7)
  - [x] `Id`, `ConversationId`, `Conversation` (nav), `Role`, `Content`, `TelegramMessageId`, `SenderName`, `SenderId`, `Status`, `CreatedAt`
- [x] Create `ScheduledTask.cs` entity (section 7)
  - [x] `Id`, `ChatId`, `Description`, `CronExpression`, `IsActive`, `LastRunAt`, `CreatedAt`

#### 2.2 DbContext & Migrations

- [x] Create `ClawPilotDbContext.cs` (section 7)
  - [x] `DbSet<Conversation>`, `DbSet<Message>`, `DbSet<ScheduledTask>`
  - [x] `OnModelCreating`: unique index on `Conversation.ChatId`
  - [x] `OnModelCreating`: composite index on `Message.(ConversationId, Status)`
  - [x] `OnModelCreating`: index on `Message.CreatedAt`
  - [x] `OnModelCreating`: index on `ScheduledTask.ChatId`
- [x] Install `dotnet-ef` tool if not present
- [x] Create initial EF Core migration: `dotnet ef migrations add InitialCreate`
- [x] Verify migration compiles and applies: `dotnet ef database update`

#### 2.3 Database Tests

- [x] Create `tests/ClawPilot.Tests/DatabaseTests.cs` (section 16)
  - [x] Test: `CreateConversation_SetsDefaults` — verify ChatId, IsGroup, CreatedAt
  - [x] Test: `Messages_LinkedToConversation` — verify message FK and retrieval
  - [x] Test: `ChatId_UniqueConstraint` — verify duplicate ChatId throws
  - [x] Test: `ScheduledTask_Persistence` — verify CRUD for scheduled tasks

---

### Phase 3: Telegram Channel Layer

#### 3.1 Interface & Types

- [x] Create `src/ClawPilot/Channels/` directory
- [x] Create `ITelegramChannel.cs` interface (section 5)
  - [x] `StartAsync(CancellationToken)`
  - [x] `SendTextAsync(long chatId, string text, long? replyToMessageId, CancellationToken)`
  - [x] `SendTypingAsync(long chatId, CancellationToken)`
  - [x] `event Func<IncomingMessage, Task> OnMessage`
- [x] Create `IncomingMessage` record (section 5)
  - [x] `ChatId`, `MessageId`, `Text`, `SenderName`, `SenderId`, `IsGroup`, `GroupName`, `Timestamp`

#### 3.2 TelegramChannel Implementation

- [x] Create `TelegramChannel.cs` (section 5)
  - [x] Constructor: inject `IOptions<ClawPilotOptions>`, `ILogger<TelegramChannel>`; create `TelegramBotClient`
  - [x] `StartAsync`: configure `ReceiverOptions`, start polling with `StartReceiving`
  - [x] `HandleUpdateAsync`: extract text messages, build `IncomingMessage`, fire `OnMessage`
  - [x] `IsAllowed`: filter by `AllowedChatIds` config
  - [x] `SendTextAsync`: send with reply-to support; implement `ChunkText` for 4096 char limit
  - [x] `SendTypingAsync`: send `ChatAction.Typing`
  - [x] `HandleErrorAsync`: log polling errors
  - [x] Static helper: `ChunkText(string text, int maxLen)` — split at paragraph/code boundaries

#### 3.3 TelegramHostedService

- [x] Create `src/ClawPilot/Services/` directory
- [x] Create `TelegramHostedService.cs` (section 17)
  - [x] Inject `ITelegramChannel` and `Channel<IncomingMessage>`
  - [x] Wire `OnMessage` event → `Channel.Writer.WriteAsync`
  - [x] `ExecuteAsync`: call `StartAsync`, then `Task.Delay(Timeout.Infinite)`

#### 3.4 Telegram Tests

- [x] Create `tests/ClawPilot.Tests/TelegramChannelTests.cs` (section 16)
  - [x] Test: `ChunkText_SplitsLongMessages` — verify correct chunking
  - [x] Test: `IsAllowed_FiltersUnauthorizedChats`
  - [x] Test: `IncomingMessage_RecordEquality`

---

### Phase 4: LLM Integration (Semantic Kernel + OpenRouter)

#### 4.1 AgentOrchestrator — Core

- [x] Create `src/ClawPilot/AI/` directory
- [x] Create `AgentOrchestrator.cs` (section 6)
  - [x] Constructor: inject `MemoryService`, `IOptions<ClawPilotOptions>`, `ILogger<AgentOrchestrator>`
  - [x] Build SK `Kernel` with `AddOpenAIChatCompletion` pointing at `https://openrouter.ai/api/v1`
  - [x] Configure model from `ClawPilotOptions.Model`
  - [x] Get `IChatCompletionService` from kernel
  - [x] `ConcurrentDictionary<string, ChatHistory>` for per-conversation histories
  - [x] `GetOrCreateHistory(conversationId, systemPrompt)` — create with system message
  - [x] `ResetConversation(conversationId)` — remove history
  - [x] `Dispose()` — clear all histories

#### 4.2 AgentOrchestrator — SendMessageAsync

- [x] Implement `SendMessageAsync(conversationId, userMessage, systemPrompt, ct)` (section 6)
  - [x] Call `GetOrCreateHistory`
  - [x] Add user message to history
  - [x] Configure `OpenAIPromptExecutionSettings` with `FunctionChoiceBehavior.Auto()` and `MaxTokens`
  - [x] Call `_chatService.GetChatMessageContentAsync(history, settings, _kernel, ct)`
  - [x] Add assistant message to history
  - [x] Return response content (or `"[No response]"` if null)

> **Note**: RAG injection via `MemoryService.RecallAsync` and `MemoryService.SaveAsync` are wired in Phase 5 (Vector Memory). For now, `SendMessageAsync` works without memory.

#### 4.3 OpenRouter Connectivity Test

- [x] Create `tests/ClawPilot.Tests/AgentOrchestratorTests.cs` (section 16)
  - [x] Test: `GetOrCreateHistory_ReturnsSameHistory_ForSameConversation`
  - [x] Test: `GetOrCreateHistory_CreatesDifferentHistories_ForDifferentConversations`
  - [x] Test: `ResetConversation_RemovesHistory`
- [x] Manual smoke test: create orchestrator with real OpenRouter key, send one message, verify response

---

### Phase 5: Vector Memory (sqlite-vec)

#### 5.1 NuGet Package

- [x] Add `Microsoft.SemanticKernel.Connectors.SqliteVec` v1.*-preview to src project
- [x] Verify sqlite-vec native extension loads on macOS (M-series)

#### 5.2 MemoryService Implementation

- [x] Create `MemoryService.cs` in `src/ClawPilot/AI/` (section 7)
  - [x] Constructor: accept `ClawPilotOptions`, build `MemoryBuilder`
  - [x] Configure `WithSqliteVecMemoryStore(options.DatabasePath)`
  - [x] Configure `WithOpenAITextEmbeddingGeneration` pointing at OpenRouter (`text-embedding-3-small`)
  - [x] Build `ISemanticTextMemory`
  - [x] `SaveAsync(conversationId, userMessage, assistantResponse, ct)` — store exchange as embedding
  - [x] `RecallAsync(conversationId, query, limit, ct)` — semantic search with `minRelevanceScore: 0.7`
  - [x] Implement `IAsyncDisposable`

#### 5.3 Wire Memory into AgentOrchestrator

- [x] Update `AgentOrchestrator.SendMessageAsync` to call `_memory.RecallAsync` before LLM call (RAG injection)
- [x] Update `AgentOrchestrator.SendMessageAsync` to call `_memory.SaveAsync` after LLM response
- [x] Inject relevant memories as system message: `"Relevant context from memory:\n..."`

#### 5.4 Memory Tests

- [x] Test: `MemoryService_SaveAndRecall` — save an exchange, recall it by query
- [x] Test: `MemoryService_RelevanceFilter` — verify low-relevance results are filtered
- [x] Test: `AgentOrchestrator_InjectsMemoryContext` — verify RAG context appears in history

---

### Phase 6: Message Processing Pipeline

#### 6.1 MessageProcessorService

- [x] Create `MessageProcessorService.cs` in `src/ClawPilot/Services/` (section 8)
  - [x] Inject `Channel<IncomingMessage>`, `AgentOrchestrator`, `ITelegramChannel`, `IServiceScopeFactory`, `IOptions<ClawPilotOptions>`, `ILogger`
  - [x] `ConcurrentDictionary<string, SemaphoreSlim>` for per-chat locks
  - [x] `ExecuteAsync`: read from channel, dispatch `ProcessMessageAsync` per message

#### 6.2 ProcessMessageAsync

- [x] Implement `ProcessMessageAsync(message, ct)` (section 8)
  - [x] Acquire per-chat semaphore
  - [x] Send typing indicator via `ITelegramChannel.SendTypingAsync`
  - [x] Create DI scope, get `ClawPilotDbContext`
  - [x] Find or create `Conversation` entity
  - [x] Persist incoming message as `Message` entity (role=user, status=processing)
  - [x] Call `BuildSystemPrompt` (base prompt + group context + conversation override)
  - [x] Call `AgentOrchestrator.SendMessageAsync`
  - [x] Persist assistant response as `Message` entity (role=assistant, status=done)
  - [x] Update `Conversation.UpdatedAt`
  - [x] Send response to Telegram via `ITelegramChannel.SendTextAsync`
  - [x] Catch exceptions: log error, send "⚠️ Sorry, something went wrong." to user
  - [x] Release semaphore in `finally`

#### 6.3 BuildSystemPrompt

- [x] Implement `BuildSystemPrompt(conversation, message)` (section 8)
  - [x] Start with `ClawPilotOptions.SystemPrompt` (or default "You are a helpful personal assistant.")
  - [x] If group chat: append group name, sender name, "only respond when directly addressed" rule
  - [x] If `Conversation.SystemPrompt` override exists: append additional context

#### 6.4 CreateConversation Helper

- [x] Implement `CreateConversation(db, message)` (section 8)
  - [x] Populate `ChatId`, `DisplayName`, `IsGroup` from `IncomingMessage`

---

### Phase 7: Program.cs — Full DI Wiring

#### 7.1 Entry Point

- [x] Rewrite `Program.cs` with full DI setup (section 17)
  - [x] Configure Serilog from `builder.Configuration`
  - [x] Bind `ClawPilotOptions` from config section
  - [x] Register `ClawPilotDbContext` with SQLite connection string from `config.DatabasePath`
  - [x] Register `Channel<IncomingMessage>` (unbounded, single reader)
  - [x] Register `ITelegramChannel` → `TelegramChannel` (singleton)
  - [x] Register `MemoryService` (singleton)
  - [x] Register `AgentOrchestrator` (singleton)
  - [x] Register `GroupQueueService` (singleton) — placeholder for Phase 8
  - [x] Register `MessageProcessorService` as hosted service
  - [x] Register `TelegramHostedService` as hosted service
  - [x] On startup: create scope, run `db.Database.MigrateAsync()`
  - [x] Call `host.RunAsync()`

#### 7.2 End-to-End Smoke Test

- [x] Set `ClawPilot__TelegramBotToken` and `ClawPilot__OpenRouterApiKey` env vars
- [x] Run `dotnet run` from `src/ClawPilot/`
- [x] Send a message to the Telegram bot
- [x] Verify: bot receives message → calls OpenRouter → sends response back
- [x] Verify: `Conversation` and `Message` rows created in SQLite DB
- [x] Verify: Serilog logs appear in console and `logs/` directory

---

### Phase 8: Group Chat Support

#### 8.1 GroupQueueService

- [x] Create `GroupQueueService.cs` in `src/ClawPilot/Services/` (section 11)
  - [x] `ConcurrentDictionary<string, Channel<Func<Task>>>` for per-group work queues
  - [x] `ConcurrentDictionary<string, Task>` for per-group processor tasks
  - [x] `EnqueueAsync(groupId, work)` — add work to group's channel; start processor if needed
  - [x] `ProcessQueueAsync(groupId, channel)` — sequentially execute queued work items

#### 8.2 Group Message Handling

- [x] Add `ShouldRespondInGroup(Message)` to `TelegramChannel` (section 11)
  - [x] Always respond in private chats
  - [x] Respond if `@BotUsername` is mentioned in message entities
  - [x] Respond if message is a reply to one of the bot's own messages
  - [x] Otherwise ignore
- [x] Update `HandleUpdateAsync` to call `ShouldRespondInGroup` before firing `OnMessage`

#### 8.3 Group-Specific System Prompt

- [x] Update `BuildSystemPrompt` to include group context: group name, sender name, response rules

#### 8.4 Per-Conversation Session Management

- [x] Implement `RestoreSessionAsync(conversationId, db)` in `AgentOrchestrator` (section 8)
  - [x] Load last 50 messages from DB, ordered by `CreatedAt`
  - [x] Rebuild `ChatHistory` from stored messages
- [x] Call `RestoreSessionAsync` on first message to a conversation after process restart

#### 8.5 Group Chat Tests

- [x] Create `tests/ClawPilot.Tests/GroupQueueTests.cs` (section 16)
  - [x] Test: `EnqueueAsync_SerializesWithinGroup` — verify sequential execution per group
  - [x] Test: `EnqueueAsync_ParallelAcrossGroups` — verify different groups run concurrently
  - [x] Test: `ShouldRespondInGroup_MentionDetection`
  - [x] Test: `ShouldRespondInGroup_ReplyDetection`

---

### Phase 9: SK Plugins (Tool System)

#### 9.1 MessagingPlugin

- [x] Create `src/ClawPilot/AI/Plugins/` directory
- [x] Create `MessagingPlugin.cs` (section 9)
  - [x] `[KernelFunction("send_message")]` — send text to a Telegram chat
  - [x] `[KernelFunction("search_messages")]` — keyword search on `Message` table via EF `Like`

#### 9.2 SchedulerPlugin

- [x] Create `SchedulerPlugin.cs` (section 9)
  - [x] `[KernelFunction("schedule_task")]` — insert `ScheduledTask` entity

#### 9.3 UtilityPlugin

- [x] Create `UtilityPlugin.cs` (section 9)
  - [x] `[KernelFunction("get_current_datetime")]` — return UTC + Unix timestamp
  - [x] `[KernelFunction("recall_memory")]` — proxy to `MemoryService.RecallAsync`

#### 9.4 Register Plugins

- [x] Register `MessagingPlugin`, `SchedulerPlugin`, `UtilityPlugin` in `AgentOrchestrator` via `builder.Plugins.AddFromType<>()`
- [x] Ensure plugin DI dependencies (ITelegramChannel, IServiceScopeFactory) are resolvable

#### 9.5 Plugin Tests

- [x] Test: `MessagingPlugin_SendMessage_CallsTelegram`
- [x] Test: `MessagingPlugin_SearchMessages_ReturnsResults`
- [x] Test: `SchedulerPlugin_ScheduleTask_PersistsToDb`
- [x] Test: `UtilityPlugin_GetCurrentDateTime_ReturnsValidFormat`

---

### Phase 10: Security Filter

#### 10.1 SecurityFilter Implementation

- [x] Create `src/ClawPilot/AI/Filters/` directory
- [x] Create `SecurityFilter.cs` (section 10)
  - [x] Implement `IFunctionInvocationFilter`
  - [x] Define `BlockedToolPatterns`: `["shell", "bash", "exec", "run_command"]`
  - [x] Define `DangerousArgPatterns`: `["rm -rf", "sudo", "chmod 777", "mkfs", "> /dev/"]`
  - [x] `OnFunctionInvocationAsync`: check function name against blocked patterns
  - [x] `OnFunctionInvocationAsync`: check arguments against dangerous patterns
  - [x] Block by setting `context.Result` with error message and returning (skip `next()`)
  - [x] Log pre-invocation and post-invocation details

#### 10.2 Register Filter

- [x] Register `SecurityFilter` as `IFunctionInvocationFilter` in `AgentOrchestrator`'s Kernel builder
- [x] Alternatively register in `Program.cs` DI

#### 10.3 Security Tests

- [x] Test: `SecurityFilter_BlocksDangerousTools` — verify blocked tool names are rejected
- [x] Test: `SecurityFilter_BlocksDangerousArgs` — verify dangerous argument patterns are rejected
- [x] Test: `SecurityFilter_AllowsSafeTools` — verify normal plugins pass through

---

### Phase 11: Task Scheduler Service

#### 11.1 TaskSchedulerService

- [x] Create `TaskSchedulerService.cs` in `src/ClawPilot/Services/` (section 3 — Quartz or Timer-based)
  - [x] Background service that periodically checks `ScheduledTask` table
  - [x] Parse cron expressions to determine if task is due
  - [x] Execute due tasks via `AgentOrchestrator` (send prompt to LLM, respond in chat)
  - [x] Update `ScheduledTask.LastRunAt` after execution
  - [x] Skip inactive tasks (`IsActive = false`)

#### 11.2 Register Service

- [x] Register `TaskSchedulerService` as hosted service in `Program.cs`

#### 11.3 Scheduler Tests

- [x] Test: `TaskSchedulerService_ExecutesDueTasks`
- [x] Test: `TaskSchedulerService_SkipsInactiveTasks`
- [x] Test: `TaskSchedulerService_UpdatesLastRunAt`

---

### Phase 12: Error Handling & Resilience

#### 12.1 LLM Error Surfacing

- [x] In `MessageProcessorService.ProcessMessageAsync`: catch `HttpRequestException` / SK exceptions from OpenRouter
- [x] Format user-facing error: "⚠️ Sorry, something went wrong." (no internal details)
- [x] Log full exception with Serilog (including status code, model, conversation ID)

#### 12.2 Telegram Error Handling

- [x] Handle `ApiRequestException` from Telegram.Bot (rate limits, invalid chat, etc.)
- [x] Log and skip messages that can't be sent (chat deleted, bot kicked from group)

#### 12.3 Database Error Handling

- [x] Handle EF Core `DbUpdateException` — log and surface generic error to user
- [x] Ensure database operations don't block the message processing pipeline

#### 12.4 Graceful Degradation

- [x] If `MemoryService` fails (sqlite-vec not available): log warning, continue without RAG
- [x] If vector memory save fails: log warning, don't block response delivery
- [x] If session restore fails on startup: log warning, start with empty history

---

### Phase 13: Polish & Production Readiness

#### 13.1 Message Chunking (4096 char limit)

- [x] Implement smart `ChunkText` in `TelegramChannel` (section 5)
  - [x] Split at paragraph boundaries (`\n\n`) when possible
  - [x] Split at code block boundaries (`` ``` ``) when possible
  - [x] Fallback: split at last newline before limit
  - [x] Last resort: hard split at 4096 chars
- [x] Send chunks sequentially with small delay to avoid rate limits

#### 13.2 Chat History Cap

- [x] In `AgentOrchestrator.GetOrCreateHistory`: cap `ChatHistory` at N messages (configurable, default 50)
- [x] When over cap: trim oldest non-system messages
- [x] Older context handled by sqlite-vec RAG (already wired in Phase 5)

#### 13.3 Graceful Shutdown

- [x] Register `IHostApplicationLifetime` in relevant services
- [x] On shutdown: complete in-flight message processing
- [x] On shutdown: flush Serilog sinks
- [x] On shutdown: dispose `MemoryService` and `AgentOrchestrator`

#### 13.4 Health Checks

- [x] Add basic health check endpoint or periodic self-check log
- [x] Check: SQLite database is accessible
- [x] Check: Telegram bot token is valid (call `getMe`)
- [x] Check: OpenRouter API key is valid (test completion with minimal tokens)

#### 13.5 Rate Limiting

- [x] Telegram API: respect 30 msg/s global limit, 1 msg/s per chat for normal messages
- [x] Add per-chat send throttle (e.g., `SemaphoreSlim` or token bucket)
- [x] OpenRouter: no explicit rate limiting needed (errors surface to user per Q&A #1)

#### 13.6 Structured Logging Polish

- [x] Ensure all log messages use structured properties: `{ChatId}`, `{ConversationId}`, `{Function}`, etc.
- [x] Add correlation ID to message processing (trace a message through the full pipeline)
- [x] Verify log outputs are valid JSON in file sink

---

### Phase 14: Testing

#### 14.1 Unit Tests

- [x] `DatabaseTests.cs` — entity persistence, constraints, indexes (Phase 2.3)
- [x] `TelegramChannelTests.cs` — chunking, filtering (Phase 3.4)
- [x] `AgentOrchestratorTests.cs` — history management, reset (Phase 4.3)
- [x] `GroupQueueTests.cs` — serialization, parallelism (Phase 8.5)
- [x] `SecurityFilterTests.cs` — blocked tools, dangerous args (Phase 10.3)
- [x] Plugin tests — messaging, scheduler, utility (Phase 9.5)
- [x] Scheduler service tests (Phase 11.3)

#### 14.2 Integration Tests

- [x] End-to-end: fake Telegram message → DB → SK → DB → fake Telegram send
- [x] Memory round-trip: save exchange → recall by semantic query
- [x] Session restore: persist messages → restart → verify history rebuilt
- [x] Group queue: concurrent messages in same group → verify sequential processing

#### 14.3 Manual Acceptance Tests

- [x] Send private message to bot → receive AI response
- [x] Send message in group → bot responds only when @mentioned or replied to
- [x] Send very long prompt → verify response is chunked correctly
- [x] Send rapid messages → verify per-chat serialization (no interleaving)
- [x] Ask bot to recall past conversation → verify vector memory RAG works
- [x] Ask bot to schedule a task → verify `ScheduledTask` row created
- [x] Kill and restart bot → verify session resumes with history context

---

### Phase 15: Deployment

#### 15.1 Build & Publish

- [x] Create publish script: `dotnet publish -c Release -o publish/`
- [x] Verify published output runs standalone

#### 15.2 macOS (launchd)

- [x] Create `com.clawpilot.plist` launchd configuration (section 17)
- [x] Set `ProgramArguments` to dotnet + published DLL path
- [x] Set `WorkingDirectory`, `RunAtLoad`, `KeepAlive`
- [x] Set `EnvironmentVariables` for `DOTNET_ENVIRONMENT=Production`
- [x] Configure `StandardOutPath` and `StandardErrorPath` for log files
- [x] Test: `launchctl load com.clawpilot.plist` → verify bot starts and stays alive

#### 15.3 Linux (systemd)

- [x] Create `clawpilot.service` systemd unit file (section 17)
- [x] Set `ExecStart`, `WorkingDirectory`, `Restart=always`, `RestartSec=10`
- [x] Set `Environment` for secrets (or use env file)
- [x] Test: `systemctl start clawpilot` → verify bot starts and stays alive

#### 15.4 Environment & Secrets

- [x] Document required env vars: `ClawPilot__TelegramBotToken`, `ClawPilot__OpenRouterApiKey`
- [x] Document optional env var overrides for all `ClawPilotOptions` properties
- [x] Create `.env.example` template file

---

### Phase 16: Skills Engine (Optional — Future)

#### 16.1 Skill Manifest

- [x] Define skill manifest JSON schema (section 13)
  - [x] `name`, `version`, `description`
  - [x] `systemPromptAppend` — text to append to base system prompt
  - [x] `mcpServers` — map of MCP server configs to import as SK plugins
  - [x] `plugins` — reserved for future native SK plugin bundles
- [x] Create `skills/` directory in project root for skill files

#### 16.2 Skill Loader

- [x] Create `SkillLoaderService` — read skill manifests from directory
- [x] Append `systemPromptAppend` to `BuildSystemPrompt` for enabled skills
- [x] Import `mcpServers` via SK `ImportMcpPluginAsync` (section 9 — MCP integration)
- [x] Register skill loader in DI

#### 16.3 Skill Management

- [x] Implement skill install command (download from URL/git, place in `skills/`)
- [x] Implement skill uninstall command (remove from `skills/`)
- [x] Implement skill list command (show enabled/disabled skills)
- [x] Persist enabled/disabled state in SQLite

#### 16.4 Skill Tests

- [x] Test: `SkillLoader_LoadsManifest` — parse valid skill JSON
- [x] Test: `SkillLoader_AppendsSystemPrompt` — verify prompt injection
- [x] Test: `SkillLoader_ImportsMcpServers` — verify MCP plugin registration

---

## 19. Open Questions & Risks

| # | Question | Risk | Mitigation | Answer |
|---|---|---|---|---|
| 1 | OpenRouter rate limits and reliability? | Service outage blocks all LLM calls | ~~Implement retry with exponential backoff~~ — surface error to the user directly | **Decided**: Throw error to the user; no retry/fallback logic |
| 2 | OpenRouter cost management across 200+ models? | Unexpected cost spikes if model is changed | Default to cost-efficient model (Claude Sonnet); ~~per-chat token budget tracking~~ not needed | **Decided**: Use cost-efficient model; no budget tracking |
| 3 | Telegram 4096 char limit — how to handle long agent responses? | Truncated or ugly split messages | Smart chunking at paragraph/code boundaries | **Decided**: Smart chunking at paragraph/code boundaries |
| 4 | In-process agent (no container) — is isolation sufficient? | SK plugins run in host process | Only register safe plugins; use MCP for untrusted code | **Decided**: Follow mitigation strategy |
| 5 | SK's SqliteVec connector is "Preview" — stability? | Breaking changes in vector store API | Pin version, abstract behind `ISemanticTextMemory` interface | **Decided**: Follow mitigation strategy |
| 6 | sqlite-vec native extension loading on different platforms? | May not work on all OS/architectures | Test on macOS (M-series), Linux (x64), provide fallback to keyword search | **Decided**: Follow mitigation strategy |
| 7 | Chat history grows unbounded in memory? | OOM with many long conversations | Cap `ChatHistory` at N messages, rely on sqlite-vec RAG for older context | **Decided**: Follow mitigation strategy |
| 8 | OpenRouter's function calling support varies by model? | Tool calls may fail on some models | Test primary models (Claude, GPT-4o); document which models support tools | **Decided**: Follow mitigation strategy |
| 9 | Embedding model costs for vector memory? | Every message pair generates an embedding call | Use efficient model (`text-embedding-3-small`); batch embeddings; make memory opt-in | **Decided**: Use efficient embedding model and batching |
| 10 | ~~Alternative: should we support Copilot SDK alongside SK?~~ | ~~Maintenance burden of two paths~~ | ~~Keep Copilot SDK as documented alternative~~ | **Decided**: No — SK + OpenRouter is the only supported path. All Copilot SDK references removed from the plan. |

---

## Summary

ClawPilot is a simplification and enhancement of NanoClaw. By switching from Claude Agent SDK (Docker containers + file IPC) to **Semantic Kernel + OpenRouter** (in-process orchestration + HTTP API), we eliminate ~40% of the codebase: container-runner, container-runtime, agent-runner, IPC layer, mount-security, and Dockerfile management.

Key improvements over the original plan (informed by deep research):
- **OpenRouter** as the sole LLM provider — no CLI installation, no subscription required, access to 200+ models
- **Semantic Kernel** provides a mature, Microsoft-backed orchestration layer with native function calling, prompt templates, and filters
- **sqlite-vec vector memory** adds semantic retrieval (RAG) — a new capability NanoClaw entirely lacks
- **SK plugins** replace NanoClaw's MCP stdio tools with idiomatic C# `[KernelFunction]` attributes
- **Error handling**: LLM errors surface directly to the user (no retry/fallback complexity)
- **Cost model**: default to cost-efficient model (Claude Sonnet), efficient embedding model with batching

The Telegram channel remains simpler than WhatsApp (official API, no QR auth). The result is a single .NET process with ~8 core files that handles the full message lifecycle end-to-end, with the added bonus of long-term semantic memory.
