# Gordon Finance Engine: Guide for Claude

This file helps Claude Code (claude.ai/code) understand how to work with this project.

## The Project

**Gordon Finance Engine** (v2.9.0) is a self-hosted financial dashboard. It pulls data from Investec's Programmable Banking API, runs simulations to predict your "runway" (how long your money will last), and lets you query your spending using AI (Ollama, Gemini, or Claude).

---

## Commands

```bash
# Start everything (builds if needed)
docker compose up -d --build

# View logs
docker compose logs -f gordon-worker

# Stop everything
docker compose down

# Manual .NET build (if not using Docker)
cd GordonWorker
dotnet build ./GordonWorker.csproj -c Release
```

## Setup

1. Copy `.env.template` to `.env`.
2. Set a strong `DB_PASSWORD`.
3. Set a `JWT_SECRET` (must be at least 32 characters or Gordon won't start).

The dashboard runs at `http://localhost:52944`.

---

## Health Checks

- `GET /health` — System status
- `GET /health/ready` — Database and critical services
- `GET /health/live` — Database connection only

---

## How it's Built

It's a .NET 8 Web API (`GordonWorker/`) with a simple architecture:
**Controllers** → **Services** → **Repositories (Dapper)** → **TimescaleDB**.

The frontend is a single-file Vue 3 app (`index.html`) served as static content. No npm build step required.

### Core Services
- **`ActuarialService`**: The math. Burn rates, runway, simulations, and salary detection.
- **`AiService`**: Orchestrates the AI providers (Ollama, Gemini, Claude). Handles fallbacks and retries.
- **`ClaudeCliService`**: Wrapper for the `claude` CLI tool to use consumer plans.
- **`InvestecClient`**: Communicates with the bank's API.
- **`TransactionSyncService`**: Deduplicates transactions and handles background categorisation.
- **`SettingsService`**: Manages encrypted user config with a 5-minute memory cache.
- **`TelegramChatService`**: A reliable message queue for the Telegram bot.

### Background Workers
- `TransactionsBackgroundService`: Polls the bank every 60 seconds.
- `WeeklyReportWorker` / `DailyBriefingWorker`: Scheduled updates.
- `RunwayTopUpWorker`: Logic for moving money between accounts.
- `ConnectivityWorker`: Monitors service health.

---

## Technical Details

- **Port:** 52944 (Internal 8080)
- **Auth:** JWT Bearer tokens.
- **Rate Limiting:** 100 requests/minute.
- **Encryption:** AES-256 for stored keys (via .NET Data Protection).
- **Database:** TimescaleDB (PostgreSQL 16) with hypertables.
- **UI:** Vue 3, Tailwind CSS, and Chart.js.

---

## Conventions
- **Language:** English UK.
- **Async:** Always use `CancellationToken`.
- **Logging:** Structured logs with `[yyyy-MM-dd HH:mm:ss]` timestamps.
- **Versioning:** Maintained in `GordonWorker.csproj`.

---

## External Services
- **Investec API**: Primary transaction source.
- **TimescaleDB**: Main storage.
- **Ollama / Gemini / Claude**: AI brains.
- **Telegram / Twilio**: Messaging and alerts.
- **SMTP**: For the weekly financial report.
