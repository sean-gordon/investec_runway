# Security Audit Report

**Project:** Gordon Finance Engine (investec_runway)
**Date:** 2026-05-15
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** `GordonWorker/` (ASP.NET Core 8 API + Vue SPA in `wwwroot/`), `db/init.sql`, `docker-compose.yml`, `.env.template`, `appsettings.json`, `Dockerfile`, `entrypoint.sh`, `.github/workflows/`
**Technology Stack:** C# / .NET 8 (ASP.NET Core MVC), PostgreSQL + TimescaleDB (Dapper / Npgsql), JWT bearer auth, BCrypt password hashing, Vue 3 SPA, Telegram / Twilio / Investec integrations, ScottPlot, Claude CLI sub-process.

---

## Executive Summary

Gordon Finance Engine is an ASP.NET Core 8 API backed by PostgreSQL + TimescaleDB, wired to Investec Programmable Banking, an AI categorisation pipeline (Gemini / OpenAI / Anthropic / Ollama), and multi-channel notifications (Telegram bot, Twilio WhatsApp, SMTP email). The 2026-05-08 audit recorded two Medium-severity findings: the missing `Webhooks__InvestecSecret` plumbing in `docker-compose.yml`, and the unversioned Tailwind play CDN script tag with no SRI. The first is now closed — `docker-compose.yml:34` propagates `Webhooks__InvestecSecret=${WEBHOOK_INVESTEC_SECRET:-}` to the `gordon-worker` container, preserving the fail-closed default. The second is partially mitigated — the script URL is now version-pinned (`https://cdn.tailwindcss.com/3.4.17`) but remains a CDN-served runtime compiler with no Subresource Integrity hash, so a CDN compromise still yields full DOM control.

This audit re-ran the full OWASP 2025 sweep across all controllers, middleware, repositories, services, workers, configuration, and the Vue SPA. The Tailwind CDN issue is the sole remaining residual finding from the prior audit, plus two new low-severity issues identified during the cross-check: the `RefreshTokenRepository` is registered in `Program.cs` but has no backing `refresh_tokens` schema in `db/init.sql` or `DatabaseInitializer`, so any invocation will fail at runtime; and the global CSP `script-src` still lists `https://cdn.tailwindcss.com` as a wildcard origin alongside `'unsafe-inline'` and `'unsafe-eval'`, both of which become unnecessary once Tailwind is vendored locally and would let any future inline-script gadget execute under the dashboard's authenticated origin. All injection, IDOR, authentication, cryptographic, and access-control checks held — `[Authorize(Roles = "Admin")]` on `LogsController` / `UsersController`, per-row `user_id = @userId` scoping across `TransactionsController` / `ChartDataController` / `SettingsController` / `SimulationController`, BCrypt with the dummy-hash timing flatten, the SQL validator on AI-generated chart queries, and `FixedTimeEquals` on both webhook secrets are all in place.

**Overall Risk Score:** 6 (Low Risk)

| Severity | Count |
|----------|-------|
| Critical | 0     |
| High     | 0     |
| Medium   | 1     |
| Low      | 1     |
| Info     | 1     |
| **Total**| **3** |

---

## Findings

### A01:2025 — Broken Access Control

No issues identified. `LogsController` is `[Authorize(Roles = "Admin")]` (`LogsController.cs:11`); `UsersController` is admin-gated (`UsersController.cs:11`); per-row queries on `TransactionsController`, `ChartDataController`, `SettingsController`, and `SimulationController` filter on `user_id = @userId` from `ClaimTypes.NameIdentifier`. The Telegram and WhatsApp webhooks both verify per-user secrets (SHA-256 of bot token / Twilio signature) in constant time before dispatch. `SecurityValidationMiddleware` enforces exact-match or authorized-subdomain host validation and CSRF Origin checks; the explicit `*` wildcard is rejected.

---

### A02:2025 — Security Misconfiguration

#### Low: CSP `script-src` allows `'unsafe-inline'`, `'unsafe-eval'`, and the Tailwind play-CDN origin

- **File:** `GordonWorker/Program.cs`
- **Line(s):** `Program.cs:194-202`
- **CWE:** CWE-693: Protection Mechanism Failure
- **Description:** The baseline CSP keeps `'unsafe-inline'` and `'unsafe-eval'` on `script-src` (required because the Tailwind play CDN ships a runtime JIT compiler that executes user-defined inline `tailwind.config = {…}` scripts and evaluates utility-class expressions at runtime). Combined with `https://cdn.tailwindcss.com` as a permitted origin, this means any future inline-injection bug elsewhere in the SPA — a stray `v-html`, a Markdown renderer that doesn't escape, an unescaped JSON-in-script element — would execute under the same origin as the authenticated dashboard and could exfiltrate the JWT held in `localStorage`. The CSP gives little defence-in-depth because the bypasses are pre-enabled. The fix is structural: vendor Tailwind locally so the play-CDN script tag is removed; then drop `'unsafe-inline'` and `'unsafe-eval'` from `script-src` (Vue 3 production builds and Chart.js do not need either).
- **Evidence:**
  ```csharp
  // Program.cs:194-202
  headers["Content-Security-Policy"] =
      "default-src 'self'; " +
      "script-src 'self' 'unsafe-inline' 'unsafe-eval' blob: https://unpkg.com https://cdn.jsdelivr.net https://cdn.tailwindcss.com https://static.cloudflareinsights.com; " +
      "style-src 'self' 'unsafe-inline' blob: https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
      "img-src 'self' data:; " +
      "connect-src 'self' https://cloudflareinsights.com https://cdn.jsdelivr.net; " +
      "font-src 'self' data: https://cdn.jsdelivr.net https://fonts.gstatic.com; " +
      "frame-ancestors 'none'; " +
      "base-uri 'self'";
  ```
- **Recommendation:** Vendor Tailwind locally (see A03 below) and then tighten the CSP:
  ```csharp
  headers["Content-Security-Policy"] =
      "default-src 'self'; " +
      "script-src 'self' https://unpkg.com https://cdn.jsdelivr.net https://static.cloudflareinsights.com; " +
      "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
      "img-src 'self' data:; " +
      "connect-src 'self' https://cloudflareinsights.com https://cdn.jsdelivr.net; " +
      "font-src 'self' data: https://cdn.jsdelivr.net https://fonts.gstatic.com; " +
      "frame-ancestors 'none'; " +
      "base-uri 'self'";
  ```
  `'unsafe-inline'` on `style-src` is retained because Vue scoped styles and the existing `<style>` block both need it; dropping that further requires nonce-injection on every `<style>` tag.

---

### A03:2025 — Software Supply Chain Failures

#### Medium: Tailwind play-CDN script still has no Subresource Integrity hash

- **File:** `GordonWorker/wwwroot/index.html`, `GordonWorker/wwwroot/pricing.html`, `GordonWorker/wwwroot/side-hustle.html`
- **Line(s):** `index.html:24`, `pricing.html:9`, `side-hustle.html:9`
- **CWE:** CWE-829: Inclusion of Functionality from Untrusted Control Sphere / CWE-353: Missing Support for Integrity Check
- **Description:** Vue (`unpkg.com/vue@3.4.21`) and Chart.js (`cdn.jsdelivr.net/npm/chart.js@4.4.2`) are version-pinned and SRI-verified with `integrity="sha384-…"`. Tailwind has been version-pinned to `3.4.17` since the previous audit, but the play-CDN endpoint does not publish stable SRI hashes (it ships a JIT compiler whose response can vary between requests). If `cdn.tailwindcss.com` is compromised, re-routed by a transparent proxy, or has its TLS chain coerced by a hostile network, the attacker controls a `<script>` tag with full DOM access on every page — including the authenticated dashboard, where they can exfiltrate the JWT in `localStorage`, intercept form submissions, or rewrite the financial UI to mislead the user.
- **Evidence:**
  ```html
  <!-- index.html:24 (also pricing.html:9 / side-hustle.html:9) -->
  <script src="https://cdn.tailwindcss.com/3.4.17" crossorigin="anonymous" referrerpolicy="no-referrer"></script>
  ```
- **Recommendation:** Vendor the Tailwind runtime locally under `wwwroot/vendor/` and serve it same-origin, removing the runtime CDN dependency entirely:
  ```html
  <!-- Self-hosted: same-origin, served by app.UseStaticFiles(), no CDN dependency. -->
  <script src="/vendor/tailwindcss-3.4.17.js"
          integrity="sha384-<computed>"
          crossorigin="anonymous"></script>
  ```
  Once vendored, drop `https://cdn.tailwindcss.com` from the CSP `script-src` (see A02). Because Tailwind's play CDN is a runtime compiler rather than a precomputed bundle, the same Tailwind JS file vendored under `wwwroot/vendor/` does have a deterministic SHA-384 (computed at download time) and so can carry an `integrity=` attribute.

---

### A04:2025 — Cryptographic Failures

No issues identified. `DatabaseInitializer.cs:65-67` uses `RandomNumberGenerator.Fill(new byte[18])` for the dev bootstrap admin password (144 bits, base64-encoded). Passwords are hashed with `BCrypt.Net.BCrypt.HashPassword` (default cost 11). The JWT secret length and placeholder check are enforced in `Program.cs:32-44`. Sensitive `AppSettings` fields are AES-256 encrypted at rest via .NET Data Protection (`SettingsService.cs:297-312`).

---

### A05:2025 — Injection

No issues identified. The AI-generated SQL path in `TransactionRepository.GetChartDataAsync` (`TransactionRepository.cs:269-309`) validates queries with: `SELECT`-only check, comment stripping, semicolon rejection, full word-boundary forbidden-keyword scan (incl. `EXEC`, `INTO`, `MERGE`, `COPY`, `CALL`, `VACUUM`, `LISTEN`, `NOTIFY`, `PG_SLEEP`), mandatory `user_id` column reference, mandatory `@userId` parameter reference, and a `SET TRANSACTION READ ONLY` boundary. Other database access is parameterised via Dapper. `ClaudeCliService.cs:31-46` uses `ProcessStartInfo.ArgumentList` rather than concatenated arguments to prevent shell-metacharacter injection from user-supplied prompts. `ChartDataController.cs:42-50` interpolates `incomeFilter` derived from a Boolean (`settings.ExcludeIncomeFromAnalytics`) into a string-built SQL — the value is not user-controlled and is one of two literal fragments, so this is safe. `UsersController.cs:93` similarly interpolates a fixed `passSql` fragment derived from a Boolean, also safe.

---

### A06:2025 — Insecure Design

No issues identified. `[EnableRateLimiting("auth")]` on `AuthController` (10/min/IP) plus the global 100/min limiter remain in place; password complexity is enforced (12 chars, upper / lower / digit / symbol) in both `AuthController.Register` and `UsersController.Create/Update`; `Register` returns the same generic acknowledgement on duplicate and new accounts to defeat user enumeration; JWT lifetime is 12 hours.

---

### A07:2025 — Authentication Failures

No issues identified. `AuthController.Login` runs `BCrypt.Verify` against the precomputed `DummyPasswordHash` when the user lookup misses (`AuthController.cs:75, 88-99`), keeping response time roughly constant whether or not the username exists. `WebhookController.ValidateSecret` and `TelegramController.TokensEqual` both use `CryptographicOperations.FixedTimeEquals`. WhatsApp uses `Twilio.Security.RequestValidator`.

---

### A08:2025 — Software or Data Integrity Failures

No new issues beyond the Tailwind CDN SRI gap captured under A03 (which is the same defect viewed through a supply-chain integrity lens). No unsafe deserialization paths were found over attacker-controlled input. JSON deserialization in `TelegramController.Webhook` (`TelegramController.cs:52`) and `WhatsAppController.Webhook` (`WhatsAppController.cs:46`) is bounded by Telegram's `Update` and Twilio's form schema respectively, and is gated by the upstream signature/secret check.

---

### A09:2025 — Security Logging and Alerting Failures

No issues identified. The `LogsController` access-control gap from the prior audit is closed. Auth events log success/failure with the source IP via `_logger.LogInformation/LogWarning`. Heartbeat catches forward the exception object to the structured logger. Shared status fields surfaced over `/api/Settings/status` are stable tags rather than raw exception messages. The dev-bootstrap admin password is written to `Console.WriteLine` (stdout) only — it is not routed through `ILogger` so it never lands in `LogSinkService`'s on-disk `/app/logs/` rotation that the `/api/Logs` endpoint can serve.

---

### A10:2025 — Mishandling of Exceptional Conditions

#### Info: `RefreshTokenRepository` is wired into DI but has no backing schema

- **File:** `GordonWorker/Program.cs`, `GordonWorker/Repositories/RefreshTokenRepository.cs`, `GordonWorker/Services/DatabaseInitializer.cs`, `db/init.sql`
- **Line(s):** `Program.cs:143`, `RefreshTokenRepository.cs:15-19`
- **CWE:** CWE-1059: Insufficient Technical Documentation / CWE-440: Expected Behavior Violation
- **Description:** `IRefreshTokenRepository` is registered in `Program.cs:143` and the implementation reads/writes a `refresh_tokens` table, but neither `db/init.sql` nor `DatabaseInitializer.InitializeAsync` creates that table. There are also no callers — no controller, worker, or service injects `IRefreshTokenRepository`. This is dead infrastructure: if a future change wires the repository into the auth flow, every call will throw `42P01 relation "refresh_tokens" does not exist` at runtime, and the surrounding endpoint will return 500. This isn't an exploitable vulnerability today, but it's a latent fail-open trap (a refresh-token-based "remember me" feature that silently returns 500 might be reduced to "the user has to re-log in occasionally" rather than treated as the broken security guarantee it actually is).
- **Evidence:**
  ```csharp
  // Program.cs:143 — registered but never injected anywhere
  builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
  ```
  ```csharp
  // RefreshTokenRepository.cs:15-19 — references a table that does not exist
  return await db.QuerySingleAsync<long>(@"
      INSERT INTO refresh_tokens (user_id, token_hash, expires_at, user_agent, ip)
      VALUES (@UserId, @TokenHash, @ExpiresAt, @UserAgent, @Ip)
      RETURNING id;",
      new { ... });
  ```
- **Recommendation:** Either delete the repository, model, and DI registration if refresh tokens aren't on the near-term roadmap, or add the schema to `db/init.sql` and `DatabaseInitializer` so the type is usable when wired:
  ```sql
  -- db/init.sql
  CREATE TABLE IF NOT EXISTS refresh_tokens (
      id           BIGSERIAL PRIMARY KEY,
      user_id      INT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
      token_hash   TEXT NOT NULL UNIQUE,
      issued_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
      expires_at   TIMESTAMPTZ NOT NULL,
      revoked_at   TIMESTAMPTZ,
      replaced_by  BIGINT REFERENCES refresh_tokens(id),
      user_agent   TEXT,
      ip           TEXT
  );
  CREATE INDEX IF NOT EXISTS idx_refresh_tokens_user ON refresh_tokens(user_id);
  ```
  Choosing one direction prevents the latent runtime failure if a future contributor assumes the repository is operational.

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | 0 | 0 | 0 | 0 | 0 | 0  |
| A02 — Security Misconfiguration    | 0 | 0 | 0 | 1 | 0 | 2  |
| A03 — Supply Chain Failures        | 0 | 0 | 1 | 0 | 0 | 4  |
| A04 — Cryptographic Failures       | 0 | 0 | 0 | 0 | 0 | 0  |
| A05 — Injection                    | 0 | 0 | 0 | 0 | 0 | 0  |
| A06 — Insecure Design              | 0 | 0 | 0 | 0 | 0 | 0  |
| A07 — Authentication Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A08 — Data Integrity Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A09 — Logging & Alerting Failures  | 0 | 0 | 0 | 0 | 0 | 0  |
| A10 — Exceptional Conditions       | 0 | 0 | 0 | 0 | 1 | 0  |
| **Total**                           | 0 | 0 | 1 | 1 | 1 | **6** |

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

---

## Remediation Priority

1. **Vendor Tailwind locally and tighten CSP (A03 Medium / A02 Low — one fix, both wins).** Self-host `tailwindcss-3.4.17.js` under `wwwroot/vendor/`, compute and apply a SHA-384 SRI hash, point all three HTML files at the same-origin path, and then drop `https://cdn.tailwindcss.com`, `'unsafe-inline'`, and `'unsafe-eval'` from the CSP `script-src`. The same vendoring step closes both the SRI gap and the CSP-weakness finding, eliminating the runtime CDN dependency entirely.
2. **Resolve the `RefreshTokenRepository` latent failure (A10 Info).** Delete the repository, model, and DI registration if refresh tokens aren't on the near-term roadmap; or add the `refresh_tokens` schema to `db/init.sql` and `DatabaseInitializer`. Either is fine — picking one direction prevents the silent 500-on-first-call trap when the next contributor wires it up.

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
