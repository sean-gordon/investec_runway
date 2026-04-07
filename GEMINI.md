# Gordon Finance Engine

Gordon is a self-hosted dashboard for personal finances. It connects to Investec's Programmable Banking API, stores your history in TimescaleDB, and uses AI (Ollama, Gemini, or Claude) so you can ask questions about your money in plain English.

**Version:** 2.9.0
**Status:** Feature-complete and stable.

---

## Core Tech

*   **Backend:** .NET 8 (C#).
*   **Database:** TimescaleDB (PostgreSQL).
    *   `transactions`: Time-series table for all your spending.
    *   `user_settings`: Encrypted JSON blob for your config.
    *   `chat_history`: Stores your AI conversations.
*   **AI Support:** Ollama, Gemini, and Claude.
    *   Automatic fallback if your primary AI fails.
    *   Custom "Thinking Models" for complex analysis.
*   **Frontend:** Vue.js 3 and Tailwind CSS.
*   **Security:** AES-256 encryption for all sensitive keys (via .NET Data Protection API).

---

## Architecture

Gordon runs as two main Docker containers:

1.  **`timescaledb`**: Your data store.
2.  **`gordon-worker`**: The engine.
    *   **Ingestion**: Polls the bank every 60 seconds.
    *   **Telegram/WhatsApp**: Background workers for chat.
    *   **Reporting**: Sends your weekly and daily updates.
    *   **API**: Serves the dashboard.

### Key Services

- **`ActuarialService`**: Calculates your burn rate and runway.
- **`AiService`**: Manages the AI providers and retries.
- **`SettingsService`**: Handles encrypted persistence with a 5-minute cache.
- **`TelegramChatService`**: Reliable message queue for the bot.
- **`TransactionSyncService`**: Deduplicates transactions and handles background categorisation.

---

## Code Standards

- **Language:** English UK.
- **Async/Await:** Required for all I/O.
- **Cancellation:** Always pass a `CancellationToken`.
- **Error Handling:** Use exceptions, not null returns.
- **DI:** Constructor injection only.
- **Caching:** Use `IMemoryCache`, never static dictionaries.

---

## Security Basics

- **Secrets:** Never commit them. Use environment variables.
- **JWT:** Minimum 32-character secret required for startup.
- **HTTPS:** Enforced in production.
- **Encryption:** Use the Data Protection API for everything sensitive.
- **Rate Limiting:** Global limit of 100 requests per minute.

---

## Monitoring

### Health Checks
- `/health`: System overall.
- `/health/ready`: Critical services only.
- `/health/live`: DB connectivity.

### Performance Targets
- Health check: < 100ms.
- Settings cache hit: < 5ms.
- AI request (Primary): < 5s.
- Telegram webhook: < 50ms.

---

## Database

### Tables
- `users`: Auth and roles.
- `user_settings`: Encrypted config.
- `transactions`: Partitioned by date. Unique index on `(id, transaction_date, user_id)`.
- `chat_history`: Log of AI interactions.

---

## Upgrade Guide (v1.x to v2.0+)

**Breaking Changes:**
1. JWT secret must be 32+ characters.
2. Allowed domains must be set in `appsettings.json`.

**Steps:**
1. Generate a new JWT secret.
2. Rebuild the Docker image.
3. Your database will migrate automatically on startup.

---

## Roadmap

See `suggestions.md` for more, but the big ones are:
1. Better ML-based categorisation.
2. Year-on-year seasonality analysis.
3. Interactive charts in the dashboard.
4. Multi-currency support.

---

**Last Updated:** 2026-04-07
