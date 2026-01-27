# Project Overview

**Gordon Finance Engine** is a self-hosted, containerised financial analytics platform designed to provide "Actuarial-grade" insights into personal finances. It connects to Investec Programmable Banking, stores transaction history in a robust time-series database, and uses a local Large Language Model (LLM) to offer a natural language interface for queries and reporting.

## Key Technologies

*   **Core:** .NET 8 (C#)
*   **Database:** TimescaleDB (PostgreSQL extension for time-series)
*   **AI/LLM:** Ollama (hosting `deepseek-coder` or configurable model)
*   **Frontend:** Vue.js 3 (CDN-based, zero build) + Tailwind CSS
*   **Orchestration:** Docker Compose

## Architecture

The system consists of three main containers managed via `docker-compose.yml`:

1.  **`timescaledb`**: Stores transaction data in a hypertable partitioned by time.
2.  **`ollama`**: Runs the local LLM inference engine.
3.  **`gordon-worker`**: The main application logic, which includes:
    *   **Ingestion Worker**: Polls Investec API every 60s for new transactions (`TransactionsBackgroundService`).
    *   **Weekly Reporter**: Checks schedule (default: Mon 9am) to generate and email a "Junior High" style summary (`WeeklyReportWorker`).
    *   **API**: Exposes endpoints for the Chat interface (`/chat`) and Settings management (`/api/settings`).
    *   **UI**: Serves a single-page application (`index.html`) for configuration and dashboarding.

### Key Services (`GordonWorker/Services`)

*   **`ActuarialService`**: Implements advanced financial math, including Exponential Moving Average (EMA) for weighted burn rates, Volatility (StdDev) analysis, and Value at Risk (VaR 95%).
*   **`OllamaService`**: Handles prompts to the LLM. It supports two main modes:
    *   *Text-to-SQL*: Generates SQL queries from natural language for raw data retrieval.
    *   *Analyst Persona*: Interprets complex JSON data from the Actuarial Service to provide human-readable advice.
*   **`SettingsService`**: Persists runtime configuration (e.g., report schedule, AI persona) to `app_data/settings.json`, allowing changes without container restarts.

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

**Initialize AI Model (First Run Only):**
The system expects a specific model to be available in the Ollama container.
```bash
docker exec -it investecrunwayapp-ollama-1 ollama pull deepseek-coder
```

**Accessing the Application:**
*   **Dashboard & Settings:** `http://localhost:52944`
*   **Chat API:** `POST http://localhost:52944/chat`

# Development Conventions

*   **Language & Locale:** All code, comments, and logs must use **English UK**.
*   **Formatting:** No em dashes (—) are permitted in code comments or strings; use regular dashes (-).
*   **Database:** Use "Safe Upsert" (`ON CONFLICT DO NOTHING`) for transaction ingestion to ensure idempotency.
*   **Frontend:** The frontend in `wwwroot` is a "no-build" implementation. Do not introduce npm build steps (Webpack/Vite) unless strictly necessary. It uses Vue 3 Global build and Tailwind CDN.
*   **Configuration:** 
    *   Secrets (API Keys, Passwords) -> `.env` (Environment Variables).
    *   Runtime Preferences (Schedule, Persona) -> `settings.json` (via `SettingsService`).
