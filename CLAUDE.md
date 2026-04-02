# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Gordon Finance Engine** (v2.9.0) — a self-hosted, containerized financial analytics platform that connects to the Investec Programmable Banking API for real-time transactions, runs Monte Carlo simulations for runway projections, and provides a natural language AI interface via Telegram bot and REST API.

## Build & Run Commands

```bash
# Production: build and start all services
docker compose up -d --build

# Start without rebuilding
docker compose up -d

# View logs
docker compose logs -f gordon-worker

# Stop
docker compose down

# .NET build (without Docker)
cd GordonWorker
dotnet build ./GordonWorker.csproj -c Release
dotnet publish ./GordonWorker.csproj -c Release -o ./publish
```

## Initial Setup

```bash
cp .env.template .env
# Edit .env: set DB_PASSWORD, JWT_SECRET (must be ≥ 32 characters — validated at startup)
```

## Health Check Endpoints

```
GET http://localhost:52944/health        # Overall health
GET http://localhost:52944/health/ready  # Database + critical services
GET http://localhost:52944/health/live   # Database only
```

Dashboard is served at `http://localhost:52944`.

## Architecture

The application is a single .NET 8 Web API project (`GordonWorker/`) with a clean architecture:

```
Controllers (HTTP API)
    → Services (Business Logic)
        → Repositories (Data Access via Dapper)
            → TimescaleDB (PostgreSQL 16 time-series DB)
```

A Vue 3 frontend (no-build, no npm compile step) is served as static files from `GordonWorker/wwwroot/index.html`. All service registration and middleware wiring is in `GordonWorker/Program.cs`.

### Key Services

- **`ActuarialService`** — Core analytics: burn rate, runway, Monte Carlo simulations, salary detection, variance analysis
- **`AiService`** — Multi-provider LLM orchestration: routes to Ollama (primary) or Gemini (fallback), 5-min settings cache, exponential backoff (2s, 4s), 90s timeout
- **`InvestecClient`** — OAuth2 client for the Investec bank API
- **`TransactionSyncService`** — Deduplicates transactions using content-based fingerprinting
- **`SettingsService`** — Stores encrypted user configuration (AES-256 via .NET Data Protection API) with 5-min `IMemoryCache`
- **`TelegramChatService`** — Background `Channel<T>` queue for reliable Telegram message processing (15s heartbeat progress updates)
- **`FinancialReportService`** — Orchestrates report generation: fetch → calculate → AI summary → HTML email

### Background Workers (`Workers/`)

- `TransactionsBackgroundService` — Polls Investec API every 60 seconds
- `WeeklyReportWorker` — Scheduled financial report delivery
- `DailyBriefingWorker` — Daily summaries
- `RunwayTopUpWorker` — Automated balance top-up logic
- `ConnectivityWorker` — Health monitoring

### Database Schema

```sql
users               -- auth & roles
user_settings       -- encrypted JSON config per user (JSONB)
transactions        -- hypertable partitioned by transaction_date
                    -- unique index on (id, transaction_date, user_id) for deduplication
chat_history        -- AI conversation logs
```

## Key Technical Details

- **Port:** 52944 (Docker maps 8080 → 52944)
- **Authentication:** JWT Bearer; secret must be ≥ 32 chars (startup validation)
- **Rate limiting:** 100 requests/minute per user/IP (`SemaphoreSlim` + middleware)
- **Encryption:** AES-256 for stored settings via .NET Data Protection
- **ORM:** Dapper with snake_case column mapping
- **Charts:** ScottPlot (server-side rendering); requires fonts installed in Dockerfile
- **Frontend:** Vue 3 Global Build (no compile step), Tailwind CSS CDN, Chart.js

## Language & Conventions

- English UK throughout (comments, user-facing strings, documentation)
- All async methods must accept and respect `CancellationToken`
- Log timestamps formatted as `[yyyy-MM-dd HH:mm:ss]`
- Semantic versioning; version is maintained in `GordonWorker/GordonWorker.csproj`

## External Service Dependencies

| Service | Role |
|---|---|
| Investec API | OAuth2 bank transaction source (critical) |
| TimescaleDB | Primary data store (critical) |
| Ollama (local) | Primary LLM provider |
| Google Gemini | Fallback LLM provider |
| Telegram Bot | Webhook-based chat interface |
| Twilio | SMS notifications (optional) |
| SMTP | Email delivery (optional) |

## GitHub Workflows

`.github/workflows/gemini-*.yml` are automated Gemini AI dispatch/triage workflows — not CI/CD test pipelines.
