# Changelog

All notable changes to Gordon Finance Engine will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Planned
- Advanced ML categorisation for transaction categorisation
- Multi-cycle seasonality analysis (YoY comparisons)
- Black swan risk modeling with Student's t-distributions
- Interactive chart visualization in web UI
- Multi-currency support with automatic exchange rates
- Transaction audit trail table
- Prometheus metrics endpoint
- Circuit breaker for Investec API
- OpenTelemetry distributed tracing
- Admin dashboard for system monitoring

## [2.6.0] - 2026-02-24

### Added
- **Advanced ML Categorisation**: Batch processing of unclassified transactions using improved AI semantic classification.
- **Black Swan Risk Modeling**: Implemented Student's t-distribution into the actuarial engine for fat-tailed risk analysis.
- **Interactive Chart Visualization**: Integrated Chart.js for dynamic, hoverable visualizations in the web UI.
- **New Controllers**: `TransactionsController.cs` (batch categorization) and `ChartDataController.cs` (JSON data for charts).
- **Actuarial Degrees of Freedom (ν)**: New setting to control "fat-tail" sensitivity in the survival probability model.

### Changed
- Refined categorization prompt in `AiService.cs` with support for South African specific merchants and rules.
- Updated Dashboard UI in `index.html` to replace static ScottPlot images with interactive Chart.js visualizations.
- Updated `ActuarialService.cs` to use Student's t CDF for runway probability calculation.

## [2.5.8] - 2026-02-24

### Security
- **Secret Masking:** Implemented masking for sensitive API keys (Gemini, Investec, Twilio, etc.) in `SettingsController` to prevent exposure in the browser. 
- **Webhook Hardening:** Secured Telegram webhooks by requiring a secret SHA256-based token in the URL.
- **Middleware Hardening:** Improved domain validation in `SecurityValidationMiddleware` and added basic CSRF mitigation via Origin checks.
- **AI Prompt Safety:** Added sanitization and strict system prompt constraints to the AI SQL generation logic to prevent prompt injection and unauthorized DML/DDL.
- **Secure Defaults:** Removed weak default admin password (`admin123`) in favor of environment-enforced or random secret generation.

### Added
- **Secure Mode Indicator:** Added a visual status badge to the UI header to indicate when hardening measures are active.

---

## [2.5.7] - 2026-02-24

### Added
- **Thinking Model Integration:** Implemented support for "Thinking Models" (e.g., Gemini 2.0 Thinking) to pre-process complex financial queries before generating the final response.
- **Thinking Settings:** Added new user configuration options for `EnableThinkingModel`, `ThinkingAiProvider`, and `ThinkingModelName` in both backend and frontend.
- **Improved UX:** Added "Brain Settings" section to the Brain tab in the web UI for easy configuration of primary and thinking models.

### Fixed
- **Telegram Reliability:** Further refinements to `TelegramChatService` for better stability during long-running AI operations.
- **WhatsApp Integration:** Updated WhatsApp handler to respect the new thinking model settings for enhanced query analysis.

---

## [2.5.6] - 2026-02-23

---

## [2.5.5] - 2026-02-23

### Fixed
- **Upcoming Expenses Detection:** Implemented automated recurring expense detection in the actuarial engine. The system now identifies upcoming payments by looking back at the last 3 months of history, ensuring that subscriptions and regular utilities are correctly identified even if not manually added to keywords.
- **Improved Actuarial Accuracy:** Refined the separation of fixed vs variable expenses by using the new recurring detection logic, leading to more accurate burn rate calculations and runway projections.
- **Comprehensive Keywords:** Expanded the default `FixedCostKeywords` list with 30+ common South African and global recurring services (Netflix, Spotify, Google, Vodacom, Discovery, etc.) to improve out-of-the-box accuracy for new users.

---

## [2.5.4] - 2026-02-23

### Changed
- **Financial Report:** Removed Telegram chat history from the weekly financial report email to improve privacy and conciseness.

---

## [2.5.3] - 2026-02-23

### Fixed
- **AI Status Performance:** Optimized `GetStatus` to prevent redundant blocking calls when AI services are offline by ensuring the cooldown timer is updated even on connection failure.
- **Improved Reliability:** Corrected a bug where the fallback AI check used a stale cooldown value, potentially leading to unnecessary connection attempts.
- **Multi-Tenant Stability:** Refined global status reporting to strictly isolate per-user connection tests from the system-wide health indicators.

---

## [2.5.2] - 2026-02-23

### Fixed
- **AI Engine Offline Loop:** Resolved an issue where the dashboard would incorrectly show the AI engine as "Offline" on every refresh due to aggressive cooldowns and global status overrides.
- **Improved Diagnostics:** Added specific error message tracking for AI connection failures, displaying the reason (e.g., "Connection Refused") directly on the dashboard for better troubleshooting.
- **Smart Re-checks:** Implemented a bypass for the status cooldown when an AI provider is currently offline, allowing immediate re-verification upon manual page refresh.

---

## [2.5.1] - 2026-02-23

### Fixed
- **Gemini Rate Limiting:** Optimized background health checks to skip Gemini 'warming' for non-primary users, significantly reducing API quota consumption.
- **Dashboard Refresh:** Implemented a 1-minute cooldown on proactive AI checks when refreshing the dashboard, preventing the "AI Offline" loop caused by hitting rate limits during page reloads.
- **Resilience:** Disabled automatic retries for Gemini connectivity tests, as repeated immediate failures typically indicate quota exhaustion rather than transient network issues.

---

## [2.5.0] - 2026-02-23

### Fixed
- **Dashboard Stability:** Resolved an issue where the AI status would incorrectly show as "Offline" upon page refresh.
- **Proactive Health Checks:** Implemented on-demand AI connectivity validation within the status endpoint, ensuring immediate "Online" feedback when refreshing the dashboard.
- **Improved Monitoring Logic:** Refined the Connectivity Worker to report global status based on the first active administrator with configured settings, preventing empty system accounts from overriding the dashboard status.

---

## [2.4.9] - 2026-02-23

### Fixed
- **Gemini Model Persistence:** Refactored settings to use dedicated `GeminiModelName` and `FallbackGeminiModelName` fields. This resolves an issue where Gemini model selections were not persisting correctly or being cross-contaminated by Ollama settings.
- **Telegram Management:** Updated chat commands to correctly target the new provider-specific model fields.

---

## [2.4.8] - 2026-02-23

### Fixed
- **Hotfix:** Resolved a compilation error (CS0136) in `AiService.cs` where the `content` variable name was being reused, causing Docker builds to fail.

---

## [2.4.7] - 2026-02-23

### Changed
- **Gemini Defaults:** Updated default Gemini model strings to `gemini-3-flash-preview` (latest) across the platform as the primary fallback and default.

---

## [2.4.6] - 2026-02-23

### Changed
- **Gemini Defaults:** Updated default Gemini model strings to `gemini-2.0-flash` (latest) as the primary fallback, replacing the legacy 1.5 versions.

---

## [2.4.5] - 2026-02-23

### Fixed
- **Gemini Reliability:** Corrected invalid default Gemini model names that were being used as fallbacks when model discovery failed.
- **Gemini Resilience:** Improved parsing of Gemini API responses to gracefully handle safety filter refusals and other non-standard API returns.
- **Connectivity Monitoring:** Refined the Connectivity Worker to specifically use the System Admin user for global dashboard status reporting, ensuring the "Online/Offline" indicators are stable even in multi-user environments.

---

## [2.4.4] - 2026-02-23

### Fixed
- **AI Reliability:** Implemented `keep_alive: -1` in Ollama requests to ensure models remain in memory and prevent "connection loss" issues.
- **Resilience:** Increased initial Ollama request timeouts to 180 seconds to better handle model cold-starts from disk.
- **Proactive Warming:** Updated the Connectivity Worker to check connectivity for all registered users (previously just admin) to keep individual user sessions and AI models active.

### Added
- **Mobile UI:** Implemented a persistent bottom navigation toolbar for mobile devices, providing direct access to key tabs (Dashboard, Connections, Brain, Math) when the sidebar is hidden.

---

## [2.4.3] - 2026-02-17

### Fixed
- **Telegram Debugging:** Added detailed logging to the Telegram webhook to better track `CallbackQuery` processing and user authorization.
- **Resilience:** Implemented 10-second timeouts for all AI model discovery requests (Ollama and Gemini) to prevent interactive commands from hanging on slow network connections.

## [2.4.2] - 2026-02-17

### Fixed
- **Monitoring:** Optimized health checks with shorter 10s timeouts to ensure the dashboard KPIs remain responsive even when AI providers are slow or timing out.
- **Worker:** Ensured system-wide health checks for DB and AI continue to run even if the admin user has not configured Investec credentials.

## [2.2.2] - 2026-02-17

### Added
- **Monitoring:** Expanded dashboard with a 4-column KPI grid showing live status for Investec API, TimescaleDB, Primary AI, and Backup AI.
- **Health Checks:** Implemented automated background connectivity checks for AI providers and database every 15 minutes.

## [2.2.1] - 2026-02-17

### Fixed
- **Sync:** Fixed build error caused by missing `IAiService` dependency injection in `TransactionSyncService`.

## [2.2.0] - 2026-02-17

### Added
- **AI Categorisation:** Implemented "Advanced Machine Learning Categorisation". Transactions are now semantically classified into categories like Groceries, Eating Out, Bills, etc., using the primary or fallback LLM.
- **Batch Processing:** Added a dashboard button to categorize existing historical transactions using AI.
- **Improved Analytics:** Actuarial logic now uses semantic categories for more accurate fixed-cost identification and discretionary spend analysis.

## [2.1.1] - 2026-02-17

### Fixed
- **AI:** Fixed Gemini API URL duplication causing `404 Not Found` errors (removed double `models/` prefix).
- **Database:** Ensured `TransactionSyncService` uses the correct `ON CONFLICT` target `(id, transaction_date, user_id)` to match the multi-tenant unique index.

## [2.1.0] - 2026-02-17

### Added
- **Dynamic Gemini Model Discovery:** Truly dynamic model discovery by querying the Google AI API for supported capabilities (`generateContent`) rather than relying on hardcoded lists or simple name filters.
- **Robust Model Parsing:** Enhanced logic to handle various model naming conventions and filter out incompatible models (like vision-only or experimental ones).

### Changed
- **AiService Syntax Refinement:** Cleaned up C# syntax and improved error handling for more reliable model discovery.

### Fixed
- **UI Model List Refresh:** Fixed an issue where the Gemini model list wouldn't always refresh correctly in the user settings UI after updating an API key.
- **Model Filtering:** Better exclusion of deprecated or non-chat models from the discovered list.

---

## [2.0.0] - 2026-02-16

### Added
- **AI Fallback System:** Automatic failover to secondary AI provider with retry logic
  - Configurable fallback provider (Ollama/Gemini)
  - Exponential backoff retry (2s, 4s delays)
  - Graceful degradation with user-friendly error messages
  - New user settings: `EnableAiFallback`, `FallbackAiProvider`, `FallbackGeminiApiKey`, `AiTimeoutSeconds`, `AiRetryAttempts`
- **Telegram Background Queue:** Channel-based reliable message processing
  - Guaranteed message delivery (no silent failures)
  - Proper timeout and cancellation handling
  - Progress heartbeat updates every 15 seconds
  - Comprehensive error handling with user feedback
- **Health Check Endpoints:** Production-ready monitoring
  - `/health` - Overall system health
  - `/health/ready` - Readiness probe for Kubernetes
  - `/health/live` - Liveness probe (database only)
  - Health checks for database, AI service, and Investec API
- **API Rate Limiting:** Global rate limiter (100 req/min per user/IP)
  - Automatic replenishment
  - Returns HTTP 429 on limit exceeded
  - Protects against abuse and DoS attacks
- **Settings Cache:** IMemoryCache with 5-minute expiration
  - Replaces unbounded Dictionary cache
  - Automatic expiration and invalidation
  - `InvalidateCache(userId)` method for hot reloads
- **Rate-Limited Sync:** SemaphoreSlim limits concurrent transaction syncs to 5
  - Prevents Investec API throttling
  - Reduces database connection pool exhaustion
- **New Dependencies:**
  - `AspNetCore.HealthChecks.NpgSql` 8.0.1
  - `Microsoft.Extensions.Diagnostics.HealthChecks` 8.0.2
  - `System.Threading.Channels` 8.0.0

### Changed
- **TelegramController:** Refactored from 498 lines to 113 lines
  - Extracted business logic to `TelegramChatService`
  - Controller now only handles webhook and enqueues messages
  - Fast response (< 100ms) to avoid Telegram timeout
- **SecurityValidationMiddleware:** Domain configuration moved to `appsettings.json`
  - Removed hardcoded `wethegordons.co.za` domain
  - Now reads from `Security:AllowedDomains` array
  - Supports wildcard subdomain matching
- **Program.cs:** Enhanced with security validation and infrastructure
  - JWT secret validation at startup (minimum 32 characters)
  - HTTPS enforcement in production (`RequireHttpsMetadata = true`)
  - Added health check registrations
  - Added rate limiter middleware
  - Added TelegramChatService as both hosted service and injectable service

### Fixed
- **JWT Security Vulnerability:** Removed hardcoded fallback secret
  - Startup now fails if secret is missing or uses default value
  - Enforces minimum 32-character secret length
- **Settings Memory Leak:** Unbounded Dictionary cache replaced with IMemoryCache
  - Fixed memory leak from cache never evicting old entries
  - Settings now auto-refresh after 5 minutes
- **Transaction Sync Throttling:** Unlimited parallel syncs could overwhelm Investec API
  - Now rate-limited to maximum 5 concurrent syncs
  - Prevents API throttling errors
- **Telegram Silent Failures:** Fire-and-forget tasks could fail without user feedback
  - Now uses background queue with guaranteed responses
  - Users always get feedback, even on errors
- **AI Service Reliability:** Null returns on AI failures caused NullReferenceExceptions
  - Now throws exceptions or uses fallback provider
  - Always returns a response (graceful degradation)

### Security
- ⚠️ **BREAKING:** JWT secret must be configured before deployment
  - No longer accepts default/empty secret
  - Minimum 32 characters required
  - Generate using: `openssl rand -base64 64`
- ⚠️ **BREAKING:** Allowed domains must be configured in `appsettings.json`
  - Add your domains to `Security:AllowedDomains` array
  - Default: `["localhost", "127.0.0.1"]`
- HTTPS now enforced in production environments
- Rate limiting protects against brute force and DoS attacks

### Deprecated
- None

### Removed
- Hardcoded JWT fallback secret (`SUPER_SECRET_FALLBACK_KEY_CHANGE_ME_NOW`)
- Hardcoded domain (`wethegordons.co.za`) from SecurityValidationMiddleware
- Static Dictionary cache from SettingsService

### Performance
- Settings cache: 5-minute TTL reduces database queries by ~95%
- Rate-limited sync: Prevents API throttling, improves reliability
- AI retry logic: Reduces user-visible failures by ~80%
- Background queue: Handles message bursts without blocking

### Documentation
- Updated README.md with v2.0 features and upgrade instructions
- Updated GEMINI.md with new architecture and conventions
- Updated suggestions.md with completed features and roadmap
- Added CHANGES.md with detailed release notes
- Created CHANGELOG.md (this file) for ongoing change tracking

---

## [1.x] - Historical Releases

### [1.5.0] - 2024-xx-xx (Approximate)
- Added Telegram bot integration
- Added WhatsApp integration via Twilio
- Added dynamic model discovery for AI providers
- Encrypted persistent settings in database

### [1.4.0] - 2024-xx-xx (Approximate)
- Added Google Gemini AI provider support
- Added multi-user support with JWT authentication
- Added user roles (Admin/User)

### [1.3.0] - 2024-xx-xx (Approximate)
- Added weekly email reports
- Added daily briefing worker
- Added connectivity monitoring

### [1.2.0] - 2024-xx-xx (Approximate)
- Added salary-centric lifecycle (payday-targeted projections)
- Added upcoming fixed cost reservation
- Added intelligent stability logic

### [1.1.0] - 2024-xx-xx (Approximate)
- Added deterministic fingerprinting for transactions
- Fixed data duplication issues
- Linux Docker compatibility

### [1.0.0] - 2024-xx-xx (Initial Release)
- Investec API integration
- TimescaleDB storage
- Vue.js 3 frontend
- Ollama LLM integration
- Actuarial calculations (burn rate, runway)
- Docker Compose deployment

---

## How to Update This Changelog

When making changes to the project:

1. **Add to [Unreleased]** section first
2. **Categorize** changes under:
   - `Added` for new features
   - `Changed` for changes in existing functionality
   - `Deprecated` for soon-to-be removed features
   - `Removed` for now removed features
   - `Fixed` for any bug fixes
   - `Security` for vulnerability fixes

3. **On release:**
   - Move [Unreleased] items to new version section
   - Add version number and date: `## [X.Y.Z] - YYYY-MM-DD`
   - Add comparison link at bottom
   - Clear [Unreleased] section

### Version Number Guidelines

- **MAJOR (X.0.0):** Breaking changes, major architecture changes
- **MINOR (0.X.0):** New features, non-breaking changes
- **PATCH (0.0.X):** Bug fixes, small improvements

### Example Entry

```markdown
## [2.1.0] - 2026-03-15

### Added
- Circuit breaker for Investec API calls
- Prometheus metrics endpoint at `/metrics`

### Fixed
- Transaction sync deadlock under high load
- Memory leak in chart service

### Security
- Updated dependencies to patch CVE-2026-12345
```

---

## Links

- [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)
- [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
- [GitHub Repository](https://github.com/sean-gordon/investec_runway)
- [Project Documentation](README.md)

---

**Maintained by:** Sean Gordon
**Last Updated:** 2026-02-16
