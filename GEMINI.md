# Project Overview

**Gordon Finance Engine** is a self-hosted, containerised financial analytics platform designed to provide "Actuarial-grade" insights into personal finances. It connects to Investec Programmable Banking, stores transaction history in a robust time-series database, and uses a Large Language Model (LLM) with automatic fallback to offer a natural language interface for queries and reporting.

**Current Version:** 2.2.3
**Status:** Production-Ready, Enterprise-Grade

---

## Key Technologies

*   **Core:** .NET 8 (C#) with background service hosting
*   **Database:** TimescaleDB (PostgreSQL extension)
    *   `transactions` table: Time-series hypertable with composite primary keys
    *   `user_settings` table: JSONB for encrypted user configuration
    *   `chat_history` table: Conversation history for context-aware AI responses
*   **AI/LLM:** Multi-provider support (Ollama or Google Gemini) with:
    *   Automatic fallback on primary failure
    *   Exponential backoff retry logic
    *   Configurable timeouts and retry attempts
    *   Dynamic model discovery
*   **Frontend:** Vue.js 3 (Global build) + Tailwind CSS. Served as static files from `wwwroot`
*   **Security:**
    *   .NET Data Protection API for encrypting sensitive keys (AES-256) at rest in the DB
    *   JWT authentication with startup validation
    *   Configurable domain-based access control
    *   Global rate limiting (100 req/min per user/IP)
*   **Messaging:**
    *   Telegram Bot with Channel-based background queue
    *   Reliable message processing with guaranteed responses
*   **Monitoring:**
    *   Health check endpoints for database, AI, and external APIs
    *   Comprehensive structured logging
*   **Orchestration:** Docker Compose (Windows & Linux compatible)

---

## Architecture

### Container Services

1.  **`timescaledb`**: Stores financial data and encrypted configuration
2.  **`gordon-worker`**: The main application logic
    *   **Ingestion Worker**: Polls Investec API with deterministic deduplication (`TransactionsBackgroundService`)
    *   **Telegram Chat Worker**: Background queue processor for Telegram messages (`TelegramChatService`)
    *   **Weekly Reporter**: Orchestrates the salary-cycle reporting pipeline (`WeeklyReportWorker`)
    *   **Daily Briefing**: Sends daily financial summaries (`DailyBriefingWorker`)
    *   **Connectivity Monitor**: Monitors external service health (`ConnectivityWorker`)
    *   **API/UI**: Serves the dashboard and REST endpoints

### Key Services (`GordonWorker/Services`)

#### Core Services

*   **`ActuarialService`**: Implements the Payday-Targeted Lifecycle
    *   Calculates burn rate, runway, and solvency projections
    *   Salary detection with fallback logic
    *   Fixed cost identification and reservation
    *   Variance analysis and trend detection
    *   Monte Carlo runway simulations

*   **`AiService`**: Handles LLM communication with enterprise reliability
    *   **NEW v2.0:** Automatic fallback to secondary provider
    *   **NEW v2.0:** Exponential backoff retry logic (2s, 4s)
    *   **NEW v2.0:** Configurable timeouts and retry attempts
    *   Dynamic provider switching (Ollama/Gemini)
    *   Multi-model support with discovery
    *   Graceful degradation on total failure
    *   Chart request analysis
    *   Affordability check analysis
    *   Transaction explanation matching

*   **`SettingsService`**: Manages encrypted database persistence
    *   **NEW v2.0:** IMemoryCache with 5-minute expiration
    *   **NEW v2.0:** Cache invalidation on updates
    *   Encryption/decryption of sensitive fields
    *   Per-user configuration management
    *   Fallback AI provider settings

*   **`TelegramChatService`**: Background queue processor (NEW v2.0)
    *   Channel-based message queue (unbounded)
    *   Guaranteed message delivery
    *   Proper timeout and cancellation handling
    *   Progress heartbeat updates (15-second intervals)
    *   Comprehensive error handling
    *   Chart generation support
    *   Affordability analysis
    *   Transaction explanation capture

*   **`TransactionSyncService`**: Logic for fetching and fingerprinting transactions
    *   Deterministic deduplication using content-based IDs
    *   Smart sync (buffer vs full history)
    *   Alert generation for significant transactions
    *   Subscription detection

*   **`FinancialReportService`**: Orchestrates data fetching, math, AI summary, and HTML email generation

*   **`InvestecClient`**: HTTP client for Investec API with OAuth2 authentication

*   **`ChartService`**: ScottPlot-based chart generation for runway visualization

*   **`TelegramService`**: Low-level Telegram Bot API wrapper

*   **`EmailService`**: SMTP email delivery service

#### Infrastructure Services (NEW v2.0)

*   **`AiHealthCheck`**: Monitors AI service connectivity
*   **`InvestecHealthCheck`**: Monitors Investec API availability
*   **Database Health Check**: Built-in TimescaleDB connectivity check

### Workers (`GordonWorker/Workers`)

*   **`TransactionsBackgroundService`**:
    *   Polls all users every 60 seconds
    *   **NEW v2.0:** Rate-limited to 5 concurrent syncs
    *   Prevents API throttling
    *   Creates new scope per user for isolation

*   **`TelegramChatService`**: (NEW v2.0, also in Services)
    *   Background service that processes Telegram queue
    *   Single reader, multiple writers
    *   Fire-and-forget processing with comprehensive error handling

*   **`WeeklyReportWorker`**:
    *   Checks every 5 minutes
    *   Sends reports based on user's configured day/hour
    *   Prevents duplicate sends via `last_weekly_report_sent` tracking

*   **`DailyBriefingWorker`**: Sends daily summaries

*   **`ConnectivityWorker`**: Monitors external service health

### Controllers (`GordonWorker/Controllers`)

*   **`TelegramController`**:
    *   **v2.0 REFACTOR:** Reduced from 498 to 113 lines
    *   Webhook endpoint for Telegram updates
    *   Authorization via chat ID matching
    *   Enqueues messages to `TelegramChatService`
    *   Fast response (< 100ms) to avoid Telegram timeout

*   **`ChatController`**: REST API for financial queries (JWT protected)

*   **`SettingsController`**: Configuration management (JWT protected)

*   **`SimulationController`**: What-if scenario analysis

*   **`AuthController`**: JWT token generation

*   **`UsersController`**: User management

### Middleware

*   **`SecurityValidationMiddleware`**:
    *   **NEW v2.0:** Configurable allowed domains (no hardcoded values)
    *   Domain-based access control
    *   Exempts webhook and health check endpoints
    *   Subdomain wildcard support

---

## Development Conventions

### General

*   **Language & Locale:** All code, comments, and logs must use **English UK**
*   **Logging:** Console logs include timestamps in `[yyyy-MM-dd HH:mm:ss]` format
*   **Database:** Use "Deterministic Fingerprinting" for IDs to prevent sync duplication
*   **Frontend:** Keep the frontend "no-build". Use Vue 3 Global build

### Code Quality (NEW v2.0)

*   **Error Handling:** Use exceptions for errors, not null returns
*   **Async/Await:** Always use `async/await` for I/O operations
*   **Cancellation Tokens:** Pass `CancellationToken` to all async methods
*   **Resource Management:** Use `using` statements for IDisposable resources
*   **Dependency Injection:** Use constructor injection, not service locator pattern
*   **Background Services:** Use `BackgroundService` base class, not manual threads
*   **Caching:** Use `IMemoryCache` with expiration, not static dictionaries
*   **Rate Limiting:** Use `SemaphoreSlim` for concurrency control

### Security Best Practices (NEW v2.0)

*   **Secrets:** Never commit secrets or API keys to source control
*   **JWT:** Minimum 32-character secret, validated at startup
*   **HTTPS:** Required in production (`RequireHttpsMetadata = true`)
*   **Encryption:** Use .NET Data Protection API for sensitive data
*   **Rate Limiting:** Always implement rate limiting on public endpoints
*   **Input Validation:** Validate all user input before processing

### AI Service Patterns (NEW v2.0)

*   **Fallback Required:** Always configure a fallback AI provider
*   **Retry Logic:** Use exponential backoff (2s, 4s, etc.)
*   **Timeouts:** Set reasonable timeouts (90s default for LLM)
*   **Graceful Degradation:** Return user-friendly messages on total failure
*   **Logging:** Log every attempt and failure for debugging
*   **Provider Config:** Encapsulate provider settings in a config object

### Telegram Reliability (NEW v2.0)

*   **Queue-Based:** Use Channel<T> for message processing
*   **Never Fire-and-Forget:** Always use background service queue
*   **Progress Updates:** Send heartbeat messages every 15 seconds
*   **Error Messages:** Always send error response, never silent failure
*   **Timeout Handling:** Use CancellationToken for long operations

---

## Configuration Management

### appsettings.json

**Required Security Configuration:**
```json
{
  "Jwt": {
    "Secret": "MINIMUM_32_CHARS_REQUIRED",
    "Issuer": "GordonFinanceEngine",
    "Audience": "GordonUsers"
  },
  "Security": {
    "AllowedDomains": [
      "localhost",
      "127.0.0.1",
      "yourdomain.com"
    ]
  }
}
```

### User Settings (Database - Encrypted)

**New AI Fallback Settings (v2.0):**
- `EnableAiFallback`: bool (default: true)
- `FallbackAiProvider`: string ("Ollama" or "Gemini")
- `FallbackOllamaBaseUrl`: string
- `FallbackOllamaModelName`: string
- `FallbackGeminiApiKey`: string (encrypted)
- `AiTimeoutSeconds`: int (default: 90)
- `AiRetryAttempts`: int (default: 2)

**Existing Settings:**
- Primary AI configuration
- Investec credentials (encrypted)
- Email settings (SMTP password encrypted)
- Telegram settings (bot token encrypted)
- Actuarial parameters
- Report scheduling

---

## Testing & Monitoring

### Health Check Endpoints (NEW v2.0)

| Endpoint | Description | Checks |
|----------|-------------|--------|
| `/health` | Overall health | All components |
| `/health/ready` | Readiness probe | Database + critical services |
| `/health/live` | Liveness probe | Database only |

### Logging Insights (NEW v2.0)

**AI Service:**
- "AI request attempt X/Y using primary provider"
- "AI request succeeded on attempt X (primary/fallback)"
- "Primary AI provider failed after X attempts. Switching to fallback"
- "All AI providers failed for user X. Returning fallback message"

**Telegram:**
- "Telegram request enqueued for user X"
- "Processing Telegram message for user X"
- "Telegram processing cancelled for user X"

**Settings Cache:**
- "Settings cache invalidated for user X"

**Transaction Sync:**
- Rate limiting prevents excessive logging

### Performance Metrics

**Response Times (Target):**
- Health check: < 100ms
- Settings cache hit: < 5ms
- Settings cache miss: < 100ms
- AI request (cached): < 500ms
- AI request (primary): < 5s
- AI request (with fallback): < 15s
- Telegram webhook: < 50ms (queue only)

---

## Database Schema

### Tables

**`users`:**
- `id` (SERIAL PRIMARY KEY)
- `username` (TEXT UNIQUE)
- `password_hash` (TEXT)
- `role` (TEXT) - "Admin" or "User"
- `is_system` (BOOLEAN)
- `last_weekly_report_sent` (TIMESTAMPTZ)
- `created_at` (TIMESTAMPTZ)

**`user_settings`:**
- `user_id` (INT PRIMARY KEY, FK to users)
- `config` (JSONB) - Encrypted settings blob

**`transactions`:**
- `id` (UUID) - Deterministic fingerprint
- `user_id` (INT, FK to users)
- `account_id` (TEXT)
- `transaction_date` (TIMESTAMPTZ) - Hypertable partition key
- `description` (TEXT)
- `amount` (NUMERIC) - Positive = expense, Negative = income
- `balance` (NUMERIC)
- `category` (TEXT)
- `is_ai_processed` (BOOLEAN)
- `notes` (TEXT) - User explanations
- **Unique Index:** `(id, transaction_date, user_id)`

**`chat_history`:**
- `id` (SERIAL PRIMARY KEY)
- `user_id` (INT, FK to users)
- `message_text` (TEXT)
- `is_user` (BOOLEAN)
- `timestamp` (TIMESTAMPTZ)

---

## Deployment

### Docker Compose

**Services:**
- `timescaledb`: Database
- `gordon-worker`: Application

**Volumes:**
- `timescaledb_data`: Persistent database storage
- `gordon_keys`: Data protection keys

**Networks:**
- `finance-net`: Internal network

### Environment Variables

**.env:**
- `DB_PASSWORD`: PostgreSQL password
- `TZ`: Timezone (e.g., "Africa/Johannesburg")

**Runtime (via Docker):**
- `ConnectionStrings__DefaultConnection`: Database connection string
- `Jwt__Secret`: JWT signing key (from appsettings.json)

---

## Upgrade Notes

### v1.x to v2.0

**Breaking Changes:**
1. JWT secret validation at startup (MUST set 32+ char secret)
2. Allowed domains now in configuration (update appsettings.json)
3. TelegramController interface changed (no breaking API changes)

**Database Migrations:**
- No schema changes required
- Automatic migration on startup

**Configuration Required:**
1. Generate new JWT secret: `openssl rand -base64 64`
2. Add `Security:AllowedDomains` to appsettings.json
3. Rebuild Docker image
4. (Optional) Configure AI fallback in UI

---

## Gemini Added Memories

### Development Workflow
- Always push changes to GitHub after completing a request
- Update CHANGELOG.md with all changes, bug fixes, and features
- Follow semantic versioning (MAJOR.MINOR.PATCH)
- Tag releases in git

### Code Review Standards (v2.0)
- Security fixes are P0 priority
- AI reliability is critical (always have fallback)
- Telegram must never lose messages
- Settings should be cached with expiration
- Rate limiting is mandatory
- Health checks are required for production

### Architecture Decisions
- Prefer background services over fire-and-forget tasks
- Use Channel<T> for reliable queuing
- Always use IMemoryCache over static dictionaries
- Implement health checks for all external dependencies
- Rate limit both API and background operations

---

## Future Enhancements

See `suggestions.md` for roadmap. Key priorities:
1. Advanced ML categorisation
2. Multi-cycle seasonality analysis
3. Black swan risk modelling
4. Interactive chart visualisation
5. Multi-currency support
6. Transaction audit trail (see CHANGELOG.md)

---

**Last Updated:** 2026-02-17 (v2.1.0 Release)
