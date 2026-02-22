# NanoClaw Deep Research Report

## Executive Summary

NanoClaw is a personal AI assistant framework that connects WhatsApp messaging (via the Baileys library) to Claude AI agents running in isolated Linux containers (Docker or Apple Container). It is designed as a lightweight, security-first alternative to larger projects like OpenClaw, emphasizing simplicity (single Node.js process, ~15 source files), true OS-level isolation, and AI-native development workflows. The entire codebase is intentionally small enough (~35k tokens) to fit within a single AI context window.

---

## 1. Architecture Overview

### High-Level Flow

```
WhatsApp (Baileys) → SQLite → Polling Loop → Container (Claude Agent SDK) → Response → WhatsApp
```

NanoClaw runs as a **single Node.js process** on the host that:
1. Maintains a WhatsApp Web connection via `@whiskeysockets/baileys`
2. Stores all messages in SQLite (`better-sqlite3`)
3. Polls for new messages every 2 seconds
4. Spawns isolated Docker containers running Claude Agent SDK for each conversation
5. Routes agent responses back to WhatsApp

### Core Subsystems

The host process runs three independent loops:

| Subsystem | File | Interval | Purpose |
|-----------|------|----------|---------|
| Message Loop | `src/index.ts` | 2s (`POLL_INTERVAL`) | Polls SQLite for new messages, dispatches to containers |
| Scheduler Loop | `src/task-scheduler.ts` | 60s (`SCHEDULER_POLL_INTERVAL`) | Checks for due scheduled tasks, spawns containers |
| IPC Watcher | `src/ipc.ts` | 1s (`IPC_POLL_INTERVAL`) | Reads filesystem-based IPC from containers (messages, tasks, group registration) |

---

## 2. Source Code Module Breakdown

### `src/index.ts` — Orchestrator (488 lines)

The main entry point and event loop. Key responsibilities:
- **State management**: Loads/saves `lastTimestamp`, `lastAgentTimestamp` (per-group cursor), `sessions` (Claude session IDs), and `registeredGroups` from SQLite
- **Message loop**: An infinite `while(true)` loop that polls `getNewMessages()`, groups messages by chat JID, checks trigger patterns, and either pipes messages to an active container (via IPC) or enqueues them for a new container
- **Agent invocation**: `runAgent()` prepares task/group snapshots and calls `runContainerAgent()`, tracking session IDs and streaming output back to WhatsApp
- **Recovery**: On startup, `recoverPendingMessages()` scans for unprocessed messages (crash recovery between advancing cursor and processing)
- **Graceful shutdown**: SIGTERM/SIGINT handlers that drain the queue and disconnect channels
- **Cursor management**: Dual-cursor system — `lastTimestamp` tracks "seen" messages globally; `lastAgentTimestamp[chatJid]` tracks per-group processing. On error, the per-group cursor rolls back (unless output was already sent to the user, preventing duplicates)

### `src/channels/whatsapp.ts` — WhatsApp Channel (290 lines)

Implements the `Channel` interface using Baileys:
- **Connection**: Creates a WASocket with multi-file auth state persistence in `store/auth/`
- **QR/Pairing code auth**: If not authenticated, exits with a notification (auth is handled by a separate `whatsapp-auth.ts` script)
- **Reconnection**: Auto-reconnects on close (unless explicitly logged out)
- **Message handling**: Extracts text from various WhatsApp message types (conversation, extendedText, image/video captions). Detects bot messages via `ASSISTANT_HAS_OWN_NUMBER` flag or assistant name prefix
- **LID translation**: Translates WhatsApp's internal Linked ID JIDs to phone-based JIDs using signal repository
- **Outgoing queue**: Messages sent while disconnected are queued and flushed on reconnect
- **Group metadata sync**: Fetches all participating groups daily, caches in SQLite
- **Typing indicators**: Sends `composing`/`paused` presence updates

### `src/db.ts` — SQLite Database (636 lines)

All persistent state in a single `store/messages.db` file with 7 tables:

| Table | Purpose |
|-------|---------|
| `chats` | Chat metadata (JID, name, last activity, channel type, is_group) |
| `messages` | Full message content with sender info and timestamps |
| `scheduled_tasks` | Task definitions (cron/interval/once, prompts, status) |
| `task_run_logs` | Execution history per task run |
| `router_state` | Key-value store for cursor positions |
| `sessions` | Claude session IDs per group folder |
| `registered_groups` | Group registration config with container settings |

Notable patterns:
- Incremental schema migrations via try/catch ALTER TABLE (idempotent)
- JSON migration from legacy file-based state (`router_state.json`, `sessions.json`, `registered_groups.json`)
- Bot message filtering via both `is_bot_message` flag AND legacy content prefix as backstop

### `src/container-runner.ts` — Container Spawning (646 lines)

The most complex module. Handles:

**Volume mount construction** (`buildVolumeMounts()`):
- Main group: mounts entire project root + own group folder
- Non-main groups: only their own group folder + global folder (read-only)
- Per-group Claude sessions directory (`data/sessions/{group}/.claude/`) → `/home/node/.claude/`
- Per-group IPC namespace (`data/ipc/{group}/`) → `/workspace/ipc/`
- Agent-runner source mounted read-only (recompiled at container start)
- Additional mounts validated against external allowlist

**Container execution**:
- Spawns `docker run -i --rm` with stdio pipes
- Passes secrets (ANTHROPIC_API_KEY, CLAUDE_CODE_OAUTH_TOKEN) via stdin JSON, never on disk
- Streaming output parsing: uses `---NANOCLAW_OUTPUT_START---` / `---NANOCLAW_OUTPUT_END---` sentinel markers to extract JSON results from stdout in real-time
- Timeout management: hard timeout (configurable, default 30min) resets on each output marker. Timeout after output is treated as idle cleanup (success), not failure
- Detailed logging to `groups/{folder}/logs/container-{timestamp}.log`

**Settings initialization**: Creates `settings.json` in each group's session dir enabling:
- Agent Swarms (`CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`)
- Additional directory CLAUDE.md loading
- Auto-memory feature

### `src/group-queue.ts` — Concurrency Control (340 lines)

Per-group queue with global concurrency limit (`MAX_CONCURRENT_CONTAINERS`, default 5):

- **State per group**: active flag, idle-waiting flag, pending messages/tasks, process handle, retry count
- **Priority**: Tasks are prioritized over messages (tasks won't be re-discovered from SQLite like messages)
- **Retry with backoff**: Up to 5 retries with exponential backoff (5s × 2^n)
- **Container reuse via IPC**: `sendMessage()` writes JSON files to the active container's IPC input directory instead of spawning a new container. The `notifyIdle()` / `closeStdin()` mechanism manages container lifecycle
- **Graceful shutdown**: Detaches containers (doesn't kill them), letting them finish via idle timeout

### `src/ipc.ts` — Inter-Process Communication (380 lines)

File-based IPC between host and containers:

- Scans `data/ipc/{groupFolder}/messages/` and `data/ipc/{groupFolder}/tasks/` directories
- **Authorization model**: Identity determined by IPC directory path (container can only write to its own group's IPC dir)
  - Main group: can send messages to any chat, schedule for any group, register groups, refresh group metadata
  - Non-main groups: can only send to own chat, schedule for own group
- **Task operations**: `schedule_task`, `pause_task`, `resume_task`, `cancel_task`, `register_group`, `refresh_groups`
- Error handling: failed IPC files moved to `data/ipc/errors/`

### `src/router.ts` — Message Formatting (47 lines)

Minimal module:
- `formatMessages()`: Wraps messages in XML format: `<messages><message sender="..." time="...">content</message></messages>`
- `formatOutbound()`: Strips `<internal>...</internal>` blocks from agent output
- `findChannel()` / `routeOutbound()`: Route messages to the correct channel by JID pattern

### `src/task-scheduler.ts` — Scheduled Tasks (223 lines)

- Polls `getDueTasks()` every 60 seconds
- Tasks run as full agent containers with all tools
- Session support: `group` context mode resumes group session; `isolated` creates fresh session
- Task containers get closed promptly (10s delay) after producing a result, unlike interactive containers which stay alive for 30min idle timeout
- Calculates `next_run` from cron expressions (with timezone support) or intervals

### `src/mount-security.ts` — Mount Validation (420 lines)

Security-critical module:
- Allowlist stored at `~/.config/nanoclaw/mount-allowlist.json` (outside project, never mounted into containers)
- Default blocked patterns: `.ssh`, `.gnupg`, `.aws`, `.kube`, `.docker`, `credentials`, `.env`, `id_rsa`, `private_key`, etc.
- Validates additional mounts by: expanding `~`, resolving symlinks, checking against allowed roots, checking blocked patterns, validating container paths (no `..`, no absolute)
- `nonMainReadOnly` option forces read-only for non-main groups

### `src/container-runtime.ts` — Runtime Abstraction (80 lines)

Thin abstraction over the container runtime (currently Docker):
- `CONTAINER_RUNTIME_BIN = 'docker'`
- `ensureContainerRuntimeRunning()`: Checks `docker info`
- `cleanupOrphans()`: Stops any `nanoclaw-*` containers from previous runs
- Designed to be swapped (e.g., Apple Container via `/convert-to-apple-container` skill)

### `src/config.ts` — Configuration (65 lines)

Constants loaded from `.env` file and process.env:
- `ASSISTANT_NAME` (default: "Andy"), `ASSISTANT_HAS_OWN_NUMBER`
- `POLL_INTERVAL` (2s), `SCHEDULER_POLL_INTERVAL` (60s)
- `CONTAINER_IMAGE` (nanoclaw-agent:latest), `CONTAINER_TIMEOUT` (30min), `IDLE_TIMEOUT` (30min)
- `MAX_CONCURRENT_CONTAINERS` (5), `CONTAINER_MAX_OUTPUT_SIZE` (10MB)
- `TRIGGER_PATTERN`: regex `^@{ASSISTANT_NAME}\b` (case insensitive)
- Secrets intentionally NOT loaded into process.env to prevent leaking to child processes

---

## 3. Container Architecture (Agent Runner)

### Dockerfile

Based on `node:22-slim` with:
- Chromium + fonts for browser automation
- `agent-browser` and `@anthropic-ai/claude-code` installed globally
- Agent-runner TypeScript compiled at startup (not at build time — source is mounted from host)
- Runs as non-root `node` user (uid 1000)
- Entry point: reads JSON from stdin, runs agent, outputs to stdout

### `container/agent-runner/src/index.ts` — Agent Runner (588 lines)

The code that runs inside each container:

**Input/Output Protocol**:
- Input: Full `ContainerInput` JSON via stdin (includes prompt, session ID, group info, secrets)
- Output: JSON wrapped in `OUTPUT_START_MARKER` / `OUTPUT_END_MARKER` pairs on stdout
- Follow-up messages: JSON files polled from `/workspace/ipc/input/`
- Shutdown signal: `_close` sentinel file in IPC input directory

**Query Loop**:
1. Read initial prompt from stdin
2. Call Claude Agent SDK's `query()` with `MessageStream` (async iterable) to keep the session alive
3. Poll IPC for follow-up messages during the query, piping them into the `MessageStream`
4. On query completion, emit session update marker, then wait for next IPC message or `_close`
5. On new message, start a new query with `resume` and `resumeSessionAt` to continue the session
6. Loop until `_close` sentinel received

**SDK Configuration**:
- `permissionMode: 'bypassPermissions'` with `allowDangerouslySkipPermissions: true`
- Tools: Bash, file ops, web search/fetch, Task/TeamCreate (agent swarms), MCP nanoclaw tools
- MCP server: spawns `ipc-mcp-stdio.js` as a child process with group context via env vars
- Hooks:
  - `PreCompact`: Archives transcript to `conversations/` as markdown before compaction
  - `PreToolUse` (Bash): Injects `unset ANTHROPIC_API_KEY CLAUDE_CODE_OAUTH_TOKEN` before every bash command to prevent credential leakage
- Session resume: Uses `resumeSessionAt` with last assistant UUID for proper multi-turn continuation
- Global CLAUDE.md: Loaded as appended system prompt for non-main groups

### `container/agent-runner/src/ipc-mcp-stdio.ts` — MCP Server (280 lines)

A stdio-based MCP server that provides tools to the agent inside the container:

| Tool | Description | Authorization |
|------|-------------|---------------|
| `send_message` | Send a message to the user/group immediately | All groups (own chat only for non-main) |
| `schedule_task` | Create a scheduled/recurring task | All groups; main can target other groups |
| `list_tasks` | List scheduled tasks | Main sees all; others see own only |
| `pause_task` | Pause a task | Own tasks + main can pause any |
| `resume_task` | Resume a paused task | Own tasks + main can resume any |
| `cancel_task` | Delete a task | Own tasks + main can cancel any |
| `register_group` | Register a new WhatsApp group | Main only |

All operations write JSON files to `/workspace/ipc/` directories, which the host's IPC watcher picks up.

---

## 4. Security Model

### Trust Hierarchy

| Entity | Trust Level |
|--------|-------------|
| Main group (self-chat) | Trusted — full project access, can manage all groups |
| Non-main groups | Untrusted — isolated filesystem, limited IPC |
| Container agents | Sandboxed — OS-level isolation via containers |
| WhatsApp messages | User input — potential prompt injection |

### Defense Layers

1. **Container Isolation** (primary): Each agent runs in an ephemeral Docker container with:
   - Process isolation from host
   - Filesystem limited to explicit volume mounts
   - Non-root user (uid 1000)
   - `--rm` flag for automatic cleanup

2. **Mount Security**: External allowlist at `~/.config/nanoclaw/mount-allowlist.json`:
   - Outside project root → tamper-proof from agents
   - Symlink resolution before validation → prevents traversal
   - Default blocked patterns for sensitive credentials
   - `nonMainReadOnly` enforcement

3. **Session Isolation**: Each group gets its own `.claude/` directory at `data/sessions/{group}/`. Groups cannot see other groups' conversation history.

4. **IPC Authorization**: Identity verified by directory path. Non-main groups cannot:
   - Send messages to other chats
   - Schedule tasks for other groups
   - Register new groups
   - Refresh group metadata

5. **Credential Handling**:
   - Secrets passed via stdin JSON, deleted from disk immediately
   - SDK env isolated from Bash subprocess env (hooks inject `unset` commands)
   - Known limitation: agent can technically discover API keys via Bash in the container environment

### Main vs Non-Main Privileges

| Capability | Main | Non-Main |
|------------|------|----------|
| Project root mount | Read-write | None |
| Group folder | Read-write | Read-write (own only) |
| Global memory | Via project mount | Read-only |
| Additional mounts | Configurable | Read-only (unless allowed) |
| Manage other groups | Yes | No |
| Cross-group messaging | Yes | No |

---

## 5. Skills System

### Philosophy: "Skills Over Features"

NanoClaw uses a unique contribution model: instead of adding features to the codebase, contributors write **skills** — markdown instruction files in `.claude/skills/` that teach Claude Code how to transform the codebase. Users run skills on their fork to get clean, minimal code that does exactly what they need.

### Available Skills

| Skill | Purpose |
|-------|---------|
| `/setup` | First-time installation, WhatsApp auth, container build, launchd |
| `/customize` | Guided customization of behavior, channels, integrations |
| `/debug` | Container troubleshooting, log analysis |
| `/add-telegram` | Add Telegram as a channel |
| `/add-discord` | Add Discord as a channel |
| `/add-gmail` | Gmail integration |
| `/add-voice-transcription` | Whisper-based voice transcription |
| `/add-parallel` | Parallel agent execution |
| `/x-integration` | X/Twitter integration |
| `/convert-to-apple-container` | Switch Docker → Apple Container |
| `/add-telegram-swarm` | Telegram with agent swarm support |

### Skills Engine (`skills-engine/`, 18+ modules)

A sophisticated programmatic skill application system:

**Core Concept**: Skills are self-contained packages applied via standard `git merge-file` three-way merging against a shared base (`.nanoclaw/base/`).

**Three-Level Resolution Model**:
1. **Git** — `git merge-file` for code, deterministic operations for structured data (npm deps, docker-compose, env vars). Handles majority of cases.
2. **Claude Code** — Reads `SKILL.md`, `.intent.md` files to resolve conflicts git can't handle. Caches via `git rerere`.
3. **User** — Asked only for genuine ambiguity requiring human judgment.

**Skill Package Structure**:
```
skills/add-whatsapp/
  SKILL.md              # Context and intent
  manifest.yaml         # Metadata, deps, structured ops, conflicts
  tests/                # Integration tests
  add/                  # New files (copied directly)
  modify/               # Modified files (full file for 3-way merge)
    src/server.ts
    src/server.ts.intent.md  # Structured intent for conflict resolution
```

**Key Modules**:
- `apply.ts`: Full apply flow — pre-flight checks, backup, file ops, merge, structured ops, post-apply, test
- `manifest.ts`: YAML manifest parsing with validation (required fields, path safety)
- `state.ts`: Tracks applied skills, versions, file hashes, custom modifications
- `merge.ts`: Git merge-file wrapper with rerere adapter
- `structured.ts`: Deterministic operations for npm deps, env vars, docker-compose
- `backup.ts` / `lock.ts`: Safety mechanisms (backup/restore on failure, mutex lock)
- `rebase.ts`: Reapply skills after core update
- `update.ts`: Preview and apply core updates
- `customize.ts`: Track user customizations as patches
- `resolution-cache.ts`: Shared resolution cache for common conflict patterns
- `path-remap.ts`: Handle renamed files across skills and updates

**Manifest Fields**:
```yaml
skill: add-whatsapp
version: 1.0.0
core_version: 1.0.0
adds: [src/channels/whatsapp.ts]
modifies: [src/index.ts, src/config.ts]
conflicts: [replace-with-telegram]
depends: []
structured:
  npm_dependencies: { "whatsapp-web.js": "^2.1.0" }
  env_additions: [WHATSAPP_TOKEN]
  docker_compose_services: { ... }
file_ops: [{ type: rename, from: old.ts, to: new.ts }]
test: "npm test"
post_apply: ["npm install"]
```

---

## 6. Data Flow Deep Dive

### Inbound Message Flow

```
1. WhatsApp message arrives → Baileys fires messages.upsert
2. WhatsApp channel extracts text, detects bot messages
3. For all chats: storeChatMetadata() (group discovery)
4. For registered groups only: onMessage() → storeMessage() in SQLite
5. Message loop polls getNewMessages(registeredJids, lastTimestamp)
6. Messages grouped by chatJid
7. Trigger check: main group exempt; others need @Andy prefix
8. If container active for group: pipe via IPC file to /workspace/ipc/input/
9. If no container: enqueue in GroupQueue
10. GroupQueue spawns container when concurrency allows
11. Container receives prompt via stdin JSON
12. Agent processes, streams results via OUTPUT markers
13. Host parses markers, sends response to WhatsApp
14. Per-group cursor advanced; state saved to SQLite
```

### Container Lifecycle

```
1. GroupQueue.runForGroup() → processGroupMessages()
2. buildVolumeMounts() constructs mount list based on group type
3. docker run -i --rm spawned with mounts
4. Secrets written to stdin, then stdin closed (container reads once)
5. Container entrypoint: recompile TypeScript → run agent-runner
6. Agent-runner reads stdin JSON, starts Claude query loop
7. During query: polls /workspace/ipc/input/ for follow-up messages
8. Results streamed back to host via OUTPUT markers on stdout
9. After query: emits session update, waits for next IPC message or _close
10. On _close sentinel or hard timeout: container exits
11. Docker --rm cleans up container automatically
```

### IPC Flow (Container → Host)

```
Container agent calls MCP tool (e.g., send_message)
→ ipc-mcp-stdio.ts writes JSON to /workspace/ipc/messages/
→ Host IPC watcher (1s poll) reads JSON files
→ Authorization check (directory = group identity)
→ Action executed (send message, create task, register group)
→ JSON file deleted
```

---

## 7. Key Design Decisions

### Why Polling Instead of Events?
SQLite is the single source of truth. Polling every 2s is simple, reliable, and avoids race conditions. The cost is negligible for a personal assistant.

### Why File-Based IPC?
Containers can't share memory with the host. Filesystem is the simplest cross-boundary communication available. Atomic writes (temp file + rename) ensure consistency. Directory-based namespacing provides authorization.

### Why No Configuration Files?
The philosophy is "customization = code changes." The codebase is small enough (~35k tokens) that Claude Code can safely modify it. This avoids configuration sprawl and ensures every user's installation does exactly what they need.

### Why XML Message Format?
Messages are formatted as XML for the agent: `<message sender="..." time="...">`. This provides structured context while being natural for Claude to parse.

### Why Session Resume Instead of Fresh Sessions?
Containers are kept alive via IPC to support multi-turn conversations. When a new message arrives for a group with an active container, it's piped in via IPC rather than spawning a new container. The SDK's `resume` + `resumeSessionAt` mechanism continues the conversation.

### Why Idle Timeout?
Containers stay alive for 30 minutes after the last output. This allows for follow-up messages to be piped into the same session without the cold-start cost of spawning a new container. After idle timeout, the container is gracefully stopped.

---

## 8. Technology Stack

| Component | Technology | Version |
|-----------|------------|---------|
| Runtime | Node.js | ≥20 |
| Language | TypeScript | ^5.7.0 |
| WhatsApp | @whiskeysockets/baileys | ^7.0.0-rc.9 |
| Database | better-sqlite3 | ^11.8.1 |
| AI Agent | @anthropic-ai/claude-agent-sdk | 0.2.29+ |
| Cron | cron-parser | ^5.5.0 |
| Logging | pino + pino-pretty | ^9.6.0 |
| Schema | zod | ^4.3.6 |
| Container | Docker or Apple Container | — |
| Browser | Chromium + agent-browser | — |
| Build | tsc / tsx | — |
| Test | vitest | ^4.0.18 |
| Config | YAML (for skills manifests) | yaml ^2.8.2 |

### Dependencies (Minimal by Design)

**Runtime** (9 packages): baileys, better-sqlite3, cron-parser, pino, pino-pretty, qrcode, qrcode-terminal, yaml, zod

**Dev** (6 packages): @types/better-sqlite3, @types/node, @types/qrcode-terminal, @vitest/coverage-v8, prettier, tsx, typescript, vitest

---

## 9. Testing Infrastructure

- **Framework**: Vitest with two configs:
  - `vitest.config.ts`: Main tests (`src/**/*.test.ts`, `skills-engine/**/*.test.ts`)
  - `vitest.skills.config.ts`: Skill-specific tests
- **Test files** identified: formatting, routing, container-runner, container-runtime, db, group-queue, ipc-auth, whatsapp channel, plus full skills-engine test suite
- **Pattern**: Tests use `_initTestDatabase()` for in-memory SQLite to avoid filesystem side effects
- **CI**: GitHub Actions workflows for tests, skill tests, and a skills-only workflow

---

## 10. Deployment

### macOS via launchd

Template plist at `launchd/com.nanoclaw.plist`:
- `RunAtLoad: true`, `KeepAlive: true` (auto-restart)
- Runs `node dist/index.js` from project root
- Logs to `logs/nanoclaw.log` and `logs/nanoclaw.error.log`
- Placeholder template values (`{{NODE_PATH}}`, `{{PROJECT_ROOT}}`, `{{HOME}}`) replaced during `/setup`

### Service Management
```bash
launchctl load ~/Library/LaunchAgents/com.nanoclaw.plist
launchctl unload ~/Library/LaunchAgents/com.nanoclaw.plist
```

---

## 11. Agent Capabilities Inside Containers

Each Claude agent inside a container has access to:

| Tool | Description |
|------|-------------|
| Bash | Shell commands (sandboxed in container) |
| Read/Write/Edit/Glob/Grep | File operations within mounted directories |
| WebSearch/WebFetch | Internet access |
| agent-browser | Full browser automation with Chromium |
| Task/TaskOutput/TaskStop | Background subagent tasks |
| TeamCreate/TeamDelete/SendMessage | Agent Swarms (multi-agent collaboration) |
| TodoWrite | Task tracking |
| mcp\_\_nanoclaw\_\_send\_message | Send messages to user/group |
| mcp\_\_nanoclaw\_\_schedule\_task | Create scheduled tasks |
| mcp\_\_nanoclaw\_\_list\_tasks | View scheduled tasks |
| mcp\_\_nanoclaw\_\_pause/resume/cancel\_task | Manage tasks |
| mcp\_\_nanoclaw\_\_register\_group | Register new WhatsApp groups (main only) |

---

## 12. Notable Implementation Details

### Dual Cursor System
- `lastTimestamp`: Global "seen" cursor — advanced immediately when messages are fetched
- `lastAgentTimestamp[chatJid]`: Per-group "processed" cursor — advanced only after successful processing, rolled back on errors (unless output was already sent)

### Bot Message Detection
Two modes based on `ASSISTANT_HAS_OWN_NUMBER`:
- **Own number**: `fromMe` flag is reliable
- **Shared number**: Messages prefixed with `{ASSISTANT_NAME}:` are bot messages

### Transcript Archival
Before Claude's automatic context compaction, the `PreCompact` hook archives the full transcript as a markdown file in `conversations/`. This preserves conversation history even as sessions compact.

### Secret Sanitization in Bash
A `PreToolUse` hook injects `unset ANTHROPIC_API_KEY CLAUDE_CODE_OAUTH_TOKEN 2>/dev/null;` before every Bash command, preventing agent-executed shell commands from discovering API credentials.

### Container Build Cache Gotcha
Docker's buildkit caches COPY steps aggressively. `--no-cache` alone doesn't invalidate them. The project works around this by mounting agent-runner source at runtime (read-only) and recompiling TypeScript in the container's entrypoint script.

### Non-Trigger Message Accumulation
In non-main groups, messages without the trigger word accumulate in the database but aren't processed. When a trigger message eventually arrives, ALL accumulated messages since the last agent response are pulled as context, giving the agent the full conversation history.

---

## 13. Comparison with OpenClaw

NanoClaw positions itself as a reaction to OpenClaw:

| Aspect | OpenClaw | NanoClaw |
|--------|----------|----------|
| Modules | 52+ | ~15 source files |
| Config files | 8+ management files | 0 (code changes) |
| Dependencies | 45+ | 9 runtime deps |
| Channel providers | 15 abstractions | 1 (WhatsApp; others via skills) |
| Security | Application-level (allowlists, pairing codes) | OS-level (containers) |
| Process model | Shared memory, one process | Per-group isolated containers |
| Customization | Configuration | Fork + code changes via AI |
| Context window | Won't fit | ~35k tokens (fits fully) |

---

## 14. Extensibility Points

1. **Channel abstraction** (`Channel` interface): Add new messaging platforms by implementing `connect()`, `sendMessage()`, `isConnected()`, `ownsJid()`, `disconnect()`, `setTyping?()`
2. **Skills system**: Full programmatic skill application with merge conflict resolution
3. **MCP tools**: Add new agent capabilities via MCP servers (stdio or embedded)
4. **Container mounts**: Additional directories mountable via `containerConfig.additionalMounts`
5. **Container runtime**: Swappable via `container-runtime.ts` (Docker ↔ Apple Container)
6. **Agent hooks**: PreCompact, PreToolUse hooks for custom behavior

---

## 15. Limitations and Known Issues

1. **Credential exposure**: API keys are accessible inside the container environment (workaround: bash hook unsets them, but agent could theoretically discover them through other means)
2. **Network unrestricted**: Containers have full network access — no network isolation
3. **Single user**: Designed for personal use; no multi-tenant support
4. **WhatsApp dependency**: Core depends on unofficial Baileys library (risk of breakage)
5. **macOS/Linux only**: No native Windows support (WSL2 required)
6. **No end-to-end encryption**: Messages stored in plaintext SQLite
7. **Polling overhead**: 2s message poll, 1s IPC poll, 60s scheduler poll (acceptable for personal use)
