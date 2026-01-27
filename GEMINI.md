# Project Overview

**Gordon Finance Engine** is a self-hosted, containerised financial analytics platform designed to provide "Actuarial-grade" insights into personal finances. It connects to Investec Programmable Banking, stores transaction history in a robust time-series database, and uses a local Large Language Model (LLM) to offer a natural language interface for queries and reporting.

## Key Technologies

*   **Core:** .NET 8 (C#)
*   **Database:** TimescaleDB (PostgreSQL extension).
    *   `transactions` table: Time-series hypertable.
    *   `system_config` table: JSONB singleton for storing persistent app settings.
*   **AI/LLM:** External Ollama instance (configurable URL).
*   **Frontend:** Vue.js 3 (Global build, no bundler) + Tailwind CSS (CDN). Served as static files from `wwwroot` by the .NET app.
*   **Orchestration:** Docker Compose.

## Architecture

The system consists of two main containers managed via `docker-compose.yml`:

1.  **`timescaledb`**: Stores financial data and configuration. Not exposed externally (internal network only).
2.  **`gordon-worker`**: The main application logic.
    *   **Ingestion Worker**: Polls Investec API every 60s (`TransactionsBackgroundService`).
    *   **Connectivity Worker**: Checks Investec API status every 30m (`ConnectivityWorker`).
    *   **Weekly Reporter**: Scheduled emailing service (`WeeklyReportWorker`).
    *   **API**: Exposes endpoints for Chat, Settings, and Status (`/api/settings`, `/chat`).
    *   **UI**: Serves the Single Page Application (`index.html`).

### Key Services (`GordonWorker/Services`)

*   **`ActuarialService`**: Implements advanced financial math:
    *   **Monte Carlo Simulation**: Probability of survival (30-day).
    *   **Linear Regression**: Month-end projection.
    *   **Weighted Burn (EMA)**: Recent spending sensitivity.
*   **`OllamaService`**: Handles LLM communication.
    *   *Text-to-SQL*: Generates SQL for raw queries.
    *   *Analyst Persona*: Interprets complex JSON stats into simple advice.
*   **`SettingsService`**: Persists runtime configuration to the `system_config` database table. **Always use this service to retrieve settings, do not hardcode.**
*   **`TransactionSyncService`**: Central logic for fetching and upserting transactions. Handles "Deep Sync" logic.
*   **`FinancialReportService`**: Orchestrates data fetching, math, AI summary, and HTML email generation.

# Building and Running

The project is designed to be run entirely via Docker.

## Prerequisites
*   Docker Desktop
*   Investec API Credentials (set in `.env`)

## Commands

**Start the System:**
```bash
docker-compose up -d --build
```

**Accessing the Application:**
*   **Dashboard & Settings:** `http://localhost:52944`
*   **Chat API:** `POST http://localhost:52944/chat`

# Development Conventions

*   **Language & Locale:** All code, comments, and logs must use **English UK**.
*   **Formatting:** No em dashes (—) are permitted in code comments or strings; use regular dashes (-).
*   **Database:** 
    *   Use "Safe Upsert" (`ON CONFLICT DO NOTHING`) for transaction ingestion.
    *   Store all user-configurable settings in the `system_config` table (JSONB).
*   **Frontend:** Keep the frontend "no-build". Use Vue 3 Global build. Do not introduce npm/webpack.
*   **Configuration:** 
    *   Secrets (API Keys) -> `.env` (Environment Variables).
    *   Runtime Preferences (Schedule, Currency, AI URL) -> Database via `SettingsService`.