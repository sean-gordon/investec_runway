# Project Overview

**Gordon Finance Engine** is a self-hosted, containerised financial analytics platform designed to provide "Actuarial-grade" insights into personal finances. It connects to Investec Programmable Banking, stores transaction history in a robust time-series database, and uses a Large Language Model (LLM) to offer a natural language interface for queries and reporting.

## Key Technologies

*   **Core:** .NET 8 (C#)
*   **Database:** TimescaleDB (PostgreSQL extension).
    *   `transactions` table: Time-series hypertable with composite primary keys.
    *   `system_config` table: JSONB singleton for app settings.
*   **AI/LLM:** Multi-provider support (Ollama or Google Gemini) with dynamic model discovery.
*   **Frontend:** Vue.js 3 (Global build) + Tailwind CSS. Served as static files from `wwwroot`.
*   **Security:** .NET Data Protection API for encrypting sensitive keys (AES-256) at rest in the DB.
*   **Orchestration:** Docker Compose (Windows & Linux compatible).

## Architecture

1.  **`timescaledb`**: Stores financial data and encrypted configuration.
2.  **`gordon-worker`**: The main application logic.
    *   **Ingestion Worker**: Polls Investec API with deterministic deduplication (`TransactionsBackgroundService`).
    *   **Weekly Reporter**: Orchestrates the salary-cycle reporting pipeline.
    *   **API/UI**: Serves the dashboard and REST endpoints.

### Key Services (`GordonWorker/Services`)

*   **`ActuarialService`**: Implements the Payday-Targeted Lifecycle.
*   **`AiService`**: Handles LLM communication with dynamic provider switching.
*   **`SettingsService`**: Manages encrypted database persistence for all runtime configuration.
*   **`TransactionSyncService`**: Logic for fetching and fingerprinting transactions.
*   **`FinancialReportService`**: Orchestrates data fetching, math, AI summary, and HTML email generation.

## Development Conventions

*   **Language & Locale:** All code, comments, and logs must use **English UK**.
*   **Logging:** Console logs include timestamps in `[yyyy-MM-dd HH:mm:ss]` format.
*   **Database:** Use "Deterministic Fingerprinting" for IDs to prevent sync duplication.
*   **Frontend:** Keep the frontend "no-build". Use Vue 3 Global build.

## Gemini Added Memories
- Always push changes to GitHub after completing a request.
