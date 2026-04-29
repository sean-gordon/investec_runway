# Security Audit Report

**Project:** Gordon Finance Engine (investec_runway)
**Date:** 2026-04-23
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** `GordonWorker/` (ASP.NET Core 8 API + Vue SPA in `wwwroot/`), `db/init.sql`, `docker-compose.yml`, `.env.template`, `appsettings.json`
**Technology Stack:** C# / .NET 8 (ASP.NET Core MVC), PostgreSQL + TimescaleDB (Dapper/Npgsql), JWT Bearer auth, BCrypt password hashing, Vue 3 SPA, Telegram/Twilio/Investec integrations.

---

## Executive Summary

The Gordon Finance Engine is an ASP.NET Core 8 API that links Investec Programmable Banking data, AI categorisation, and multi-channel notifications (Telegram, Twilio WhatsApp, email). It has a BCrypt-based auth layer, per-user encrypted settings via ASP.NET Data Protection, and basic rate-limiting.

The audit surfaced **11 findings** across OWASP 2025 categories. The most urgent issues are a wildcard domain allow-list baked into `docker-compose.yml` that neuters the `SecurityValidationMiddleware`, a WhatsApp webhook that validates the Twilio signature but **does not reject** invalid signatures, and a public `/api/auth/register` endpoint with no password-strength policy or rate limit. Several controllers also echo raw exception messages to clients, and the whole SPA loads three CDN scripts (Vue, Tailwind, Chart.js) without Subresource Integrity.

These issues are tractable and are fixed by this audit's companion patch: tighten the Security middleware, harden the CORS / security-header story, require strong passwords + stricter login throttling, stop leaking exception messages, pin CDN assets with SRI, and reject (not just log) invalid Twilio signatures.

**Overall Risk Score:** 63 (Critical Risk)

| Severity | Count |
|----------|-------|
| Critical | 2     |
| High     | 5     |
| Medium   | 3     |
| Low      | 1     |
| Info     | 0     |
| **Total**| **11** |

---

## Findings

### A01:2025 — Broken Access Control

#### Critical: Wildcard host allow-list disables SecurityValidationMiddleware
- **File:** `docker-compose.yml`
- **Line(s):** 28
- **CWE:** CWE-284: Improper Access Control
- **Description:** The middleware in `Middleware/SecurityValidationMiddleware.cs` explicitly short-circuits to "allowed" when any entry in `Security:AllowedDomains` is `"*"`. `docker-compose.yml` ships with exactly that value (`Security__AllowedDomains__0=*`), meaning every origin/host is treated as trusted — the middleware becomes a no-op and the CSRF-style `Origin` check also permits everything. The README directs operators to deploy with this compose file.
- **Evidence:**
  ```yaml
  # docker-compose.yml
  environment:
    - Security__AllowedDomains__0=*
  ```
  ```csharp
  // SecurityValidationMiddleware.cs:54-57
  bool isAllowedDomain = _allowedDomains.Any(domain =>
      domain == "*" ||
      effectiveHost.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
      effectiveHost.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase));
  ```
- **Recommendation:** Remove the `*` wildcard entirely; require operators to set concrete hostnames (use an `ALLOWED_DOMAINS` env var). Fail closed if none is configured in production. Drop the short-circuit so the code path is deterministic.
  ```yaml
  environment:
    - Security__AllowedDomains__0=${ALLOWED_DOMAINS:-localhost}
  ```

#### High: WhatsApp webhook accepts requests with invalid Twilio signatures
- **File:** `GordonWorker/Controllers/WhatsAppController.cs`
- **Line(s):** 63-76
- **CWE:** CWE-345: Insufficient Verification of Data Authenticity
- **Description:** The webhook validates the Twilio signature but on failure it only logs a warning and then proceeds to hand the payload to the AI and persist bot replies. This lets anyone who can reach the endpoint send messages that are processed as if they came from Twilio/WhatsApp, causing spoofed commands and message-credit costs.
- **Evidence:**
  ```csharp
  var validator = new Twilio.Security.RequestValidator(userSettings.TwilioAuthToken);
  if (!validator.Validate(requestUrl, parameters, signature))
  {
      _logger.LogWarning("Twilio signature validation failed for user {UserId}", matchedUserId);
  }
  ```
- **Recommendation:** Return `401` when validation fails. Also require a configured `TwilioAuthToken` before honouring a request (fail-closed).
  ```csharp
  if (!validator.Validate(requestUrl, parameters, signature))
  {
      _logger.LogWarning("Twilio signature validation failed for user {UserId}", matchedUserId);
      return Unauthorized();
  }
  ```

---

### A02:2025 — Security Misconfiguration

#### High: Missing security headers (HSTS, CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy)
- **File:** `GordonWorker/Program.cs`
- **Line(s):** 143-191
- **CWE:** CWE-16 / CWE-693: Protection Mechanism Failure
- **Description:** No `UseHsts`, no `UseHttpsRedirection`, and no response-header hardening middleware is registered. The SPA is served from the same origin as the API and handles JWTs in `localStorage`, so a missing `Content-Security-Policy` plus `X-Frame-Options`/`X-Content-Type-Options` dramatically expands the blast radius of any injection or mis-hosted asset.
- **Evidence:** `Program.cs` never references `UseHsts`, `UseHttpsRedirection`, `app.Use(...headers)`, `Content-Security-Policy`, etc.
- **Recommendation:** Add a small middleware that sets the standard headers and enable HSTS outside Development.
  ```csharp
  if (!app.Environment.IsDevelopment()) app.UseHsts();
  app.Use(async (ctx, next) =>
  {
      ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
      ctx.Response.Headers["X-Frame-Options"] = "DENY";
      ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
      ctx.Response.Headers["Content-Security-Policy"] =
          "default-src 'self'; script-src 'self' https://unpkg.com https://cdn.jsdelivr.net https://cdn.tailwindcss.com; " +
          "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; img-src 'self' data:; " +
          "connect-src 'self'; frame-ancestors 'none'";
      await next();
  });
  ```

#### High: Container runs as root
- **File:** `docker-compose.yml`
- **Line(s):** 13 (`user: root`)
- **CWE:** CWE-250: Execution with Unnecessary Privileges
- **Description:** `gordon-worker` is explicitly pinned to `user: root`, overriding the non-root `app` user in the Dockerfile. A code-execution or file-write vulnerability in the app then runs as root inside the container.
- **Evidence:**
  ```yaml
  gordon-worker:
    build: ...
    user: root
  ```
- **Recommendation:** Remove `user: root` and let the image's default `app` user apply. If a writable key path is needed, `chown` it in the Dockerfile rather than escalating the runtime user.

#### Medium: Exception messages echoed to HTTP clients
- **File:** `GordonWorker/Controllers/UsersController.cs`, `SettingsController.cs`, `TransactionsController.cs`, `SimulationController.cs`
- **Line(s):** `UsersController.cs:56, 101, 129`; `SettingsController.cs:136, 150, 177, 204, 225, 292, 378, 392, 423, 445`; `TransactionsController.cs:66, 87`
- **CWE:** CWE-209: Information Exposure Through an Error Message
- **Description:** Controllers return the raw `ex.Message` to the caller on failure. Exception messages often contain database identifiers, stack-like detail, or configuration hints that help an attacker.
- **Evidence:**
  ```csharp
  catch (Exception ex)
  {
      _logger.LogError(ex, "Update user failed.");
      return StatusCode(500, ex.Message);
  }
  ```
- **Recommendation:** Return a generic message; keep the detail in logs only.
  ```csharp
  return StatusCode(500, new { Error = "Internal server error." });
  ```

---

### A03:2025 — Software Supply Chain Failures

#### High: CDN scripts loaded without Subresource Integrity
- **File:** `GordonWorker/wwwroot/index.html`
- **Line(s):** 10, 11, 15
- **CWE:** CWE-494: Download of Code Without Integrity Check
- **Description:** Vue, Tailwind (runtime CDN) and Chart.js are loaded from third-party CDNs without `integrity` or `crossorigin` attributes. A compromise of the CDN or a request-time MITM delivers code that runs on every authenticated page.
- **Evidence:**
  ```html
  <script src="https://unpkg.com/vue@3/dist/vue.global.prod.js?v=3.5"></script>
  <script src="https://cdn.tailwindcss.com"></script>
  <script src="https://cdn.jsdelivr.net/npm/chart.js"></script>
  ```
- **Recommendation:** Pin each script to a specific version and add `integrity="sha384-..."` + `crossorigin="anonymous"`. Prefer vendoring the files locally for production, especially Tailwind (which the Tailwind authors explicitly say not to use from the CDN in production).

---

### A04:2025 — Cryptographic Failures

#### Low: MD5 used to derive deterministic UUIDs
- **File:** `GordonWorker/Services/InvestecClient.cs`
- **Line(s):** 196
- **CWE:** CWE-327: Use of a Broken or Risky Cryptographic Algorithm
- **Description:** `GenerateUuidFromString` uses `MD5` to hash the Investec transaction identifier into a GUID. It is not used for authentication or integrity, but MD5 is deprecated and flagged by most compliance scans; prefer SHA-256 (truncated) or a proper `Guid v5` namespaced hash.
- **Evidence:**
  ```csharp
  private Guid GenerateUuidFromString(string input) {
      using (var md5 = System.Security.Cryptography.MD5.Create()) {
          return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(input)));
      }
  }
  ```
- **Recommendation:** Switch to SHA-256 and take the first 16 bytes, or to a `Guid v5` derivation using a namespace.
  ```csharp
  using var sha = System.Security.Cryptography.SHA256.Create();
  var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
  return new Guid(hash.Take(16).ToArray());
  ```

#### High: JWT signing key decoded as ASCII can silently truncate secrets
- **File:** `GordonWorker/Program.cs`, `GordonWorker/Controllers/AuthController.cs`
- **Line(s):** `Program.cs:41`, `AuthController.cs:89`
- **CWE:** CWE-916: Use of Password Hash With Insufficient Computational Effort / CWE-331 weak entropy when non-ASCII secrets are used
- **Description:** `Encoding.ASCII.GetBytes(jwtSecret)` silently replaces any non-ASCII byte with `?` (0x3F). An operator who generates the recommended `openssl rand -base64 64` (mostly ASCII but may include `+`/`/`) is fine, but if they supply a UTF-8 passphrase they lose entropy. Consistent mishandling also causes key mismatches between tiers.
- **Evidence:**
  ```csharp
  var key = Encoding.ASCII.GetBytes(jwtSecret);
  ```
- **Recommendation:** Use UTF-8 everywhere.
  ```csharp
  var key = Encoding.UTF8.GetBytes(jwtSecret);
  ```

---

### A05:2025 — Injection

No critical injection issues identified. Dapper is used with parameterized queries throughout `Repositories/` and controllers, the AI-generated chart SQL has a deny-list + read-only transaction at `TransactionRepository.GetChartDataAsync`, and external process invocation in `ClaudeCliService` uses `ArgumentList` (no shell). One minor observation:

#### Medium: String interpolation used in SimulationController SQL
- **File:** `GordonWorker/Controllers/SimulationController.cs`
- **Line(s):** 42
- **CWE:** CWE-89: SQL Injection (pattern-level risk, not currently exploitable)
- **Description:** `historyDays` is an `int` loaded from settings, so this is not reachable today, but the pattern `$"... INTERVAL '{historyDays} days'"` is the exact shape that becomes exploitable the moment the type is relaxed. Best corrected now.
- **Evidence:**
  ```csharp
  var history = (await connection.QueryAsync<Transaction>(
      $"SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= NOW() - INTERVAL '{historyDays} days'",
      new { userId })).ToList();
  ```
- **Recommendation:**
  ```csharp
  var cutoff = DateTime.UtcNow.AddDays(-historyDays);
  var history = (await connection.QueryAsync<Transaction>(
      "SELECT * FROM transactions WHERE user_id = @userId AND transaction_date >= @cutoff",
      new { userId, cutoff })).ToList();
  ```

---

### A06:2025 — Insecure Design

#### Critical: Public `/api/auth/register` with no password policy, CAPTCHA or per-endpoint rate limit
- **File:** `GordonWorker/Controllers/AuthController.cs`
- **Line(s):** 26-56
- **CWE:** CWE-521 Weak Password Requirements, CWE-307 Improper Restriction of Excessive Authentication Attempts
- **Description:** Anyone who can reach the app can create accounts; there is no minimum password length, no character-class requirement, and no per-endpoint throttle beyond the global 100 req/min. This enables credential-stuffing against `/login` (also only the global limiter), mass-account creation, and trivially weak passwords like `"a"`.
- **Evidence:**
  ```csharp
  [HttpPost("register")]
  public async Task<IActionResult> Register([FromBody] RegisterModel model)
  {
      if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
          return BadRequest("Username and Password are required.");
      ...
      var passwordHash = BCrypt.Net.BCrypt.HashPassword(model.Password);
  ```
- **Recommendation:** Enforce minimum complexity, apply a dedicated stricter rate-limit policy to `/api/auth/*`, and log auth outcomes for alerting.
  ```csharp
  // policy registered in Program.cs, e.g. "auth" = 10/min/IP
  [EnableRateLimiting("auth")]
  [HttpPost("register")]
  public async Task<IActionResult> Register(...)
  {
      if (!IsPasswordStrong(model.Password)) return BadRequest("Password must be >= 12 chars and mix letters, digits, symbols.");
      ...
  }
  ```

#### Medium: JWTs valid for 7 days with no refresh-rotation hook
- **File:** `GordonWorker/Controllers/AuthController.cs`
- **Line(s):** 99 (`Expires = DateTime.UtcNow.AddDays(7)`)
- **CWE:** CWE-613: Insufficient Session Expiration
- **Description:** The access token lives for a week. A stolen token gives long-term access, and there is no server-side revocation list for JWTs — a refresh-token repository exists but is not wired up in `AuthController`. Tokens are stored in `localStorage` (vulnerable to XSS exfiltration).
- **Evidence:**
  ```csharp
  Expires = DateTime.UtcNow.AddDays(7),
  ```
- **Recommendation:** Shorten access-token life to 1 hour and add a refresh flow using the existing `IRefreshTokenRepository`. At minimum, lower to 24 hours now and make the lifetime configurable.

---

### A07:2025 — Authentication Failures

#### Medium: User enumeration via registration error text
- **File:** `GordonWorker/Controllers/AuthController.cs`
- **Line(s):** 40-41 (`return BadRequest("Username already exists.");`)
- **CWE:** CWE-203: Observable Discrepancy
- **Description:** The register endpoint returns distinct messages for "username taken" vs other validation errors, so an attacker can enumerate valid usernames. Login already uses a single generic message — registration should too, or rate-limit aggressively.
- **Evidence:**
  ```csharp
  if (existingUser != null)
      return BadRequest("Username already exists.");
  ```
- **Recommendation:** Return the same 200/generic response regardless of whether the username exists (and optionally email the legitimate owner) or gate registration behind an invite/admin-only flow. Combined with the stricter rate limit from A06, this kills the enumeration oracle.

---

### A08:2025 — Software or Data Integrity Failures

Covered under A03 (CDN scripts without SRI). AI-generated SQL is executed against the DB but only via a read-only transaction plus a keyword deny-list and mandatory `@userId` filter (`Repositories/TransactionRepository.cs:265-305`). Acceptable given the mitigations; continue to monitor.

_No additional issues identified beyond A03._

---

### A09:2025 — Security Logging and Alerting Failures

#### High: Initial admin password written to application logs
- **File:** `GordonWorker/Services/DatabaseInitializer.cs`
- **Line(s):** 46-56
- **CWE:** CWE-532: Insertion of Sensitive Information into Log File
- **Description:** When `ADMIN_PASSWORD` is not provided, the startup path generates a random password and writes it to logs at `LogCritical`. Logs are persisted via the `LogSinkService`, the file sink (`logs/*/gordon-*.log`), and surfaced through the `/api/Logs` endpoint to any authenticated user. An operator who later adds a second admin can read the bootstrap password.
- **Evidence:**
  ```csharp
  _logger.LogCritical($" INITIAL ADMIN PASSWORD: {adminPass} ");
  ```
- **Recommendation:** Require `ADMIN_PASSWORD` to be set; fail startup if missing in Production. In Development, still do not persist the password — write it to stdout only and/or a one-shot file that is chmod 600.
  ```csharp
  if (string.IsNullOrEmpty(adminPass))
  {
      if (_environment.IsProduction())
          throw new InvalidOperationException("ADMIN_PASSWORD must be configured in production.");
      adminPass = Guid.NewGuid().ToString("N")[..16];
      Console.WriteLine($"[BOOTSTRAP] Temporary admin password: {adminPass} — change immediately.");
  }
  ```

---

### A10:2025 — Mishandling of Exceptional Conditions

#### Medium: Silent empty catch blocks
- **File:** `GordonWorker/Services/DatabaseInitializer.cs` (38-40, 125, 170), `GordonWorker/Services/SettingsService.cs:168`
- **Line(s):** As noted
- **CWE:** CWE-703: Improper Check or Handling of Exceptional Conditions
- **Description:** Multiple `try { ... } catch {}` blocks swallow errors silently. In `SettingsService.GetUserIdByWhatsAppNumberAsync` that means a corrupted settings row is invisible; in `DatabaseInitializer` it means migration failures ship with no trace.
- **Evidence:**
  ```csharp
  try { await connection.ExecuteAsync("ALTER TABLE users ADD COLUMN IF NOT EXISTS role TEXT DEFAULT 'User';"); } catch {}
  ...
  catch { /* Ignore parse errors */ }
  ```
- **Recommendation:** Always log. Prefer `catch (Exception ex) { _logger.LogDebug(ex, "..."); }` so the signal is preserved.

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | 1 | 1 | 0 | 0 | 0 | 17 |
| A02 — Security Misconfiguration    | 0 | 2 | 1 | 0 | 0 | 18 |
| A03 — Supply Chain Failures        | 0 | 1 | 0 | 0 | 0 | 7  |
| A04 — Cryptographic Failures       | 0 | 1 | 0 | 1 | 0 | 9  |
| A05 — Injection                    | 0 | 0 | 1 | 0 | 0 | 4  |
| A06 — Insecure Design              | 1 | 0 | 1 | 0 | 0 | 14 |
| A07 — Authentication Failures      | 0 | 0 | 1 | 0 | 0 | 4  |
| A08 — Data Integrity Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A09 — Logging & Alerting Failures  | 0 | 1 | 0 | 0 | 0 | 7  |
| A10 — Exceptional Conditions       | 0 | 0 | 1 | 0 | 0 | 4  |
| **Total**                           | 2 | 6 | 5 | 1 | 0 | **84** |

> Note: A08's supply-chain-integrity issue is counted under A03 to avoid double-scoring.

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

---

## Remediation Priority

1. **Wildcard domain allow-list in `docker-compose.yml`** — this is shipped in the default deploy story, so real deployments are running with the middleware effectively disabled. Remove `*` and require a concrete host list.
2. **WhatsApp webhook signature bypass** — return `401` on validation failure instead of logging-and-proceeding; fail closed when the Twilio auth token is unconfigured.
3. **Register / login rate-limit + password complexity** — add an `auth` rate-limit policy (≤10 req/min/IP) and enforce strong passwords on both register and admin-create paths.
4. **Bootstrap admin password leak** — stop writing the generated password through `ILogger`; require `ADMIN_PASSWORD` in production.
5. **Security headers + HSTS** — adds baseline defence-in-depth (CSP mitigates XSS-driven token theft from `localStorage`).
6. **Exception-message leakage** — switch controllers to a generic error body and keep detail in logs.
7. **CDN SRI** — pin versions and add `integrity` / `crossorigin` to Vue, Tailwind, Chart.js.
8. **JWT lifetime + encoding** — use UTF-8 for the signing key and shorten access-token lifetime.
9. **Registration user-enumeration** — return the same response whether or not the username exists.
10. **SimulationController string-interpolated SQL** — parameterise for defensiveness even though it isn't exploitable today.
11. **Silent `catch {}` blocks** — log at debug/warning level so signal is preserved.

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
