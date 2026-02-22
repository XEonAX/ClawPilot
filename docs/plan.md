# ClawPilot — Implementation Plan

> **Fork of NanoClaw**: .NET 8+ rewrite using **GitHub Copilot SDK** (instead of Claude Agent SDK) and **Telegram** (instead of WhatsApp).

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Component Mapping](#2-component-mapping)
3. [Project Structure](#3-project-structure)
4. [Telegram Channel Layer](#4-telegram-channel-layer)
5. [Copilot SDK Integration](#5-copilot-sdk-integration)
6. [Database Layer](#6-database-layer)
7. [Session & Conversation Management](#7-session--conversation-management)
8. [Tool System (IPC Replacement)](#8-tool-system-ipc-replacement)
9. [Hooks & Security](#9-hooks--security)
10. [Group Chat Support](#10-group-chat-support)
11. [Configuration System](#11-configuration-system)
12. [Skills Engine](#12-skills-engine)
13. [Container Strategy](#13-container-strategy)
14. [Logging & Observability](#14-logging--observability)
15. [Testing Strategy](#15-testing-strategy)
16. [Deployment](#16-deployment)
17. [Migration Checklist](#17-migration-checklist)
18. [Open Questions & Risks](#18-open-questions--risks)

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
SQLite  (EF Core — message queue + conversation state)
    ↓
CopilotSessionManager  (in-process, no containers)
    ↓
CopilotClient / CopilotSession  (GitHub Copilot SDK)
    ↓
Custom Tools (AIFunctionFactory) + MCP servers
    ↓
response → SQLite → Telegram
```

**Key architectural difference**: NanoClaw spawns Docker containers per agent run with Claude SDK inside each. ClawPilot runs **in-process** — the Copilot SDK manages its own `copilot` CLI subprocess via JSON-RPC. This eliminates the container-runner, agent-runner, and file-based IPC layers entirely.

---

## 2. Component Mapping

| NanoClaw Component | File(s) | ClawPilot Equivalent | Technology |
|---|---|---|---|
| WhatsApp channel | `channels/whatsapp.ts` | Telegram channel | `Telegram.Bot` NuGet |
| Claude Agent SDK | `container/agent-runner/` | Copilot SDK | `GitHub.Copilot.SDK` NuGet |
| Docker containers | `container-runner.ts`, `container-runtime.ts` | **Eliminated** — in-process | CopilotClient manages CLI |
| File-based IPC | `ipc.ts`, `ipc-mcp-stdio.ts` | **Eliminated** — native tools | AIFunctionFactory + MCP |
| SQLite (better-sqlite3) | `db.ts` | SQLite (EF Core) | `Microsoft.EntityFrameworkCore.Sqlite` |
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

---

## 3. Project Structure

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
│       │   │   └── ScheduledTask.cs
│       │   └── Migrations/
│       │
│       ├── Channels/
│       │   ├── ITelegramChannel.cs     # Interface
│       │   └── TelegramChannel.cs      # Telegram.Bot integration
│       │
│       ├── Copilot/
│       │   ├── CopilotSessionManager.cs  # Session lifecycle
│       │   ├── ToolRegistry.cs           # Custom tool definitions
│       │   └── HookHandlers.cs           # PreToolUse, PostToolUse, etc.
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
│       ├── CopilotSessionManagerTests.cs
│       ├── TelegramChannelTests.cs
│       └── GroupQueueTests.cs
│
└── docs/
    ├── research.md
    └── plan.md
```

### ClawPilot.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <!-- Telegram -->
    <PackageReference Include="Telegram.Bot" Version="22.*" />

    <!-- Copilot SDK -->
    <PackageReference Include="GitHub.Copilot.SDK" Version="*" />

    <!-- Database -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="8.*" />

    <!-- Logging -->
    <PackageReference Include="Serilog.Extensions.Hosting" Version="8.*" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.*" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.*" />

    <!-- Configuration -->
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="8.*" />
  </ItemGroup>

</Project>
```

---

## 4. Telegram Channel Layer

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

## 5. Copilot SDK Integration

This is the core replacement for NanoClaw's Docker container + Claude Agent SDK pipeline. Instead of spawning a container with `claude --model ... --system-prompt ...`, we create an in-process `CopilotClient` that manages a `copilot` CLI subprocess.

### CopilotSessionManager.cs

```csharp
using GitHub.Copilot.SDK;

namespace ClawPilot.Copilot;

/// <summary>
/// Manages the CopilotClient lifecycle and per-conversation sessions.
/// Replaces NanoClaw's container-runner.ts + agent-runner entirely.
/// </summary>
public class CopilotSessionManager : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly ToolRegistry _toolRegistry;
    private readonly HookHandlers _hooks;
    private readonly ILogger<CopilotSessionManager> _logger;
    private readonly ClawPilotOptions _options;

    // Active sessions keyed by conversation ID (chat ID string)
    private readonly ConcurrentDictionary<string, CopilotSession> _sessions = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    public CopilotSessionManager(
        ToolRegistry toolRegistry,
        HookHandlers hooks,
        IOptions<ClawPilotOptions> options,
        ILogger<CopilotSessionManager> logger)
    {
        _toolRegistry = toolRegistry;
        _hooks = hooks;
        _options = options.Value;
        _logger = logger;

        _client = new CopilotClient(new CopilotClientOptions
        {
            // Optional: BYOK for custom model provider
            // Provider = new ProviderConfig
            // {
            //     Type = "openai",
            //     BaseUrl = "https://api.openai.com/v1",
            //     ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            // },
        });
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _client.StartAsync(ct);
        _logger.LogInformation("Copilot client started");
    }

    /// <summary>
    /// Get or create a session for a given conversation.
    /// Maps to NanoClaw's per-container agent lifecycle.
    /// </summary>
    public async Task<CopilotSession> GetOrCreateSessionAsync(
        string conversationId,
        string systemPrompt,
        CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(conversationId, out var existing))
            return existing;

        await _sessionLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_sessions.TryGetValue(conversationId, out existing))
                return existing;

            var session = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = _options.Model ?? "gpt-4o",

                // Use conversationId as sessionId for persistence/resume
                SessionId = $"clawpilot-{conversationId}",

                // System prompt — equivalent to NanoClaw's CLAUDE.md injection
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = systemPrompt,
                },

                // Register custom tools (replaces NanoClaw's IPC MCP tools)
                Tools = _toolRegistry.GetTools(),

                // Auto-approve all tool calls (like NanoClaw's PermissionMode.AutoApproval)
                OnPermissionRequest = PermissionHandler.ApproveAll,

                // Hooks for security, logging, context injection
                Hooks = _hooks.CreateHooks(),

                // Infinite sessions with auto-compaction
                // (NanoClaw truncates at 200k tokens)
                InfiniteSessions = new InfiniteSessionConfig
                {
                    Enabled = true,
                },

                // Enable streaming for real-time "typing" indicators
                Streaming = true,
            }, ct);

            _sessions[conversationId] = session;
            _logger.LogInformation("Created session for conversation {Id}", conversationId);
            return session;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Send a message and wait for the complete response.
    /// This is the primary entry point — replaces NanoClaw's
    /// container spawn → IPC write → IPC read pipeline.
    /// </summary>
    public async Task<string> SendMessageAsync(
        string conversationId,
        string userMessage,
        string systemPrompt,
        CancellationToken ct = default)
    {
        var session = await GetOrCreateSessionAsync(conversationId, systemPrompt, ct);

        var reply = await session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = userMessage,
        }, ct);

        return reply?.Data?.Content ?? "[No response from Copilot]";
    }

    /// <summary>
    /// Clean up a session when a conversation is reset or archived.
    /// Equivalent to NanoClaw's container cleanup.
    /// </summary>
    public async Task DestroySessionAsync(string conversationId)
    {
        if (_sessions.TryRemove(conversationId, out var session))
        {
            await session.DisposeAsync();
            _logger.LogInformation("Destroyed session for conversation {Id}", conversationId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, session) in _sessions)
        {
            await session.DisposeAsync();
        }
        _sessions.Clear();

        await _client.DisposeAsync();
    }
}
```

### How Agent Execution Changes

| NanoClaw | ClawPilot |
|---|---|
| `spawnContainer()` → Docker run | `_client.CreateSessionAsync()` → in-process |
| Write to IPC file → agent reads stdin | `session.SendAndWaitAsync()` → JSON-RPC |
| Agent runs Claude SDK in container | Copilot SDK talks to `copilot` CLI subprocess |
| Read IPC file for response | `SendAndWaitAsync` returns `AssistantMessageEvent` |
| Container dies after timeout | Session persists, auto-compacts (infinite sessions) |
| ~5-10s container startup overhead | ~instant (session creation is lightweight) |

### BYOK (Bring Your Own Key) Configuration

If you want to use a different model provider instead of Copilot's default:

```csharp
// In CopilotSessionManager constructor or config
var client = new CopilotClient(new CopilotClientOptions
{
    Provider = new ProviderConfig
    {
        Type = "openai",         // or "azure", "anthropic", "ollama"
        BaseUrl = "https://api.openai.com/v1",
        ApiKey = config["OpenAI:ApiKey"],
    },
});
```

Supported providers: `openai`, `azure`, `anthropic`, `ollama` — any OpenAI-compatible endpoint.

---

## 6. Database Layer

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

    /// <summary>Copilot session ID for resume</summary>
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
    }
}
```

### Comparison with NanoClaw's DB

NanoClaw has these tables: `conversations`, `messages`, `conversation_permissions`, `scheduled_tasks`, `auth_sessions`. ClawPilot drops `auth_sessions` (Telegram needs no auth flow) and `conversation_permissions` (simplified — use `AllowedChatIds` config). The core `conversations` + `messages` + `scheduled_tasks` carry over directly.

---

## 7. Session & Conversation Management

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
    private readonly CopilotSessionManager _copilot;
    private readonly ITelegramChannel _telegram;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageProcessorService> _logger;
    private readonly ClawPilotOptions _options;

    // Per-chat semaphores to serialize processing within a chat
    // (like NanoClaw's container lock per conversation)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatLocks = new();

    public MessageProcessorService(
        Channel<IncomingMessage> messageQueue,
        CopilotSessionManager copilot,
        ITelegramChannel telegram,
        IServiceScopeFactory scopeFactory,
        IOptions<ClawPilotOptions> options,
        ILogger<MessageProcessorService> logger)
    {
        _messageQueue = messageQueue;
        _copilot = copilot;
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

            // Send to Copilot and get response
            var response = await _copilot.SendMessageAsync(
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

NanoClaw tracks conversation state in SQLite and passes full history to each container run. Copilot SDK has **native session persistence** — pass a custom `sessionId` and resume later:

```csharp
// Creating with a deterministic session ID
var session = await client.CreateSessionAsync(new SessionConfig
{
    SessionId = $"clawpilot-{chatId}",  // deterministic per chat
    // ... other config
});

// Later, resume the same session (conversation history preserved server-side)
var resumed = await client.ResumeSessionAsync($"clawpilot-{chatId}");
```

This eliminates NanoClaw's need to store and replay full message history.

---

## 8. Tool System (IPC Replacement)

NanoClaw exposes tools to Claude via a custom MCP stdio server (`ipc-mcp-stdio.ts`) inside each container. The agent-runner pipes tool calls through file-based IPC. ClawPilot replaces this entirely with **Copilot SDK custom tools** registered via `AIFunctionFactory`.

### ToolRegistry.cs

```csharp
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace ClawPilot.Copilot;

/// <summary>
/// Registers custom tools available to the Copilot agent.
/// Replaces NanoClaw's IPC MCP server tools:
///   - send_message, schedule_task, get_current_datetime, etc.
/// </summary>
public class ToolRegistry
{
    private readonly ITelegramChannel _telegram;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(
        ITelegramChannel telegram,
        IServiceScopeFactory scopeFactory,
        ILogger<ToolRegistry> logger)
    {
        _telegram = telegram;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public AIFunction[] GetTools()
    {
        return
        [
            // Equivalent to NanoClaw's "send_message" MCP tool
            AIFunctionFactory.Create(SendMessageTool,
                "send_message",
                "Send a message to a Telegram chat. Use this to proactively message the user."),

            // Equivalent to NanoClaw's "schedule_task" MCP tool
            AIFunctionFactory.Create(ScheduleTaskTool,
                "schedule_task",
                "Schedule a recurring task. Takes a description and cron expression."),

            // Equivalent to NanoClaw's "get_current_datetime" MCP tool
            AIFunctionFactory.Create(GetCurrentDateTimeTool,
                "get_current_datetime",
                "Get the current date and time in UTC and the user's timezone."),

            // Equivalent to NanoClaw's "search_messages" MCP tool
            AIFunctionFactory.Create(SearchMessagesTool,
                "search_messages",
                "Search past conversation messages by keyword."),
        ];
    }

    private async Task<string> SendMessageTool(string chatId, string message)
    {
        await _telegram.SendTextAsync(long.Parse(chatId), message);
        return $"Message sent to {chatId}";
    }

    private async Task<string> ScheduleTaskTool(
        string chatId, string description, string cronExpression)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClawPilotDbContext>();

        db.ScheduledTasks.Add(new ScheduledTask
        {
            ChatId = chatId,
            Description = description,
            CronExpression = cronExpression,
        });
        await db.SaveChangesAsync();

        return $"Task scheduled: {description} ({cronExpression})";
    }

    private Task<string> GetCurrentDateTimeTool()
    {
        var utcNow = DateTimeOffset.UtcNow;
        return Task.FromResult(
            $"UTC: {utcNow:yyyy-MM-dd HH:mm:ss}\n" +
            $"Unix: {utcNow.ToUnixTimeSeconds()}");
    }

    private async Task<string> SearchMessagesTool(string query, int limit = 20)
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

### MCP Server Integration (Optional)

For more complex tool ecosystems, Copilot SDK supports external MCP servers directly:

```csharp
var session = await client.CreateSessionAsync(new SessionConfig
{
    McpServers = new Dictionary<string, object>
    {
        // Example: filesystem MCP server for file access
        ["filesystem"] = new McpLocalServerConfig
        {
            Type = "local",
            Command = "npx",
            Args = new List<string> { "-y", "@modelcontextprotocol/server-filesystem", "/data" },
            Tools = new List<string> { "*" },
        },

        // Example: GitHub MCP server
        ["github"] = new McpLocalServerConfig
        {
            Type = "local",
            Command = "npx",
            Args = new List<string> { "-y", "@modelcontextprotocol/server-github" },
            Env = new Dictionary<string, string>
            {
                ["GITHUB_TOKEN"] = Environment.GetEnvironmentVariable("GITHUB_TOKEN")!,
            },
            Tools = new List<string> { "*" },
        },
    },

    // Custom tools AND MCP tools work together
    Tools = toolRegistry.GetTools(),
});
```

---

## 9. Hooks & Security

NanoClaw has a specific `PreToolUse` bash command sanitization hook in the container-runner that checks for shell command patterns. The Copilot SDK has a full hook system.

### HookHandlers.cs

```csharp
using GitHub.Copilot.SDK;

namespace ClawPilot.Copilot;

public class HookHandlers
{
    private readonly ILogger<HookHandlers> _logger;

    // Dangerous tool patterns to block
    // (NanoClaw blocks bash commands with 'rm -rf', 'sudo', etc.)
    private static readonly string[] BlockedToolPatterns =
        ["shell", "bash", "exec", "run_command"];

    private static readonly string[] DangerousArgPatterns =
        ["rm -rf", "sudo", "chmod 777", "mkfs", "> /dev/"];

    public HookHandlers(ILogger<HookHandlers> logger)
    {
        _logger = logger;
    }

    public SessionHooks CreateHooks() => new()
    {
        OnPreToolUse = OnPreToolUseAsync,
        OnPostToolUse = OnPostToolUseAsync,
        OnSessionStart = OnSessionStartAsync,
        OnErrorOccurred = OnErrorOccurredAsync,
    };

    private Task<PreToolUseHookOutput?> OnPreToolUseAsync(
        PreToolUseHookInput input, HookInvocation invocation)
    {
        _logger.LogDebug("[{SessionId}] PreToolUse: {Tool} args={Args}",
            invocation.SessionId, input.ToolName, input.ToolArgs);

        // Block dangerous tools entirely
        if (BlockedToolPatterns.Any(p =>
            input.ToolName.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Blocked tool: {Tool}", input.ToolName);
            return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
            {
                PermissionDecision = "deny",
                PermissionDecisionReason =
                    $"Tool '{input.ToolName}' is not permitted in ClawPilot",
            });
        }

        // Check for dangerous argument patterns
        var argsJson = input.ToolArgs?.ToString() ?? "";
        if (DangerousArgPatterns.Any(p =>
            argsJson.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Blocked dangerous args in {Tool}: {Args}",
                input.ToolName, argsJson);
            return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
            {
                PermissionDecision = "deny",
                PermissionDecisionReason = "Arguments contain blocked patterns",
            });
        }

        return Task.FromResult<PreToolUseHookOutput?>(new PreToolUseHookOutput
        {
            PermissionDecision = "allow",
        });
    }

    private Task<PostToolUseHookOutput?> OnPostToolUseAsync(
        PostToolUseHookInput input, HookInvocation invocation)
    {
        _logger.LogDebug("[{SessionId}] PostToolUse: result length={Len}",
            invocation.SessionId, input.ToolResult?.ToString()?.Length ?? 0);

        // Could sanitize output here (strip secrets, PII, etc.)
        return Task.FromResult<PostToolUseHookOutput?>(null);
    }

    private Task<SessionStartHookOutput?> OnSessionStartAsync(
        SessionStartHookInput input, HookInvocation invocation)
    {
        // Inject runtime context (like NanoClaw's dynamic system prompt additions)
        return Task.FromResult<SessionStartHookOutput?>(new SessionStartHookOutput
        {
            AdditionalContext =
                $"Current UTC time: {DateTimeOffset.UtcNow:O}\n" +
                "You are ClawPilot, a personal assistant running on Telegram.",
        });
    }

    private Task<ErrorHookOutput?> OnErrorOccurredAsync(
        ErrorHookInput input, HookInvocation invocation)
    {
        _logger.LogError("Session {SessionId} error: {Error}",
            invocation.SessionId, input.Error);
        return Task.FromResult<ErrorHookOutput?>(null);
    }
}
```

### Security Model Comparison

| NanoClaw | ClawPilot |
|---|---|
| Docker isolation per agent | In-process (less isolation, but no shell access) |
| Mount allow-list (`mount-security.ts`) | Not needed — no filesystem mounts |
| PreToolUse bash sanitization | `OnPreToolUse` hook with deny patterns |
| Network namespace per container | Process-level (consider restricting MCP servers) |
| `PHONE_ID` env whitelist | `AllowedChatIds` config array |
| File-based IPC (temp files) | In-memory JSON-RPC (no temp files) |

---

## 10. Group Chat Support

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

## 11. Configuration System

NanoClaw uses `.env` files parsed by `env.ts` with manual `Bun.env` reads. ClawPilot uses the standard .NET configuration system.

### appsettings.json

```json
{
  "ClawPilot": {
    "TelegramBotToken": "",
    "BotUsername": "@ClawPilotBot",
    "AllowedChatIds": ["123456789", "-1001234567890"],
    "Model": "gpt-4o",
    "SystemPrompt": "You are ClawPilot, a personal AI assistant on Telegram. You are helpful, concise, and proactive.",
    "DatabasePath": "clawpilot.db",
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
    public string? Model { get; set; } = "gpt-4o";
    public string? SystemPrompt { get; set; }
    public string DatabasePath { get; set; } = "clawpilot.db";
    public int MaxResponseLength { get; set; } = 4096;
    public int SessionTimeoutMinutes { get; set; } = 60;

    // Optional: BYOK provider config
    public ProviderOptions? Provider { get; set; }
}

public class ProviderOptions
{
    public string Type { get; set; } = "openai";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
}
```

Environment variable override (for secrets):
```bash
export ClawPilot__TelegramBotToken="123456:ABC-DEF..."
export ClawPilot__Provider__ApiKey="sk-..."
```

---

## 12. Skills Engine

NanoClaw's skills engine is a sophisticated overlay system (900+ lines) that manages file merging, backups, rebasing, and conflict resolution for "skills" — bundles of CLAUDE.md instructions, MCP configs, and container customizations.

### Phase 1: Skip

The skills engine is the most complex subsystem and is **not needed for initial launch**. The core loop (Telegram → Copilot → respond) works without it.

### Phase 2: Simplified Port

For ClawPilot, skills become simpler because there are no containers:

| NanoClaw Skill Capability | ClawPilot Equivalent |
|---|---|
| Custom `CLAUDE.md` system prompts | Append to `SystemMessage.Content` |
| MCP server configs (`.mcp.json`) | Add to `McpServers` dictionary |
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
  "tools": []
}
```

---

## 13. Container Strategy

### Decision: Eliminate Containers

NanoClaw runs each agent invocation inside a Docker container for isolation. This makes sense for Claude Agent SDK (which has shell access, file system access, etc.). 

ClawPilot **does not need containers** because:

1. **Copilot SDK manages its own subprocess** — the `copilot` CLI is spawned and managed by CopilotClient. No need for Docker.
2. **Tools are registered in-process** — custom tools run as C# delegates, not shell commands.
3. **Security is handled by hooks** — PreToolUse can deny dangerous operations.
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

## 14. Logging & Observability

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

## 15. Testing Strategy

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

### Example Test: CopilotSessionManager (Mocked)

```csharp
using Moq;
using Xunit;

public class CopilotSessionManagerTests
{
    [Fact]
    public async Task GetOrCreateSession_ReturnsSameSession_ForSameConversation()
    {
        // Use test doubles — the real CopilotClient needs the CLI
        var manager = CreateTestManager();
        await manager.StartAsync();

        var session1 = await manager.GetOrCreateSessionAsync("chat-1", "prompt");
        var session2 = await manager.GetOrCreateSessionAsync("chat-1", "prompt");

        Assert.Same(session1, session2);
    }

    [Fact]
    public async Task GetOrCreateSession_CreatesDifferentSessions_ForDifferentConversations()
    {
        var manager = CreateTestManager();
        await manager.StartAsync();

        var session1 = await manager.GetOrCreateSessionAsync("chat-1", "prompt");
        var session2 = await manager.GetOrCreateSessionAsync("chat-2", "prompt");

        Assert.NotSame(session1, session2);
    }
}
```

---

## 16. Deployment

### Program.cs (Full Entry Point)

```csharp
using ClawPilot.Channels;
using ClawPilot.Configuration;
using ClawPilot.Copilot;
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
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<HookHandlers>();
builder.Services.AddSingleton<CopilotSessionManager>();
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

// Start Copilot client
var copilot = host.Services.GetRequiredService<CopilotSessionManager>();
await copilot.StartAsync();

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

1. **`copilot` CLI** must be installed and in PATH (Copilot SDK communicates with it via JSON-RPC)
2. **Telegram Bot Token** from @BotFather
3. **.NET 8 SDK** installed
4. (Optional) GitHub Copilot subscription for default model, or configure BYOK

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
Environment=ClawPilot__TelegramBotToken=<token>

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
        <string>/usr/local/share/dotnet/dotnet</string>
        <string>/opt/clawpilot/ClawPilot.dll</string>
    </array>
    <key>WorkingDirectory</key>
    <string>/opt/clawpilot</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/clawpilot.stdout.log</string>
    <key>StandardErrorPath</key>
    <string>/tmp/clawpilot.stderr.log</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>DOTNET_ENVIRONMENT</key>
        <string>Production</string>
    </dict>
</dict>
</plist>
```

---

## 17. Migration Checklist

### Phase 1: Core Loop (MVP)

- [ ] `dotnet new worker` project scaffold
- [ ] Add NuGet packages (`Telegram.Bot`, `GitHub.Copilot.SDK`, `EF Core SQLite`, `Serilog`)
- [ ] `ClawPilotOptions` configuration class
- [ ] `ClawPilotDbContext` with `Conversation`, `Message` entities
- [ ] EF Core initial migration
- [ ] `TelegramChannel` — connect, receive messages, send responses
- [ ] `CopilotSessionManager` — create client, create session, `SendAndWaitAsync`
- [ ] `MessageProcessorService` — wire Telegram → DB → Copilot → DB → Telegram
- [ ] `Program.cs` — DI setup, hosted services
- [ ] Basic system prompt
- [ ] End-to-end test: send Telegram message → get AI response

### Phase 2: Tools & Hooks

- [ ] `ToolRegistry` — `send_message`, `get_current_datetime`, `search_messages`
- [ ] `HookHandlers` — `OnPreToolUse` security filtering
- [ ] `ScheduleTaskTool` + `TaskSchedulerService`
- [ ] Session persistence (resume sessions after restart)
- [ ] Error handling and graceful degradation

### Phase 3: Group Chat

- [ ] `GroupQueueService` — serialize per-group processing
- [ ] `ShouldRespondInGroup()` — @mention and reply detection
- [ ] Group-specific system prompt injection
- [ ] Per-conversation session management

### Phase 4: Polish & Production

- [ ] Structured logging with Serilog
- [ ] Health checks
- [ ] Graceful shutdown (`IHostApplicationLifetime`)
- [ ] Message chunking for long responses (4096 char limit)
- [ ] Rate limiting (Telegram API limits)
- [ ] Deployment scripts (systemd / launchd)
- [ ] Unit and integration tests

### Phase 5: Skills Engine (Optional)

- [ ] Skill manifest format (JSON)
- [ ] Skill loader (append system prompt, register MCP servers)
- [ ] Skill install/uninstall commands
- [ ] Skill marketplace or git-based distribution

---

## 18. Open Questions & Risks

| # | Question | Risk | Mitigation |
|---|---|---|---|
| 1 | Does `copilot` CLI need a GitHub Copilot subscription? | Might limit user base | BYOK support — configure OpenAI/Anthropic directly |
| 2 | Copilot SDK session persistence — how long do sessions survive? | May lose context on CLI restart | Store conversation summary in DB as fallback |
| 3 | Telegram 4096 char limit — how to handle long agent responses? | Truncated or ugly split messages | Smart chunking at paragraph/code boundaries |
| 4 | In-process agent (no container) — is isolation sufficient? | Custom tools run in host process | Only register safe tools; use MCP for untrusted code |
| 5 | Copilot SDK is pre-1.0 — API stability? | Breaking changes | Pin version, abstract behind interfaces |
| 6 | `copilot` CLI cold start latency? | First message slow | Pre-warm client at startup (`StartAsync`) |
| 7 | Infinite sessions compaction — what gets dropped? | Important context lost | Periodically persist key facts to DB |
| 8 | Concurrent sessions — CLI subprocess limits? | Resource exhaustion with many chats | Pool sessions, set max concurrent limit |

---

## Summary

ClawPilot is a simplification of NanoClaw. By switching from Claude Agent SDK (Docker containers + file IPC) to GitHub Copilot SDK (in-process + JSON-RPC), we eliminate ~40% of the codebase: container-runner, container-runtime, agent-runner, IPC layer, mount-security, and Dockerfile management. The Telegram channel is simpler than WhatsApp (official API, no QR auth). The result is a single .NET process with ~6 core files that handles the full message lifecycle end-to-end.
