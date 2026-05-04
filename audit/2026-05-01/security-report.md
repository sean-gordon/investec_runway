# Security Audit Report

**Project:** Gordon Finance Engine (investec_runway)
**Date:** 2026-05-01
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** `GordonWorker/` (ASP.NET Core 8 API + Vue SPA in `wwwroot/`), `db/init.sql`, `docker-compose.yml`, `.env.template`, `appsettings.json`, `Dockerfile`, `entrypoint.sh`, `.github/workflows/`
**Technology Stack:** C# / .NET 8 (ASP.NET Core MVC), PostgreSQL + TimescaleDB (Dapper / Npgsql), JWT bearer auth, BCrypt password hashing, Vue 3 SPA, Telegram / Twilio / Investec integrations, ScottPlot, Claude CLI sub-process.

---

## Executive Summary

Gordon Finance Engine is an ASP.NET Core 8 API backed by PostgreSQL + TimescaleDB, wired to Investec Programmable Banking, an AI categorisation pipeline (Gemini / OpenAI / Anthropic / Ollama), and multi-channel notifications (Telegram bot, Twilio WhatsApp, SMTP email). The 2026-04-30 audit's five findings have all been closed at the points called out — `TelegramController` now uses `CryptographicOperations.FixedTimeEquals`, `AiService.TestConnectionAsync` and `GetAvailableModelsAsync` no longer surface raw `ex.Message`, `InvestecClient.TestConnectivityAsync` returns a stable tag, and the Telegram heartbeat catch passes the exception object to the structured logger.

This audit re-ran the full OWASP 2025 sweep and found **6 new residual findings**. The two most consequential are an **access-control gap on `LogsController`** (any authenticated user can read the global server log files, which contain other users' transactions, request paths, and stack traces) and a **second AI-SQL execution path in `TelegramChatService.HandleChartRequestAsync`** that bypasses the hardened validation gate in `TransactionRepository.GetChartDataAsync` — keyword scan uses substring `Contains`, comments are not stripped, multi-statement blocks are not rejected, the `user_id` filter is not enforced, and the query is not run in a read-only transaction. The remaining four findings are a username-enumeration timing oracle on the login endpoint, a missing SRI hash on the Tailwind play CDN script (across three HTML files), a non-CSPRNG (`Guid.NewGuid()`) used as the dev bootstrap admin password, and a missing `WEBHOOK_INVESTEC_SECRET` documentation entry in `.env.template`.

**Overall Risk Score:** 26 (Moderate Risk)

| Severity | Count |
|----------|-------|
| Critical | 0     |
| High     | 2     |
| Medium   | 3     |
| Low      | 1     |
| Info     | 0     |
| **Total**| **6** |

---

## Findings

### A01:2025 — Broken Access Control

#### High: `LogsController` exposes server-wide logs to every authenticated user

- **File:** `GordonWorker/Controllers/LogsController.cs`
- **Line(s):** 7, 24-85
- **CWE:** CWE-862: Missing Authorization / CWE-200: Exposure of Sensitive Information
- **Description:** The controller is decorated with `[Authorize]` only, so any logged-in user — including a freshly self-registered non-admin — can hit `GET /api/Logs`, `GET /api/Logs/files`, `GET /api/Logs/files/{level}/{date}`, and `GET /api/Logs/files/{level}/{date}/tail`. The log sink (`LogSinkService`) is a single global ring buffer plus daily files under `/app/logs/{info,error,debug}/` that record every user's HTTP requests, AI prompts/responses, transaction sync notes, login successes/failures with the source IP, Telegram updates, and the full exception stack traces produced by the catch-all error paths in every controller. None of this is partitioned by user, which means a low-privilege user can passively harvest other tenants' financial activity, admin login attempts, and operational error detail. `LogSinkService.AddLog` (`LogSinkService.cs:49-73`) and the `[Authorize(Roles = "Admin")]` already used on `UsersController` confirm this is a genuine multi-tenant separation gap.
- **Evidence:**
  ```csharp
  // LogsController.cs:7-12
  [Authorize]
  [ApiController]
  [Route("api/[controller]")]
  public class LogsController : ControllerBase
  {
      private readonly ILogSinkService _sink;
      ...
  ```
  ```csharp
  // LogsController.cs:24-28  -- returns the in-memory ring buffer (cross-tenant)
  [HttpGet]
  public IActionResult GetLogs()
  {
      return Ok(_sink.GetLogs());
  }
  ```
- **Recommendation:** Restrict the controller to administrators. The logs are a global operational surface, not user-scoped data, so role gating is the correct fix.
  ```csharp
  [Authorize(Roles = "Admin")]
  [ApiController]
  [Route("api/[controller]")]
  public class LogsController : ControllerBase
  ```

---

### A02:2025 — Security Misconfiguration

#### Low: `WEBHOOK_INVESTEC_SECRET` is required by `WebhookController` but not documented in `.env.template`

- **File:** `.env.template`, `GordonWorker/Controllers/WebhookController.cs`
- **Line(s):** `.env.template` (full file), `WebhookController.cs:97-100`
- **CWE:** CWE-1188: Initialization of a Resource with an Insecure Default
- **Description:** `WebhookController.ValidateSecret` reads the shared webhook secret from `Webhooks:InvestecSecret` or the `WEBHOOK_INVESTEC_SECRET` environment variable, and fails closed if either is unset (returns 401). The fail-closed posture is correct, but the `.env.template` shipped with the repo only documents `DB_PASSWORD`, `JWT_SECRET`, `INVESTEC_*`, `TZ`, `ADMIN_PASSWORD`, and `ALLOWED_DOMAIN`. Operators following the template will silently deploy with the Investec realtime webhook permanently rejecting every request, which masks the real-time card-push pipeline being non-functional. This is a configuration documentation gap rather than an exploitable bug, but it lowers the chance that the secret is ever set with a strong value.
- **Evidence:**
  ```csharp
  // WebhookController.cs:96-100
  var expected = _configuration["Webhooks:InvestecSecret"]
      ?? Environment.GetEnvironmentVariable("WEBHOOK_INVESTEC_SECRET");

  if (string.IsNullOrWhiteSpace(expected)) return false;
  ```
- **Recommendation:** Add a documented entry to `.env.template`:
  ```bash
  # Shared secret for the Investec real-time webhook (sent in X-Webhook-Secret).
  # Configure the same value on the Investec developer portal. Leaving this
  # empty disables the realtime card-push pipeline (all requests return 401).
  WEBHOOK_INVESTEC_SECRET=
  ```

---

### A03:2025 — Software Supply Chain Failures

#### Medium: Tailwind play-CDN script has no Subresource Integrity hash

- **File:** `GordonWorker/wwwroot/index.html`, `GordonWorker/wwwroot/pricing.html`, `GordonWorker/wwwroot/side-hustle.html`
- **Line(s):** `index.html:16`, `pricing.html:8`, `side-hustle.html:8`
- **CWE:** CWE-829: Inclusion of Functionality from Untrusted Control Sphere / CWE-353: Missing Support for Integrity Check
- **Description:** Vue (`unpkg.com/vue@3.4.21`) and Chart.js (`cdn.jsdelivr.net/npm/chart.js@4.4.2`) are correctly version-pinned and SRI-verified with `integrity="sha384-..."` and `crossorigin="anonymous"`. Tailwind, however, is loaded from `https://cdn.tailwindcss.com` with no version pin and no integrity attribute on all three HTML pages. If `cdn.tailwindcss.com` is compromised or re-routed, the attacker controls a `<script>` tag with full DOM access on every page (including the authenticated dashboard) and can exfiltrate the JWT in `localStorage`, intercept form submissions, or rewrite the financial UI. The 2026-04-30 audit recorded Tailwind as SRI-protected, so this represents a regression in the asset pinning policy.
- **Evidence:**
  ```html
  <!-- index.html:16 (also pricing.html:8 / side-hustle.html:8) -->
  <script src="https://cdn.tailwindcss.com"></script>
  ```
- **Recommendation:** The `cdn.tailwindcss.com` runtime intentionally does not publish stable hashes (it ships its compiler, not a fixed bundle). The two safe options are (a) replace it with a versioned, hashable bundle from jsDelivr, or (b) vendor the compiled CSS locally and drop the runtime entirely. Example for option (a):
  ```html
  <script
      src="https://cdn.jsdelivr.net/npm/@tailwindcss/browser@4.0.6/dist/index.global.js"
      integrity="sha384-<computed>"
      crossorigin="anonymous"
      referrerpolicy="no-referrer"></script>
  ```
  Option (b) — running the Tailwind CLI to emit a fingerprinted CSS file at build time and serving it from `wwwroot/` — is the strongest fix and removes the runtime CDN dependency entirely.

---

### A04:2025 — Cryptographic Failures

#### Low: Dev bootstrap admin password generated from `Guid.NewGuid()`

- **File:** `GordonWorker/Services/DatabaseInitializer.cs`
- **Line(s):** 63
- **CWE:** CWE-338: Use of Cryptographically Weak Pseudo-Random Number Generator
- **Description:** When `ADMIN_PASSWORD` is unset and the environment is non-production, the database initializer generates a one-shot admin password via `Guid.NewGuid().ToString("N").Substring(0, 16)`. .NET v4 GUIDs use 122 bits of randomness, but the entropy source is `RandomNumberGenerator` on most runtimes — except this code only takes the first 16 hex characters (64 bits), which is below the 128-bit baseline expected for password material, and the choice of `Guid` here misleads the reader into assuming the default Guid algorithm is appropriate for password generation. Although this is a dev-only path and the password is printed once to stdout (not logged), the seam exists and is easy to fix.
- **Evidence:**
  ```csharp
  // DatabaseInitializer.cs:62-67
  // Dev / non-production: generate a one-shot password and write it to stdout
  // only — never through the logger (which persists to disk and is reachable
  // from the /api/Logs endpoint).
  adminPass = Guid.NewGuid().ToString("N").Substring(0, 16);
  ```
- **Recommendation:** Use `RandomNumberGenerator.GetBytes` and Base64-encode (or use `RandomNumberGenerator.GetString` on .NET 8) to produce a full-entropy password.
  ```csharp
  var bytes = new byte[18]; // 144 bits → 24 base64 chars
  System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
  adminPass = Convert.ToBase64String(bytes);
  ```

---

### A05:2025 — Injection

#### High: AI-generated SQL on the Telegram path bypasses the central validation gate

- **File:** `GordonWorker/Services/TelegramChatService.cs`
- **Line(s):** 423-432
- **CWE:** CWE-89: SQL Injection / CWE-20: Improper Input Validation
- **Description:** When a Telegram message is classified as a chart request, `AnalyzeChartRequestAsync` returns AI-generated SQL (Gemini / OpenAI / Anthropic / Ollama output) that is then dispatched directly to the database in `HandleChartRequestAsync`. The validation here is far weaker than the equivalent server path through `TransactionRepository.GetChartDataAsync`:
  - The forbidden-keyword scan uses `string.Contains` rather than a word-boundary regex, so `INSERT_DATE` in a CTE alias would falsely trip and (more importantly) `EXEC`, `MERGE`, `COPY`, `CALL`, `VACUUM`, `LISTEN`, `NOTIFY`, `pg_sleep`, and `INTO` are not in the list.
  - SQL comments (`--`, `/* */`) are not stripped, so an AI response of `SELECT ... /* DROP */ ...` would silently slip past.
  - Multi-statement payloads with stray semicolons are not rejected.
  - The query is not required to filter on `user_id`, so an AI output that omits the WHERE clause leaks across tenants.
  - Execution is on the standard connection — there is no read-only transaction, so a missed keyword could mutate state.

  The hardened version of every one of those checks already exists in `TransactionRepository.GetChartDataAsync` (`TransactionRepository.cs:269-309`); the Telegram service simply doesn't route through it.
- **Evidence:**
  ```csharp
  // TelegramChatService.cs:423-432
  // SQL Injection Validation Check
  var forbiddenKeywords = new[] { "DROP", "DELETE", "UPDATE", "INSERT", "TRUNCATE", "ALTER", "GRANT" };
  if (forbiddenKeywords.Any(k => chartSql.Contains(k, StringComparison.OrdinalIgnoreCase)))
  {
      _logger.LogWarning("Blocked potentially malicious SQL generated by AI for user {UserId}: {Sql}", userId, chartSql);
      if (placeholderId > 0) await telegramService.EditMessageAsync(userId, placeholderId, "⚠️ <b>Security Alert</b>...", chatId);
      return;
  }

  var chartDataRaw = await db.QueryAsync<dynamic>(chartSql, new { userId });
  ```
  ```csharp
  // TransactionRepository.cs:269-309 — the hardened gate the Telegram path SHOULD use
  public async Task<IEnumerable<dynamic>> GetChartDataAsync(int userId, string sql)
  {
      var trimmed = sql.Trim().TrimEnd(';').Trim();
      if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
          throw new InvalidOperationException("Only SELECT queries are permitted.");
      if (trimmed.Contains(';'))
          throw new InvalidOperationException("Multiple SQL statements are not permitted.");

      var stripped = StripSqlComments(trimmed);
      var upperSql = stripped.ToUpperInvariant();

      string[] forbidden = { "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "TRUNCATE", "CREATE",
                             "GRANT", "REVOKE", "EXEC", "EXECUTE", "INTO", "MERGE", "COPY",
                             "CALL", "VACUUM", "ANALYZE", "LISTEN", "NOTIFY", "PG_SLEEP" };
      foreach (var keyword in forbidden)
          if (Regex.IsMatch(upperSql, $@"\b{keyword}\b"))
              throw new InvalidOperationException($"Forbidden SQL keyword detected: {keyword}");

      if (!upperSql.Contains("USER_ID"))
          throw new InvalidOperationException("Query must filter on the user_id column.");
      if (!Regex.IsMatch(stripped, @"@userId\b", RegexOptions.IgnoreCase))
          throw new InvalidOperationException("Query must reference the @userId parameter.");
      ...
      await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: txn);
      ...
  }
  ```
- **Recommendation:** Inject `ITransactionRepository` into `TelegramChatService` (it is already DI-registered in `Program.cs:142`) and dispatch through `GetChartDataAsync` so every AI-generated SQL path goes through one hardened gate. Convert the dynamic rows the repository returns into the existing `(Label, Value)` projection.
  ```csharp
  // In ProcessMessageAsync (TelegramChatService)
  var transactionRepository = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
  ...
  // In HandleChartRequestAsync
  IEnumerable<dynamic> chartDataRaw;
  try
  {
      chartDataRaw = await transactionRepository.GetChartDataAsync(userId, chartSql);
  }
  catch (InvalidOperationException ex)
  {
      _logger.LogWarning("Blocked AI SQL for user {UserId}: {Reason}", userId, ex.Message);
      if (placeholderId > 0)
          await telegramService.EditMessageAsync(userId, placeholderId,
              "⚠️ <b>Security Alert</b>\n\nThe analytical engine attempted to generate an unauthorized database command. This request has been blocked.",
              chatId);
      return;
  }
  ```

---

### A06:2025 — Insecure Design

No new issues identified. `[EnableRateLimiting("auth")]` on `AuthController` (per-IP, 10 req/min), 12-character password complexity, 12-hour JWT lifetime, and the uniform `"Registration request received."` reply on duplicate / new accounts remain in place.

---

### A07:2025 — Authentication Failures

#### Medium: Login endpoint leaks username existence via response timing

- **File:** `GordonWorker/Controllers/AuthController.cs`
- **Line(s):** 79-85
- **CWE:** CWE-208: Observable Timing Discrepancy / CWE-204: Observable Response Discrepancy
- **Description:** `Login` short-circuits `BCrypt.Verify` when the user is not found, returning the same generic `Unauthorized("Invalid username or password.")` immediately. When the user is found, BCrypt's adaptive cost forces ~50–250 ms of CPU before the same generic 401 is returned. The string responses match, but the response-time delta is large and stable enough to enumerate valid usernames over the public network — the very oracle the generic error message is designed to prevent. The auth rate limiter (10/min/IP) slows this down but does not eliminate it.
- **Evidence:**
  ```csharp
  // AuthController.cs:79-85
  var user = await connection.QuerySingleOrDefaultAsync<User>(
      "SELECT * FROM users WHERE username = @Username", new { model.Username });

  if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
  {
      _logger.LogWarning("Failed login for username '{User}' from {IP}.", model.Username, HttpContext.Connection.RemoteIpAddress);
      return Unauthorized("Invalid username or password.");
  }
  ```
- **Recommendation:** Always run BCrypt at roughly the same cost — when the user does not exist, run `BCrypt.Verify` against a static dummy hash so the response time is dominated by the hash comparison either way.
  ```csharp
  // Pre-computed once at startup; cost factor must match HashPassword's default (currently 11).
  private const string DummyHash = "$2a$11$abcdefghijklmnopqrstuuVHfYwPjzpDgcLwDlBcAxhg3.JCPV0hi6";

  var user = await connection.QuerySingleOrDefaultAsync<User>(
      "SELECT * FROM users WHERE username = @Username", new { model.Username });

  var passwordOk = user != null
      ? BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash)
      : BCrypt.Net.BCrypt.Verify(model.Password, DummyHash) && false; // always false, full BCrypt cost

  if (!passwordOk)
  {
      _logger.LogWarning("Failed login for username '{User}' from {IP}.", model.Username, HttpContext.Connection.RemoteIpAddress);
      return Unauthorized("Invalid username or password.");
  }
  ```

---

### A08:2025 — Software or Data Integrity Failures

No new issues identified beyond the Tailwind CDN SRI gap captured under A03 (which is the same defect viewed through a supply-chain lens). AI-generated SQL is otherwise validated, and no unsafe deserialization paths over attacker-controlled input were found.

---

### A09:2025 — Security Logging and Alerting Failures

No new issues identified independently. The `LogsController` access-control gap (A01) has a logging-related impact in that the very logs that record auth events become readable by any authenticated user — fixing the role decorator closes that secondary impact at the same time.

---

### A10:2025 — Mishandling of Exceptional Conditions

No new issues identified. The `LogWarning("Heartbeat error: {Msg}", ex.Message)` flagged by the previous audit is now `_logger.LogWarning(ex, "Telegram heartbeat error.")` and other catch blocks correctly forward the exception object to the structured logger.

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | 0 | 1 | 0 | 0 | 0 | 7  |
| A02 — Security Misconfiguration    | 0 | 0 | 0 | 1 | 0 | 2  |
| A03 — Supply Chain Failures        | 0 | 0 | 1 | 0 | 0 | 4  |
| A04 — Cryptographic Failures       | 0 | 0 | 0 | 1 | 0 | 2  |
| A05 — Injection                    | 0 | 1 | 0 | 0 | 0 | 7  |
| A06 — Insecure Design              | 0 | 0 | 0 | 0 | 0 | 0  |
| A07 — Authentication Failures      | 0 | 0 | 1 | 0 | 0 | 4  |
| A08 — Data Integrity Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A09 — Logging & Alerting Failures  | 0 | 0 | 0 | 0 | 0 | 0  |
| A10 — Exceptional Conditions       | 0 | 0 | 0 | 0 | 0 | 0  |
| **Total**                           | 0 | 2 | 2 | 2 | 0 | **26** |

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

---

## Remediation Priority

1. **Lock `LogsController` to admins (A01, High).** Add `[Authorize(Roles = "Admin")]` so the global server logs (which contain cross-tenant request paths, auth events, AI prompts, and stack traces) stop being readable by every authenticated user.
2. **Route Telegram AI-SQL through `TransactionRepository.GetChartDataAsync` (A05, High).** The hardened validator (`SELECT`-only, comment stripping, semicolon block, full forbidden-keyword list with word boundaries, mandatory `user_id` filter, read-only transaction) already exists; the Telegram path must use it instead of the inline shallow check.
3. **Constant-time login comparison (A07, Medium).** Run `BCrypt.Verify` against a fixed dummy hash when the user lookup misses so the response time no longer depends on whether the username exists.
4. **Add SRI / vendor Tailwind (A03, Medium).** Either swap `cdn.tailwindcss.com` for an SRI-pinned `@tailwindcss/browser` build, or vendor the compiled CSS into `wwwroot/`. Repeat across `index.html`, `pricing.html`, and `side-hustle.html`.
5. **Use `RandomNumberGenerator` for the dev bootstrap admin password (A04, Low).** Replace `Guid.NewGuid().ToString("N").Substring(0, 16)` with a 144-bit CSPRNG draw.
6. **Document `WEBHOOK_INVESTEC_SECRET` in `.env.template` (A02, Low).** Operators need to know they must configure it for the realtime card-push pipeline to function.

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
