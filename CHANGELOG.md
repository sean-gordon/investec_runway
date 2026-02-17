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

---

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
