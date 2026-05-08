# Security Audit Report

**Project:** Gordon Finance Engine (investec_runway)
**Date:** 2026-05-08
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** `GordonWorker/` (ASP.NET Core 8 API + Vue SPA in `wwwroot/`), `db/init.sql`, `docker-compose.yml`, `.env.template`, `appsettings.json`, `Dockerfile`, `entrypoint.sh`, `.github/workflows/`
**Technology Stack:** C# / .NET 8 (ASP.NET Core MVC), PostgreSQL + TimescaleDB (Dapper / Npgsql), JWT bearer auth, BCrypt password hashing, Vue 3 SPA, Telegram / Twilio / Investec integrations, ScottPlot, Claude CLI sub-process.

---

## Executive Summary

Gordon Finance Engine is an ASP.NET Core 8 API backed by PostgreSQL + TimescaleDB, wired to Investec Programmable Banking, an AI categorisation pipeline (Gemini / OpenAI / Anthropic / Ollama), and multi-channel notifications (Telegram bot, Twilio WhatsApp, SMTP email). The 2026-05-01 audit's six findings were largely closed in commit `cf6d1a6` — `LogsController` is now `[Authorize(Roles = "Admin")]`, the Telegram chart path routes through the hardened `TransactionRepository.GetChartDataAsync` validator, the login endpoint runs `BCrypt.Verify` against a precomputed dummy hash on missing users to flatten the timing oracle, the dev bootstrap admin password uses `RandomNumberGenerator.Fill` (144-bit), and `WEBHOOK_INVESTEC_SECRET` is documented in `.env.template`.

This audit re-ran the full OWASP 2025 sweep and found **2 residual findings**, both Medium. The first is a configuration plumbing gap: `WEBHOOK_INVESTEC_SECRET` is documented in `.env.template` but is **not** propagated into the `gordon-worker` container by `docker-compose.yml`. Operators following the documented setup will silently deploy with the realtime card-push webhook permanently failing closed (every `POST /api/webhook/investec` returns 401), masking the fact that the entire realtime pipeline is non-functional. The second finding is the Tailwind play-CDN `<script>` tag in `index.html`, `pricing.html`, and `side-hustle.html` — it is unversioned (`https://cdn.tailwindcss.com`) and has no Subresource Integrity hash, so a CDN compromise (or a transparent proxy on a hostile network) yields full DOM control on the authenticated dashboard. The previous audit recorded this and it remains unfixed in the current tree.

**Overall Risk Score:** 8 (Low Risk)

| Severity | Count |
|----------|-------|
| Critical | 0     |
| High     | 0     |
| Medium   | 2     |
| Low      | 0     |
| Info     | 0     |
| **Total**| **2** |

---

## Findings

### A01:2025 — Broken Access Control

No new issues identified. `LogsController` is now `[Authorize(Roles = "Admin")]` (`LogsController.cs:11`); `UsersController` is admin-gated; per-row queries on `TransactionsController`, `ChartDataController`, `SettingsController`, and `SimulationController` filter on `user_id = @userId` from `ClaimTypes.NameIdentifier`. The Telegram and WhatsApp webhooks both verify the per-user secret (SHA-256 of bot token / Twilio signature) in constant time before dispatch.

---

### A02:2025 — Security Misconfiguration

#### Medium: `WEBHOOK_INVESTEC_SECRET` is documented in `.env.template` but never reaches the container

- **File:** `docker-compose.yml`, `.env.template`, `GordonWorker/Controllers/WebhookController.cs`
- **Line(s):** `docker-compose.yml:23-33`, `.env.template:29-33`, `WebhookController.cs:96-100`
- **CWE:** CWE-1188: Initialization of a Resource with an Insecure Default
- **Description:** The 2026-05-01 audit asked operators to set `WEBHOOK_INVESTEC_SECRET` so the Investec realtime card-push webhook is authenticated. `.env.template` was duly updated with documentation, but `docker-compose.yml` never wires the variable through to the `gordon-worker` service environment. As a result, even an operator who carefully sets `WEBHOOK_INVESTEC_SECRET=…` in their `.env` will see `WebhookController.ValidateSecret()` read `null` from `_configuration["Webhooks:InvestecSecret"]` and `Environment.GetEnvironmentVariable("WEBHOOK_INVESTEC_SECRET")`, fail the `IsNullOrWhiteSpace(expected)` short-circuit, and reject every webhook with HTTP 401. The realtime pipeline is silently inert. The fail-closed posture is correct, but the configuration plumbing makes the documented setup fail in normal use, increasing the chance the operator will eventually disable validation or run with a known-weak secret to debug.
- **Evidence:**
  ```yaml
  # docker-compose.yml:23-33 — WEBHOOK_INVESTEC_SECRET is missing from this list.
  environment:
    - ConnectionStrings__DefaultConnection=Host=timescaledb;...;Password=${DB_PASSWORD}
    - INVESTEC_CLIENT_ID=${INVESTEC_CLIENT_ID}
    - INVESTEC_SECRET=${INVESTEC_SECRET}
    - INVESTEC_API_KEY=${INVESTEC_API_KEY}
    - JWT_SECRET=${JWT_SECRET:-}
    - JWT_ISSUER=${JWT_ISSUER:-GordonFinanceEngine}
    - JWT_AUDIENCE=${JWT_AUDIENCE:-GordonUsers}
    - ADMIN_PASSWORD=${ADMIN_PASSWORD:-}
    - TZ=${TZ}
    - Security__AllowedDomains__0=${ALLOWED_DOMAIN:-localhost}
  ```
  ```csharp
  // WebhookController.cs:96-100 — fails closed when the env var is missing.
  var expected = _configuration["Webhooks:InvestecSecret"]
      ?? Environment.GetEnvironmentVariable("WEBHOOK_INVESTEC_SECRET");

  if (string.IsNullOrWhiteSpace(expected)) return false;
  ```
- **Recommendation:** Add the variable to the container environment using the .NET configuration provider's double-underscore naming so it binds to the existing `Webhooks:InvestecSecret` key:
  ```yaml
  # docker-compose.yml — gordon-worker.environment
  - Webhooks__InvestecSecret=${WEBHOOK_INVESTEC_SECRET:-}
  ```
  The `:-` default keeps the existing fail-closed behaviour when the operator hasn't set the secret yet (rather than failing the container start).

---

### A03:2025 — Software Supply Chain Failures

#### Medium: Tailwind play-CDN script is unversioned and has no Subresource Integrity hash

- **File:** `GordonWorker/wwwroot/index.html`, `GordonWorker/wwwroot/pricing.html`, `GordonWorker/wwwroot/side-hustle.html`
- **Line(s):** `index.html:19`, `pricing.html:8`, `side-hustle.html:8`
- **CWE:** CWE-829: Inclusion of Functionality from Untrusted Control Sphere / CWE-353: Missing Support for Integrity Check
- **Description:** Vue (`unpkg.com/vue@3.4.21`) and Chart.js (`cdn.jsdelivr.net/npm/chart.js@4.4.2`) are version-pinned and SRI-verified with `integrity="sha384-…"`. Tailwind, however, is loaded from `https://cdn.tailwindcss.com` with no version pin (it always serves "latest") and no integrity attribute. If `cdn.tailwindcss.com` is compromised, re-routed by a transparent proxy, or has its TLS chain coerced by a hostile network, the attacker controls a `<script>` tag with full DOM access on every page — including the authenticated dashboard, where they can exfiltrate the JWT in `localStorage`, intercept form submissions, or rewrite the financial UI to mislead the user. The previous audit recorded this and it remains unfixed across all three HTML files.
- **Evidence:**
  ```html
  <!-- index.html:19 (also pricing.html:8 / side-hustle.html:8) -->
  <script src="https://cdn.tailwindcss.com"></script>
  ```
- **Recommendation:** The `cdn.tailwindcss.com` runtime intentionally does not publish stable hashes (it ships its compiler, not a fixed bundle), so the cleanest fix is to vendor the Tailwind runtime locally and serve it same-origin. As a partial defence-in-depth step the URL should at minimum be pinned to a specific version (`https://cdn.tailwindcss.com/3.4.17`) which is byte-deterministic, and the CSP `script-src` should be tightened to that exact path. The fully-vendored option is the strongest fix and removes the runtime CDN dependency entirely:
  ```html
  <!-- Self-hosted: same-origin, served by app.UseStaticFiles(), no CDN dependency. -->
  <script src="/vendor/tailwindcss-3.4.17.js"></script>
  ```
  Followed by tightening the CSP `script-src` to drop `https://cdn.tailwindcss.com`.

---

### A04:2025 — Cryptographic Failures

No new issues identified. `DatabaseInitializer.cs:65-67` now uses `RandomNumberGenerator.Fill(new byte[18])` for the dev bootstrap admin password (144 bits, base64-encoded). Passwords are hashed with `BCrypt.Net.BCrypt.HashPassword` (default cost 11). The JWT secret length and placeholder check are enforced in `Program.cs:32-44`.

---

### A05:2025 — Injection

No new issues identified. The Telegram chart-request handler now routes through `TransactionRepository.GetChartDataAsync` (`TelegramChatService.cs:431`), which validates AI-generated SQL with: `SELECT`-only check, comment stripping (`StripSqlComments`), semicolon rejection, full word-boundary forbidden-keyword scan (incl. `EXEC`, `INTO`, `MERGE`, `COPY`, `CALL`, `VACUUM`, `LISTEN`, `NOTIFY`, `PG_SLEEP`), mandatory `user_id` and `@userId` references, and a `SET TRANSACTION READ ONLY` boundary. Other database access is parameterised via Dapper. `ClaudeCliService.cs:31-46` uses `ProcessStartInfo.ArgumentList` rather than concatenated arguments to prevent shell injection.

---

### A06:2025 — Insecure Design

No new issues identified. `[EnableRateLimiting("auth")]` on `AuthController` (10/min/IP) plus the global 100/min limiter remain in place; password complexity is enforced (12 chars, upper / lower / digit / symbol); `Register` returns the same generic acknowledgement on duplicate and new accounts; JWT lifetime is 12 hours.

---

### A07:2025 — Authentication Failures

No new issues identified. `AuthController.Login` now runs `BCrypt.Verify` against a precomputed `DummyPasswordHash` when the user lookup misses (`AuthController.cs:75, 88-99`), keeping response time roughly constant whether or not the username exists. `WebhookController.ValidateSecret` and `TelegramController.TokensEqual` both use `CryptographicOperations.FixedTimeEquals`. WhatsApp uses `Twilio.Security.RequestValidator`.

---

### A08:2025 — Software or Data Integrity Failures

No new issues identified beyond the Tailwind CDN SRI gap captured under A03 (which is the same defect viewed through a supply-chain integrity lens). No unsafe deserialization paths were found over attacker-controlled input.

---

### A09:2025 — Security Logging and Alerting Failures

No new issues identified. The `LogsController` access-control gap from the prior audit is closed. Auth events log success/failure with the source IP via `_logger.LogInformation/LogWarning`. Heartbeat catches forward the exception object to the structured logger. Shared status fields surfaced over `/api/Settings/status` are stable tags rather than raw exception messages.

---

### A10:2025 — Mishandling of Exceptional Conditions

No new issues identified. Error handlers throughout the controllers return generic 500 messages while logging the full exception with `_logger.LogError(ex, …)`. Database initializer rethrows on critical failure to halt startup. Background-task heartbeats in `TelegramChatService.StartHeartbeatAsync` swallow `TaskCanceledException` correctly and log other exceptions with `LogWarning(ex, …)`.

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | 0 | 0 | 0 | 0 | 0 | 0  |
| A02 — Security Misconfiguration    | 0 | 0 | 1 | 0 | 0 | 4  |
| A03 — Supply Chain Failures        | 0 | 0 | 1 | 0 | 0 | 4  |
| A04 — Cryptographic Failures       | 0 | 0 | 0 | 0 | 0 | 0  |
| A05 — Injection                    | 0 | 0 | 0 | 0 | 0 | 0  |
| A06 — Insecure Design              | 0 | 0 | 0 | 0 | 0 | 0  |
| A07 — Authentication Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A08 — Data Integrity Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A09 — Logging & Alerting Failures  | 0 | 0 | 0 | 0 | 0 | 0  |
| A10 — Exceptional Conditions       | 0 | 0 | 0 | 0 | 0 | 0  |
| **Total**                           | 0 | 0 | 2 | 0 | 0 | **8** |

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

---

## Remediation Priority

1. **Vendor Tailwind locally and tighten CSP (A03, Medium).** Self-host the Tailwind runtime under `wwwroot/vendor/`, point all three HTML files at the same-origin path, and drop `https://cdn.tailwindcss.com` from the CSP `script-src`. This eliminates the SRI gap entirely rather than mitigating it.
2. **Wire `WEBHOOK_INVESTEC_SECRET` into `docker-compose.yml` (A02, Medium).** Add `Webhooks__InvestecSecret=${WEBHOOK_INVESTEC_SECRET:-}` to the `gordon-worker` service environment block so the documented `.env` variable actually reaches the container. The fail-closed default is preserved.

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
