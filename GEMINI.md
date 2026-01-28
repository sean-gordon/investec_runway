# Project Overview

**Gordon Finance Engine** is a self-hosted, containerised financial analytics platform designed to provide "Actuarial-grade" insights into personal finances. It connects to Investec Programmable Banking, stores transaction history in a robust time-series database, and uses a Large Language Model (LLM) to offer a natural language interface for queries and reporting.

## Key Technologies

*   **Core:** .NET 8 (C#)
*   **Database:** TimescaleDB (PostgreSQL extension).
    *   `transactions` table: Time-series hypertable.
    *   `system_config` table: JSONB singleton for app settings.
*   **AI/LLM:** Multi-provider support (Ollama or Google Gemini).
*   **Frontend:** Vue.js 3 (Global build) + Tailwind CSS. Served as static files from `wwwroot`.
*   **Orchestration:** Docker Compose.

## Architecture

1.  **`timescaledb`**: Stores financial data and configuration.
2.  **`gordon-worker`**: The main application logic.
    *   **Ingestion Worker**: Polls Investec API with deterministic deduplication (`TransactionsBackgroundService`).
    *   **Weekly Reporter**: Orchestrates the salary-cycle reporting pipeline.
    *   **API/UI**: Serves the dashboard and REST endpoints.

### Key Services (`GordonWorker/Services`)

*   **`ActuarialService`**: Implements the Payday-Targeted Lifecycle:
    *   **Salary Cycle Detection**: Sign-agnostic detection of paycheck dates.
    *   **Upcoming Payments**: Reserves balance for missing fixed historical overhead.
    *   **Period-To-Date (PTD)**: Like-for-like comparison between cycles.
    *   **Weighted Burn (EMA)**: Recent spending sensitivity.
*   **`AiService`**: Handles LLM communication (Ollama/Gemini).
    *   *Text-to-SQL*: Generates SQL for raw queries.
    *   *Analyst Persona*: Interprets complex JSON stats into simple advice.
*   **`SettingsService`**: Persists runtime configuration to the database.
*   **`TransactionSyncService`**: Logic for fetching and fingerprinting transactions.
*   **`FinancialReportService`**: Orchestrates data fetching, math, AI summary, and HTML email generation.

## Development Conventions

*   **Language & Locale:** All code, comments, and logs must use **English UK**.
*   **Logging**: Console logs must include timestamps in `[yyyy-MM-dd HH:mm:ss]` format.
*   **Database:** Use "Deterministic Fingerprinting" for IDs to prevent sync duplication.
*   **Frontend:** Keep the frontend "no-build". Use Vue 3 Global build.

## Gemini Added Memories
- Always push changes to GitHub after completing a request.