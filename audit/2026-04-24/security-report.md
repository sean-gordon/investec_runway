# Security Audit Report

**Project:** Gordon Finance Engine (investec_runway)
**Date:** 2026-04-24
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** `GordonWorker/` (ASP.NET Core 8 API + Vue SPA in `wwwroot/`), `db/init.sql`, `docker-compose.yml`, `.env.template`, `appsettings.json`, `Dockerfile`
**Technology Stack:** C# / .NET 8 (ASP.NET Core MVC), PostgreSQL + TimescaleDB (Dapper/Npgsql), JWT Bearer auth, BCrypt password hashing, Vue 3 SPA, Telegram/Twilio/Investec integrations.

---

## Executive Summary

Gordon Finance Engine is an ASP.NET Core 8 API wired to Investec Programmable Banking, an AI categorisation layer, and multi-channel notifications (Telegram, Twilio WhatsApp, email). The codebase has already absorbed most fixes from the 2026-04-23 audit: wildcard host allow-list is gone, strict CSP / HSTS / security headers are emitted, the register/login endpoints are throttled (`[EnableRateLimiting("auth")]`) and enforce 12-char password complexity, the Twilio webhook fails closed on signature mismatch, the simulation SQL is parameterised, MD5 was replaced with SHA-256, CDN scripts now ship with SRI, the bootstrap admin password is no longer written through `ILogger`, and JWT keys are decoded as UTF-8.

This audit re-ran the full OWASP 2025 sweep against the current tree and surfaced **5 residual findings** — none critical, but two are easily exploitable and worth fixing together.

The most important residual issue is a **default-JWT-secret check that silently fails**: `Program.cs` compares the configured secret against `"PLEASE_CHANGE_THIS_TO_A_VERY_LONG_RANDOM_STRING_IN_PRODUCTION"`, but `appsettings.json` ships with `"..._MINIMUM_32_CHARACTERS"`. The documented insecure default passes startup validation because the two strings don't match.

The remaining issues are information-disclosure: four API endpoints still return raw `ex.Message` bodies to the client, and the `/api/Settings/status` endpoint exposes `ex.Message` values cached on `ISystemStatusService` (populated from AI, Investec, and Telegram failures).

**Overall Risk Score:** 22 (Moderate Risk)

| Severity | Count |
|----------|-------|
| Critical | 0     |
| High     | 1     |
| Medium   | 3     |
| Low      | 2     |
| Info     | 0     |
| **Total**| **6** |

---

## Findings

### A01:2025 — Broken Access Control

No new issues identified. The `SecurityValidationMiddleware` now rejects the `*` wildcard, enforces per-request host matching against the configured `Security:AllowedDomains`, and applies an Origin-based CSRF filter for browser callers. `docker-compose.yml` uses `${ALLOWED_DOMAIN:-localhost}` (no wildcard), admin endpoints are gated by `[Authorize(Roles = "Admin")]`, and the webhook endpoints validate shared secrets (`WebhookController`) or Twilio signatures (`WhatsAppController`) with a fail-closed path.

---

### A02:2025 — Security Misconfiguration

#### Medium: Exception messages echoed to HTTP clients
- **File:** `GordonWorker/Controllers/SettingsController.cs`, `GordonWorker/Controllers/TelegramController.cs`
- **Line(s):** `SettingsController.cs:150, 177, 204, 225, 292`; `TelegramController.cs:183`
- **CWE:** CWE-209: Information Exposure Through an Error Message
- **Description:** Six controller paths still respond with the raw `ex.Message` string. `ex.Message` from Npgsql, HttpClient or the AI providers commonly contains host names, port numbers, file paths, partial query text, or driver-level error codes that help an attacker map the environment. The prior audit already fixed `UsersController` and `TransactionsController`; these two controllers were missed.
- **Evidence:**
  ```csharp
  // SettingsController.cs:148-150
  catch (Exception ex)
  {
      return StatusCode(500, new { Error = ex.Message });
  }
  ```
  ```csharp
  // SettingsController.cs:289-293
  catch (Exception ex)
  {
      _logger.LogError(ex, "Failed to get models.");
      return Ok(new List<string> { $"SettingsController Error: {ex.Message}" });
  }
  ```
  ```csharp
  // TelegramController.cs:180-184
  catch (Exception ex)
  {
      _logger.LogError(ex, "Failed to setup Telegram webhook.");
      return StatusCode(500, new { Error = ex.Message });
  }
  ```
- **Recommendation:** Log `ex` with the logger and return a generic body.
  ```csharp
  catch (Exception ex)
  {
      _logger.LogError(ex, "Test email failed for user {UserId}", UserId);
      return StatusCode(500, new { Error = "Failed to send test email." });
  }
  ```

#### Medium: Exception detail leaked via `/api/Settings/status`
- **File:** `GordonWorker/Controllers/SettingsController.cs`, `GordonWorker/Controllers/TelegramController.cs`, `GordonWorker/Services/SystemStatusService.cs`
- **Line(s):** `SettingsController.cs:176, 203, 317-365`; `TelegramController.cs:135`
- **CWE:** CWE-209: Information Exposure Through an Error Message
- **Description:** `/api/Settings/status` is available to any authenticated user and returns `AiPrimaryError`, `AiFallbackError`, and `LastTelegramError`. Several call sites populate those fields with the raw `ex.Message` of an upstream failure — e.g. `catch { _statusService.PrimaryAiError = ex.Message; }` — so an attacker with any user account sees the same leaky text as `test-ai`. In particular, `TestConnectivityAsync` on the Investec client already masks detail, but the AI path does not.
- **Evidence:**
  ```csharp
  // SettingsController.cs:173-178
  catch (Exception ex)
  {
      _statusService.IsAiPrimaryOnline = false;
      _statusService.PrimaryAiError = ex.Message;
      return StatusCode(500, new { Error = ex.Message });
  }
  ```
  ```csharp
  // TelegramController.cs:132-135
  catch (Exception ex)
  {
      _logger.LogError(ex, "Error processing Telegram webhook.");
      _statusService.LastTelegramError = ex.Message;
  }
  ```
- **Recommendation:** Store a stable short tag (e.g. `"AI provider unreachable"`, `"telegram webhook error"`) on `ISystemStatusService` instead of the raw exception text. Keep detailed traces in the logger.

#### Low: Silent `catch { }` blocks in services
- **File:** `GordonWorker/Services/ClaudeCliService.cs`, `GordonWorker/Services/ChartService.cs`
- **Line(s):** `ClaudeCliService.cs:75`, `ChartService.cs:16, 66`
- **CWE:** CWE-703: Improper Check or Handling of Exceptional Conditions
- **Description:** Three sites use the empty `catch { }` form. The Chart service is benign (font fallback) and the Claude CLI one is a `process.Kill` safety net, but silent swallowing still hides fault signal.
- **Evidence:**
  ```csharp
  // ClaudeCliService.cs:75
  try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
  // ChartService.cs:16
  try { ScottPlot.Fonts.Default = "DejaVu Sans"; } catch { }
  ```
- **Recommendation:** Log at debug level rather than discarding.
  ```csharp
  catch (Exception ex) { _logger.LogDebug(ex, "ScottPlot default font fallback failed."); }
  ```

---

### A03:2025 — Software Supply Chain Failures

No issues identified. Vue (`unpkg.com/vue@3.5.13`), Chart.js (`cdn.jsdelivr.net/npm/chart.js@4.4.6`) and Tailwind are now loaded with `integrity="sha384-..."` and `crossorigin="anonymous"` in `wwwroot/index.html:14-30`. Lock file is present (`package-lock.json`). Consider vendoring the CDN assets for fully offline deployments.

---

### A04:2025 — Cryptographic Failures

#### High: Default-JWT-secret check does not match the shipped placeholder
- **File:** `GordonWorker/Program.cs`, `GordonWorker/appsettings.json`
- **Line(s):** `Program.cs:28`, `appsettings.json:20`
- **CWE:** CWE-798: Use of Hard-coded Credentials, CWE-1188: Initialization of a Resource with an Insecure Default
- **Description:** The boot check refuses `"PLEASE_CHANGE_THIS_TO_A_VERY_LONG_RANDOM_STRING_IN_PRODUCTION"`, but the default written to `appsettings.json` is `"PLEASE_CHANGE_THIS_TO_A_VERY_LONG_RANDOM_STRING_IN_PRODUCTION_MINIMUM_32_CHARACTERS"`. The second string is longer than 32 characters, so the length check passes too. Result: an operator who forgets to override `JWT_SECRET` starts the app with a publicly documented signing key, and can mint forged tokens at will. This is only not "Critical" because the app won't start at all without *some* secret — here the check silently permits the documented one.
- **Evidence:**
  ```csharp
  // Program.cs:28
  if (string.IsNullOrWhiteSpace(jwtSecret) || jwtSecret == "PLEASE_CHANGE_THIS_TO_A_VERY_LONG_RANDOM_STRING_IN_PRODUCTION")
  ```
  ```json
  // appsettings.json:20
  "Secret": "PLEASE_CHANGE_THIS_TO_A_VERY_LONG_RANDOM_STRING_IN_PRODUCTION_MINIMUM_32_CHARACTERS",
  ```
- **Recommendation:** Match on a prefix, normalise casing, and reject any secret starting with the placeholder string. Better yet, remove the default from `appsettings.json` and require the operator to supply `JWT_SECRET` via environment (already the `.env.template` workflow).
  ```csharp
  const string placeholderPrefix = "PLEASE_CHANGE_THIS";
  if (string.IsNullOrWhiteSpace(jwtSecret) ||
      jwtSecret.StartsWith(placeholderPrefix, StringComparison.OrdinalIgnoreCase))
  {
      throw new InvalidOperationException(
          "JWT Secret is not configured or still uses the shipped placeholder value. " +
          "Set JWT_SECRET to a 32+ char random value before starting.");
  }
  ```

#### Low: `AllowedHosts: "*"` in `appsettings.json`
- **File:** `GordonWorker/appsettings.json`
- **Line(s):** 15
- **CWE:** CWE-346: Origin Validation Error
- **Description:** Kestrel's `AllowedHosts` is set to `"*"`. This is de-duplicated in practice by `SecurityValidationMiddleware`, but is a defence-in-depth regression: Kestrel itself will accept any `Host:` header, which the middleware then has to reject. Flip this to a comma-separated list bound from `ALLOWED_DOMAIN`.
- **Evidence:**
  ```json
  "AllowedHosts": "*"
  ```
- **Recommendation:** Either remove the key (let the middleware be the single source of truth) or set it to the same domain list used by the middleware. Functionally this is duplicated by `SecurityValidationMiddleware` already, so documenting the behaviour in `appsettings.json` is acceptable instead of removing it.

---

### A05:2025 — Injection

No issues identified. Dapper uses parameterised queries throughout (`Repositories/`, `Controllers/`). `SimulationController` now uses the `@cutoff` parameter (no string interpolation). AI-generated chart SQL is run through a keyword deny-list + read-only transaction + mandatory `@userId` filter (`TransactionRepository.GetChartDataAsync`). Process invocation in `ClaudeCliService` uses `ArgumentList` (no shell expansion).

---

### A06:2025 — Insecure Design

No new issues identified. The prior Critical finding (public `/register` with no password policy or rate limit) is fixed: `[EnableRateLimiting("auth")]` is applied to the whole `AuthController`, passwords must be ≥ 12 chars with mixed character classes, and the JWT lifetime has been shortened from 7 days to 12 hours. User enumeration via registration error text is neutralised — both success and duplicate now return the same `"Registration request received."` body.

---

### A07:2025 — Authentication Failures

No new issues identified. Login reuses a generic `"Invalid username or password."` response, failed logins are logged at warning with the remote IP, and webhook endpoints use constant-time secret comparison (`CryptographicOperations.FixedTimeEquals` in `WebhookController.cs:109`).

---

### A08:2025 — Software or Data Integrity Failures

No issues identified. Covered under A03 (CDN SRI now present). AI-generated SQL runs in a read-only transaction with user scoping. No `eval`, `Function`, or unsafe deserialisation paths found on user-controlled input.

---

### A09:2025 — Security Logging and Alerting Failures

No new issues identified. The bootstrap admin password is no longer routed through `ILogger` — in production the absence of `ADMIN_PASSWORD` throws, and in development the generated password is written to stdout only (`DatabaseInitializer.cs:60-68`). Auth success/failure events are logged with IP.

---

### A10:2025 — Mishandling of Exceptional Conditions

Related finding already covered under A02 ("Silent `catch { }` blocks"). No additional issues identified.

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | 0 | 0 | 0 | 0 | 0 | 0  |
| A02 — Security Misconfiguration    | 0 | 0 | 2 | 1 | 0 | 10 |
| A03 — Supply Chain Failures        | 0 | 0 | 0 | 0 | 0 | 0  |
| A04 — Cryptographic Failures       | 0 | 1 | 0 | 1 | 0 | 9  |
| A05 — Injection                    | 0 | 0 | 0 | 0 | 0 | 0  |
| A06 — Insecure Design              | 0 | 0 | 0 | 0 | 0 | 0  |
| A07 — Authentication Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A08 — Data Integrity Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A09 — Logging & Alerting Failures  | 0 | 0 | 0 | 0 | 0 | 0  |
| A10 — Exceptional Conditions       | 0 | 0 | 0 | 0 | 0 | 0  |
| **Total**                           | 0 | 1 | 3 | 2 | 0 | **22 (counted once, no double-scoring)** |

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

The project has moved from "Critical Risk" (prior run) to "Moderate Risk" today.

---

## Remediation Priority

1. **Fix the default-JWT-secret check in `Program.cs`** — currently the check lets the documented insecure default pass. Reject anything starting with `"PLEASE_CHANGE_THIS"` and stop shipping a baked-in default in `appsettings.json`.
2. **Stop returning `ex.Message` in `SettingsController` and `TelegramController`** — swap the remaining six sites for a generic error body, keep the exception text in the logger only.
3. **Stop caching `ex.Message` on `ISystemStatusService`** — store a short, stable tag so the `/status` endpoint cannot be turned into an information-disclosure oracle by any authenticated user.
4. **Tighten `AllowedHosts` in `appsettings.json`** — flip `"*"` to the configured domain list so Kestrel is not a permissive fallback.
5. **Log on empty `catch { }` blocks** — not currently exploitable, but adds operational signal for free.

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
