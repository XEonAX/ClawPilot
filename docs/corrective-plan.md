# ClawPilot — Corrective Plan

> Audit of the current implementation against `plan.md`. Each item describes a deviation and the fix required. Items are grouped by severity.

**Audit date**: 2026-02-22  
**Build status**: ✅ Builds (0 errors, 0 warnings)  
**Test status**: ✅ 57/57 passing  

---

## Table of Contents

1. [Critical — Runtime Failures](#1-critical--runtime-failures)
2. [Major — Functional Deviations](#2-major--functional-deviations)
3. [Moderate — Behavioral Differences](#3-moderate--behavioral-differences)
4. [Minor — Cosmetic / Structural](#4-minor--cosmetic--structural)
5. [Missing Tests](#5-missing-tests)
6. [Summary Checklist](#6-summary-checklist)

---

## 1. Critical — Runtime Failures

These issues will cause crashes or silent failures at runtime.

### 1.1 MemoryService DI Resolution Failure

**Plan** (§7, §17): `MemoryService` takes `ClawPilotOptions` and builds `ISemanticTextMemory` internally via `MemoryBuilder`.

**Implementation**: `MemoryService` constructor signature is:
```csharp
public MemoryService(ClawPilotOptions options, ILogger<MemoryService> logger,
    IEmbeddingGenerator<string, Embedding<float>>? embeddingService = null)
```

`Program.cs` registers it as:
```csharp
builder.Services.AddSingleton<MemoryService>();
```

**Problem**: DI cannot resolve `ClawPilotOptions` directly — only `IOptions<ClawPilotOptions>` is registered. This will throw at startup when the DI container tries to construct `MemoryService`.

**Fix**: Change `MemoryService` constructor to accept `IOptions<ClawPilotOptions>` and unwrap `.Value`.

### 1.2 Vector Memory Is Non-Functional (No Embedding Service Registered)

**Plan** (§7): `MemoryService` internally creates an embedding generator via `memoryBuilder.WithOpenAITextEmbeddingGeneration(...)` using the OpenRouter endpoint and `EmbeddingModel` config.

**Implementation**: `MemoryService` relies on an injected `IEmbeddingGenerator<string, Embedding<float>>` which is never registered in `Program.cs`. Since the parameter is nullable with `= null`, the service silently disables itself — `SaveAsync` and `RecallAsync` both return early.

**Result**: All vector memory / RAG capabilities are dead code. The plan's core differentiator (sqlite-vec semantic retrieval) does not function.

**Fix**: Rewrite `MemoryService` to use the `MemoryBuilder` pattern as the plan specifies (§7), which internally handles embedding generation. This aligns better with SK patterns and provides future extensibility. Remove the `IEmbeddingGenerator` constructor parameter and instead build `ISemanticTextMemory` internally via:
```csharp
var memoryBuilder = new MemoryBuilder();
memoryBuilder.WithSqliteVecMemoryStore(options.DatabasePath);
memoryBuilder.WithOpenAITextEmbeddingGeneration(
    modelId: options.EmbeddingModel,
    apiKey: options.OpenRouterApiKey,
    endpoint: "https://openrouter.ai/api/v1");
_memory = memoryBuilder.Build();
```

### 1.3 Database Uses EnsureCreated Instead of Migrations

**Plan** (§2.2, §17): Create EF Core migrations and run `db.Database.MigrateAsync()` at startup.

**Implementation**: 
- The `Database/Migrations/` directory is **empty** — no migrations exist.
- `Program.cs` calls `db.Database.EnsureCreatedAsync()` instead of `db.Database.MigrateAsync()`.

**Problem**: `EnsureCreated` cannot apply schema changes after initial creation. Any entity changes will require manual DB deletion. This makes the app non-upgradeable in production.

**Fix**:
1. Generate initial migration: `dotnet ef migrations add InitialCreate -p src/ClawPilot -s src/ClawPilot`
2. Change `Program.cs` to `await db.Database.MigrateAsync();`

---

## 2. Major — Functional Deviations

These cause incorrect behavior vs. the plan's spec.

### 2.1 BuildSystemPrompt Ignores Global System Prompt

**Plan** (§8, MessageProcessorService): `BuildSystemPrompt` starts from the **global** config prompt:
```csharp
var prompt = _options.SystemPrompt ?? "You are a helpful personal assistant.";
// ...
if (conv.SystemPrompt is not null)
    prompt += $"\n\nAdditional context:\n{conv.SystemPrompt}";
```

**Implementation** (`MessageProcessorService.BuildSystemPrompt`):
```csharp
var prompt = conversation.SystemPrompt ?? "You are a helpful personal assistant.";
```

**Problem**: The global `ClawPilotOptions.SystemPrompt` (set in `appsettings.json`) is **never used**. The conversation-level override replaces it entirely instead of appending to it. New conversations with no custom prompt get the hardcoded default instead of the configured one.

**Fix**: Start from `_options.SystemPrompt`, then append `conversation.SystemPrompt` as additional context — matching the plan.

### 2.2 AgentOrchestrator Does Not Build Its Own Kernel

**Plan** (§6): `AgentOrchestrator` constructor internally builds the SK `Kernel` with `Kernel.CreateBuilder()`, adds `OpenAIChatCompletion`, registers plugins via `AddFromType<>()`, and wires the security filter.

**Implementation**: `AgentOrchestrator` receives a pre-built `Kernel` via constructor injection. Kernel construction and plugin registration are in `Program.cs`.

**Impact**: 
- Architecture differs from plan — the orchestrator is no longer self-contained.
- Plugins registered via `AddFromObject()` (manually constructed) instead of `AddFromType<>()` (DI-resolved). This means plugin instances are created at startup and share state across the entire app lifetime, which may cause issues with scoped dependencies.
- The `AgentOrchestrator` loses its documented responsibility as the "single class that manages the SK lifecycle."

**Fix**: Move kernel construction back into `AgentOrchestrator` constructor per the plan, or document this as an intentional deviation. If keeping the current approach, at minimum ensure scoped dependencies (like `IServiceScopeFactory` in plugins) work correctly.

### 2.3 OpenRouter Endpoint Configuration Differs

**Plan** (§6): Uses the `endpoint` parameter:
```csharp
builder.AddOpenAIChatCompletion(
    modelId: ..., apiKey: ...,
    endpoint: new Uri("https://openrouter.ai/api/v1"));
```

**Implementation** (`Program.cs`): Uses `httpClient` with `BaseAddress`:
```csharp
kernelBuilder.AddOpenAIChatCompletion(
    modelId: config.Model,
    apiKey: config.OpenRouterApiKey,
    httpClient: new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1") });
```

**Problem**: The `httpClient` approach may cause subtle issues — SK's OpenAI connector may append `/chat/completions` differently when using `BaseAddress` vs the `endpoint` parameter. Also, the `HttpClient` is not disposed properly (created inline, not via `IHttpClientFactory`).

**Fix**: Use the `endpoint` parameter as per plan, or if `httpClient` is required, use `IHttpClientFactory` for proper lifecycle management.

### 2.4 Session Restore Not Wired Into Message Pipeline

**Plan** (§8.4): "Call `RestoreSessionAsync` on first message to a conversation after process restart."

**Implementation**: `AgentOrchestrator.RestoreSessionAsync` exists but is **never called** — not from `MessageProcessorService`, not from `Program.cs`, not from anywhere in production code (only from tests).

**Result**: After a process restart, all conversation history is lost. The "two-tier persistence" described in §8 doesn't work — the in-memory `ChatHistory` starts empty, and since vector memory is also broken (§1.2), there's no context recovery at all.

**Fix**: In `MessageProcessorService.ProcessMessageAsync`, before calling `SendMessageAsync`, check if a history exists for this `chatKey`. If not, load recent messages from DB and call `RestoreSessionAsync`.

### 2.5 Telegram DropPendingUpdates Not Set

**Plan** (§5, TelegramChannel.StartAsync):
```csharp
var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = [UpdateType.Message],
    DropPendingUpdates = true,
};
```

**Implementation**: `DropPendingUpdates` is **not set** (defaults to `false`).

**Problem**: On restart, the bot will receive and process all messages that arrived while it was offline. This can cause a flood of responses and unexpected behavior.

**Fix**: Add `DropPendingUpdates = true` to `ReceiverOptions`.

### 2.6 HealthCheck Service Missing Telegram and OpenRouter Checks

**Plan** (§13.4): Health checks should verify:
- [x] SQLite database is accessible
- [ ] Telegram bot token is valid (call `getMe`)
- [ ] OpenRouter API key is valid (test completion with minimal tokens)

**Implementation**: `HealthCheckService` only checks the database with `SELECT 1`.

**Fix**: Add Telegram `getMe` call and a minimal OpenRouter API call (e.g., list models or tiny completion) to `RunChecksAsync`.

---

## 3. Moderate — Behavioral Differences

### 3.1 Target Framework: net9.0 Instead of net8.0

**Plan** (§4): `TargetFramework` is `net8.0`.  
**Implementation**: Both `ClawPilot.csproj` and `ClawPilot.Tests.csproj` target `net9.0`.

All NuGet package versions also differ:
| Package | Plan | Actual |
|---|---|---|
| `Microsoft.EntityFrameworkCore.Sqlite` | `8.*` | `9.*` |
| `Microsoft.EntityFrameworkCore.Design` | `8.*` | `9.*` |
| `Serilog.Extensions.Hosting` | `8.*` | `9.*` |
| `Microsoft.Extensions.Hosting` | `8.*` | `9.*` |

**Impact**: Not necessarily wrong (net9.0 is newer), but diverges from plan. May affect the stated "`.NET 8 SDK` installed" prerequisite in §17.

**Fix**: Keep net9.0. Update plan and docs to match the implementation — net9.0 is the latest and will have longer support. Update the §17 prerequisite from `.NET 8 SDK` to `.NET 9 SDK`.

### 3.2 MemoryService Uses Different API Pattern Than Plan

**Plan** (§7): Uses the legacy `ISemanticTextMemory` + `MemoryBuilder` pattern:
```csharp
var memoryBuilder = new MemoryBuilder();
memoryBuilder.WithSqliteVecMemoryStore(options.DatabasePath);
memoryBuilder.WithOpenAITextEmbeddingGeneration(...);
_memory = memoryBuilder.Build();
```

**Implementation**: Uses the newer `VectorStoreCollection` / `IEmbeddingGenerator` pattern with custom `MemoryRecord` class.

**Impact**: The newer pattern may be more correct for current SK versions, but the `MemoryRecord` entity with `[VectorStoreVector(1536)]` hard-codes the embedding dimension. If a different embedding model is used (e.g., one that doesn't produce 1536-dim vectors), this will fail.

**Fix**: Make the vector dimension configurable or derive it from the embedding model config. Also verify the chosen pattern works end-to-end with the sqlite-vec connector (currently untestable because the embedding service is never registered — see §1.2).

### 3.3 MemoryRecord Entity Misplaced

**Plan** (§4): Project structure shows `Database/Entities/MemoryRecord.cs`.  
**Implementation**: `MemoryRecord` class is defined at the bottom of `AI/MemoryService.cs`.

**Fix**: Move `MemoryRecord` to its own file in `Database/Entities/MemoryRecord.cs` per plan.

### 3.4 AllowedChatIds Empty Means "Allow All"

**Plan** (§5, `IsAllowed`):
```csharp
return _options.AllowedChatIds.Contains(chatId);
```

**Implementation**:
```csharp
if (_options.AllowedChatIds.Count == 0)
    return true;
return _options.AllowedChatIds.Contains(chatId);
```

**Impact**: When no chat IDs are configured, the bot responds to **everyone**. The plan's version would silently ignore all messages. The implementation's behavior is more practical for development but less secure.

**Fix**: Document this "open mode" behavior explicitly, or add a separate `AllowAllChats` boolean config option for clarity. Consider the security implications.

### 3.5 SendTextAsync Missing ParseMode.Markdown

**Plan** (§5):
```csharp
await _bot.SendMessage(
    chatId: chatId,
    text: chunk,
    parseMode: ParseMode.Markdown,
    ...);
```

**Implementation**: No `parseMode` parameter in `SendMessage` call.

**Impact**: LLM responses containing markdown formatting (headers, bold, code blocks) will display as raw text instead of formatted.

**Fix**: Add `parseMode: ParseMode.Markdown` (or `ParseMode.MarkdownV2` for better compatibility).

### 3.6 Missing OpenRouterConfig.cs File

**Plan** (§4): Project structure includes `AI/OpenRouterConfig.cs`.  
**Implementation**: This file does not exist.

**Fix**: Create `OpenRouterConfig.cs` if there's OpenRouter-specific configuration logic to centralize, or remove from plan if not needed.

### 3.7 Skill MCP Server Integration Not Wired

**Plan** (§16.2): "Import `mcpServers` via SK `ImportMcpPluginAsync`"

**Implementation**: `SkillLoaderService` loads manifests and `McpServerConfig` objects, but never calls `ImportMcpPluginAsync`. The MCP server configs in skill manifests are loaded and stored but completely unused.

**Fix**: Wire MCP server import from loaded skills into the SK kernel (e.g., during kernel construction or via a separate initialization step).

### 3.8 Skill Enabled/Disabled State Not Persisted to SQLite

**Plan** (§16.3): "Persist enabled/disabled state in SQLite."

**Implementation**: `SetSkillEnabled` only changes an in-memory property on `SkillManifest`. The change is lost on restart.

**Fix**: Store skill enabled/disabled state in the SQLite database (via a new `Skills` table or append to the skill JSON on disk).

### 3.9 McpServerConfig Missing `Type` Field

**Plan** (§13, skill manifest JSON):
```json
{
  "type": "local",
  "command": "npx",
  "args": [...]
}
```

**Implementation** (`McpServerConfig`): Has `Command`, `Args`, `Env` but no `Type` field.

**Fix**: Add `public string Type { get; set; } = "local";` to `McpServerConfig`.

---

## 4. Minor — Cosmetic / Structural

### 4.1 Extra Package: Serilog.Settings.Configuration

**Plan**: Not listed.  
**Implementation**: `Serilog.Settings.Configuration` Version 10.0.0 is in `.csproj`.

**Impact**: Required for `ReadFrom.Configuration()` — needed but not documented in plan.

**Fix**: Add `Serilog.Settings.Configuration` to the plan's dependency list — it's required for the `ReadFrom.Configuration()` approach used.

### 4.2 UtilityPlugin.recall_memory Signature Differs

**Plan** (§9):
```csharp
public async Task<string> RecallMemoryAsync(
    [Description("What to search for")] string query,
    Kernel kernel)
```
Uses `kernel.GetRequiredService<MemoryService>()` with hardcoded `"global"` conversationId.

**Implementation**:
```csharp
public async Task<string> RecallMemoryAsync(
    [Description("The conversation ID to search within")] string conversationId,
    [Description("The query to search for relevant memories")] string query, ...)
```
Takes explicit `conversationId` parameter.

**Impact**: The LLM must know and pass the `conversationId`, which it may not have. The plan's approach (`"global"` scope, `Kernel` injection) is simpler for the LLM to use.

**Fix**: Update implementation to match plan — use the global approach with `kernel.GetRequiredService<MemoryService>()` and hardcoded `"global"` conversationId. This is more practical since the LLM doesn't need to track conversation IDs.

### 4.3 MessagingPlugin.search_messages Output Format

**Plan**: Returns JSON via `JsonSerializer.Serialize(messages)`.  
**Implementation**: Returns formatted strings `[role] sender: content`.

**Fix**: Update implementation to return JSON via `JsonSerializer.Serialize(messages)` as per plan. The JSON format is more structured and easier for the LLM to parse for relevant information.

### 4.4 Plugin Parameter Types: string vs long for chatId

**Plan**: `send_message` and `schedule_task` take `string chatId`.  
**Implementation**: Takes `long chatId`.

**Impact**: Using `long` is more type-safe for Telegram chat IDs. Either approach works with SK function calling.

**Fix**: Keep `long` for better type safety. Update plan to specify `long chatId` instead of `string chatId`.

### 4.5 Deploy Plist Log Paths Differ

**Plan**: `StandardOutPath` → `/tmp/clawpilot.stdout.log`  
**Implementation**: `StandardOutPath` → `/var/log/clawpilot/stdout.log`

**Impact**: Implementation's `/var/log/` is more appropriate for production.

**Fix**: Update plan to match implementation.

### 4.6 Deploy Plist `dotnet` Path

**Plan**: `/usr/local/share/dotnet/dotnet`  
**Implementation**: `dotnet` (searches PATH)

**Fix**: Implementation is more portable. Update plan.

### 4.7 SystemD Service Uses EnvironmentFile

**Plan**: Inline `Environment=ClawPilot__TelegramBotToken=<token>`  
**Implementation**: `EnvironmentFile=-/opt/clawpilot/.env`

**Impact**: Implementation is better practice for secrets management.

**Fix**: Update plan to match.

---

## 5. Missing Tests

The plan explicitly requires these tests that do not exist in the test suite:

### From Phase 2 (Database)
- [ ] `ChatId_UniqueConstraint` — verify duplicate ChatId throws

### From Phase 3 (Telegram)
- [ ] `IsAllowed_FiltersUnauthorizedChats`

### From Phase 5 (Vector Memory)
- [ ] `MemoryService_SaveAndRecall` — save exchange, recall by query (real vector test)
- [ ] `MemoryService_RelevanceFilter` — verify low-relevance filtered
- [ ] `AgentOrchestrator_InjectsMemoryContext` — verify RAG context in history

### From Phase 8 (Group Chat)
- [ ] `ShouldRespondInGroup_MentionDetection`
- [ ] `ShouldRespondInGroup_ReplyDetection`

### From Phase 11 (Task Scheduler)
- [ ] `TaskSchedulerService_ExecutesDueTasks` — full end-to-end (not just `IsDue`)
- [ ] `TaskSchedulerService_SkipsInactiveTasks`
- [ ] `TaskSchedulerService_UpdatesLastRunAt`

**Note**: Some memory tests (§5) can only be written once the embedding service is properly wired (§1.2).

---

## 6. Summary Checklist

### Critical (Fix Before Running)

| # | Issue | Section |
|---|---|---|
| 1.1 | MemoryService DI cannot resolve `ClawPilotOptions` | §1.1 |
| 1.2 | No `IEmbeddingGenerator` registered — vector memory is dead code | §1.2 |
| 1.3 | No EF migrations — uses `EnsureCreated` instead of `MigrateAsync` | §1.3 |

### Major (Fix Before Production)

| # | Issue | Section |
|---|---|---|
| 2.1 | Global system prompt from config is never used | §2.1 |
| 2.2 | AgentOrchestrator doesn't build its own kernel (architecture drift) | §2.2 |
| 2.3 | OpenRouter endpoint configured via `httpClient` instead of `endpoint` | §2.3 |
| 2.4 | `RestoreSessionAsync` never called — no session recovery | §2.4 |
| 2.5 | `DropPendingUpdates` not set — message flood on restart | §2.5 |
| 2.6 | HealthCheck missing Telegram + OpenRouter checks | §2.6 |

### Moderate (Behavioral Drift)

| # | Issue | Section |
|---|---|---|
| 3.1 | net9.0 instead of net8.0 | §3.1 |
| 3.2 | MemoryService API pattern differs from plan | §3.2 |
| 3.3 | MemoryRecord entity misplaced (in MemoryService.cs) | §3.3 |
| 3.4 | Empty AllowedChatIds = allow all (not in plan) | §3.4 |
| 3.5 | SendTextAsync missing ParseMode.Markdown | §3.5 |
| 3.6 | Missing `OpenRouterConfig.cs` file | §3.6 |
| 3.7 | Skill MCP server configs loaded but never imported | §3.7 |
| 3.8 | Skill enabled state not persisted to DB | §3.8 |
| 3.9 | McpServerConfig missing `Type` field | §3.9 |

### Missing Tests: 11 tests

---

## Recommended Fix Order

1. **§1.1 + §1.2** — Fix MemoryService DI and register embedding generator (unblocks vector memory)
2. **§1.3** — Create migration, switch to `MigrateAsync`
3. **§2.1** — Fix BuildSystemPrompt to use global config prompt
4. **§2.4** — Wire RestoreSessionAsync into message pipeline
5. **§2.5** — Add DropPendingUpdates
6. **§2.3** — Fix OpenRouter endpoint configuration
7. **§3.5** — Add ParseMode.Markdown
8. **§2.6** — Complete health checks
9. **§3.3** — Move MemoryRecord to separate file
10. **§3.7 + §3.8 + §3.9** — Complete skills engine wiring
11. **§5** — Add missing tests
12. **§2.2** — Move kernel construction back into AgentOrchestrator per plan
13. **§3.1** — Update plan and docs to specify net9.0
