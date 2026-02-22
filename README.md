# ClawPilot

A personal AI assistant that runs on Telegram, powered by [Semantic Kernel](https://github.com/microsoft/semantic-kernel) and [OpenRouter](https://openrouter.ai/).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A **Telegram Bot Token** — create one via [@BotFather](https://t.me/BotFather)
- An **OpenRouter API Key** — get one at [openrouter.ai/keys](https://openrouter.ai/keys)

## Quick Start

### 1. Clone the repo

```bash
git clone https://github.com/AEonAX/ClawPilot.git
cd ClawPilot
```

### 2. Configure environment variables

Copy the example env file and fill in your keys:

```bash
cp .env.example .env
```

Edit `.env` and set the two **required** values:

```dotenv
ClawPilot__TelegramBotToken=your-telegram-bot-token
ClawPilot__OpenRouterApiKey=your-openrouter-api-key
```

#### Optional settings

| Variable | Default | Description |
|---|---|---|
| `ClawPilot__BotUsername` | `@YourBotUsername` | Your bot's Telegram username |
| `ClawPilot__Model` | `anthropic/claude-sonnet-4-20250514` | LLM model (any OpenRouter model ID) |
| `ClawPilot__EmbeddingModel` | `openai/text-embedding-3-small` | Embedding model for memory/RAG |
| `ClawPilot__SystemPrompt` | *(built-in)* | Custom system prompt |
| `ClawPilot__DatabasePath` | `clawpilot.db` | SQLite database file path |
| `ClawPilot__MaxResponseTokens` | `4096` | Max tokens per response |
| `ClawPilot__MaxResponseLength` | `4096` | Max character length per response |
| `ClawPilot__SessionTimeoutMinutes` | `60` | Conversation session timeout |
| `ClawPilot__AllowedChatIds__0` | *(none)* | Restrict bot to specific Telegram chat IDs |

### 3. Run

```bash
cd src/ClawPilot
dotnet run
```

The bot will automatically create the SQLite database and apply migrations on first start.

> **Tip:** In development the database is named `clawpilot-dev.db` and log level is `Debug`. Set `DOTNET_ENVIRONMENT=Development` to use dev settings.

### 4. Talk to your bot

Open Telegram, find your bot, and send it a message.

## Running Tests

```bash
dotnet test
```

## Project Structure

```
src/ClawPilot/
  AI/              — Agent orchestrator, memory service, SK plugins
  Channels/        — Telegram channel abstraction
  Configuration/   — Strongly-typed options
  Database/        — EF Core DbContext & entities
  Services/        — Hosted services (message processor, scheduler, health check)
  Skills/          — Skill loader & manifest
tests/
  ClawPilot.Tests/ — Unit & integration tests
deploy/            — systemd & launchd service files
scripts/           — Publish helper
```

## Deployment

### Publish

```bash
./scripts/publish.sh
```

This produces a release build in the `publish/` directory.

### Linux (systemd)

```bash
sudo cp deploy/clawpilot.service /etc/systemd/system/
sudo cp -r publish /opt/clawpilot/publish
sudo cp .env /opt/clawpilot/.env
sudo systemctl enable --now clawpilot
```

### macOS (launchd)

```bash
sudo mkdir -p /opt/clawpilot /var/log/clawpilot
sudo cp -r publish /opt/clawpilot/publish
sudo cp .env /opt/clawpilot/.env
sudo cp deploy/com.clawpilot.plist /Library/LaunchDaemons/
sudo launchctl load /Library/LaunchDaemons/com.clawpilot.plist
```

## License

[MIT](LICENSE) — Copyright (c) 2026 AEonAX
