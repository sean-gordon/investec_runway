# Security Audit Report

**Project:** Gordon Finance Engine (investec_runway)
**Date:** 2026-04-30
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** `GordonWorker/` (ASP.NET Core 8 API + Vue SPA in `wwwroot/`), `db/init.sql`, `docker-compose.yml`, `.env.template`, `appsettings.json`, `Dockerfile`
**Technology Stack:** C# / .NET 8 (ASP.NET Core MVC), PostgreSQL + TimescaleDB (Dapper/Npgsql), JWT Bearer auth, BCrypt password hashing, Vue 3 SPA, Telegram/Twilio/Investec integrations.

---

## Executive Summary

Gordon Finance Engine is an ASP.NET Core 8 API integrated with Investec Programmable Banking, an AI categorisation layer, and multi-channel notifications (Telegram, Twilio WhatsApp, email). Two prior audits (2026-04-23, 2026-04-24) closed a long list of issues — the JWT placeholder check now matches by prefix, exception messages no longer leak from controllers, the `AllowedHosts` is no longer wildcard, CDN scripts ship with SRI, the empty `catch { }` blocks in `ChartService` and `ClaudeCliService` now log at debug.

This audit re-ran the full OWASP 2025 sweep and found **5 new residual findings**. The most consequential is a **timing-attack vulnerability in `TelegramController`** — the SHA-256 webhook token is compared with the `==` operator instead of constant-time comparison, while the equivalent check in `WebhookController` correctly uses `CryptographicOperations.FixedTimeEquals`. The remaining findings are information disclosure: four service paths still surface raw `ex.Message` strings to API consumers (AI model fetch, AI test, Investec connectivity test).

**Overall Risk Score:** 19 (Moderate Risk)

| Severity | Count |
|----------|-------|
| Critical | 0     |
| High     | 1     |
| Medium   | 3     |
| Low      | 1     |
| Info     | 0     |
| **Total**| **5** |

---

## Findings

### A01:2025 — Broken Access Control

No new issues identified. The `SecurityValidationMiddleware` rejects wildcard hosts, enforces per-request host matching against `Security:AllowedDomains`, and applies an Origin-based CSRF filter. Admin endpoints are gated by `[Authorize(Roles = "Admin")]`, and webhook endpoints validate shared secrets (`WebhookController`) or per-user Twilio signatures (`WhatsAppController`) with a fail-closed path.

---

### A02:2025 — Security Misconfiguration

#### Medium: Raw exception messages returned by `AiService.GetAvailableModelsAsync`

- **File:** `GordonWorker/Services/AiService.cs`
- **Line(s):** 402, 438, 473
- **CWE:** CWE-209: Information Exposure Through an Error Message
- **Description:** When fetching available models from Gemini, OpenAI, or Ollama fails, the service returns the raw `ex.Message` inside the model list — which is then surfaced verbatim by `SettingsController.GetModels` and rendered in the SPA's settings UI. Exception text from `HttpClient`, the JSON parser, or the underlying socket layer can disclose internal hostnames, ports, paths, partial response bodies, and driver-level error codes. The previous audits closed similar paths in controllers; this service path was missed.
- **Evidence:**
  ```csharp
  // AiService.cs:399-403
  catch (Exception ex)
  {
      _logger.LogError(ex, "Failed to fetch Gemini models from Google API.");
      return new List<string> { $"Gemini Fetch Error: {ex.Message}" };
  }
  ```
  ```csharp
  // AiService.cs:435-439
  catch (Exception ex)
  {
      _logger.LogError(ex, "Failed to fetch OpenAI models.");
      return new List<string> { $"OpenAI Fetch Error: {ex.Message}" };
  }
  ```
  ```csharp
  // AiService.cs:473
  catch (Exception ex) { _logger.LogError(ex, "Failed to fetch models from {Provider}.", useFallback ? "Fallback AI" : "Primary AI"); return new List<string> { $"Ollama Fetch Error: {ex.Message}" }; }
  ```
- **Recommendation:** Keep the rich exception in the logger and return a generic, human-readable string instead.
  ```csharp
  catch (Exception ex)
  {
      _logger.LogError(ex, "Failed to fetch Gemini models from Google API.");
      return new List<string> { "Error: failed to fetch Gemini models. Check server logs." };
  }
  ```

#### Medium: `AiService.TestConnectionAsync` returns raw `ex.Message`

- **File:** `GordonWorker/Services/AiService.cs`
- **Line(s):** 561
- **CWE:** CWE-209: Information Exposure Through an Error Message
- **Description:** The generic `catch (Exception ex)` arm of the AI connection test returns `(false, ex.Message)`. The error string then flows out via `SettingsController.TestAi`, `TestFallbackAi`, `TestThinkingAi`, and the cached `_statusService.PrimaryAiError` / `FallbackAiError` exposed to every authenticated user via `/api/Settings/status`. This re-introduces the same status-oracle pattern that the 2026-04-24 audit fixed at the controller layer.
- **Evidence:**
  ```csharp
  // AiService.cs:557-562
  catch (Exception ex)
  {
      if (attempt < maxAttempts) continue;
      _logger.LogError(ex, "AI Connection test failed.");
      return (false, ex.Message);
  }
  ```
- **Recommendation:** Log the exception and return a stable, sanitised tag.
  ```csharp
  catch (Exception ex)
  {
      if (attempt < maxAttempts) continue;
      _logger.LogError(ex, "AI Connection test failed.");
      return (false, "AI provider connection failed.");
  }
  ```

#### Medium: `InvestecClient.TestConnectivityAsync` returns raw `ex.Message`

- **File:** `GordonWorker/Services/InvestecClient.cs`
- **Line(s):** 92-96
- **CWE:** CWE-209: Information Exposure Through an Error Message
- **Description:** `TestConnectivityAsync` swallows any exception and returns the raw message. The result is forwarded to authenticated users by `SettingsController.TestInvestec` (`return StatusCode(500, $"Investec connection failed: {error}");`). Failure modes include socket errors, DNS errors, and `JsonException` parse messages — all of which can leak environment detail.
- **Evidence:**
  ```csharp
  // InvestecClient.cs:92-96
  public async Task<(bool Success, string Error)> TestConnectivityAsync()
  {
      try { var token = await AuthenticateAsync(); return (!string.IsNullOrEmpty(token), string.Empty); }
      catch (Exception ex) { return (false, ex.Message); }
  }
  ```
- **Recommendation:** Log via `ILogger<InvestecClient>` and return a generic tag.
  ```csharp
  catch (Exception ex)
  {
      _logger.LogWarning(ex, "Investec connectivity test failed.");
      return (false, "Investec authentication failed.");
  }
  ```

---

### A03:2025 — Software Supply Chain Failures

No issues identified. Vue (`unpkg.com/vue@3.4.21`), Chart.js (`cdn.jsdelivr.net/npm/chart.js@4.4.2`) and Tailwind (`cdn.tailwindcss.com/3.4.3`) are all loaded with `integrity="sha384-..."` and `crossorigin="anonymous"` (`wwwroot/index.html:14-31`).

---

### A04:2025 — Cryptographic Failures

No new issues identified. JWT secret validation now rejects the placeholder prefix (`Program.cs:30-37`) and enforces a 32-character minimum length. Passwords are BCrypt hashed. AES-256 (.NET Data Protection) protects sensitive settings at rest.

---

### A05:2025 — Injection

No issues identified. Dapper uses parameterised queries throughout. AI-generated chart SQL is constrained by a keyword deny-list, runs in a read-only transaction, and is forced to filter by `@userId`. Process invocation in `ClaudeCliService` uses `ArgumentList` (no shell expansion).

---

### A06:2025 — Insecure Design

No new issues identified. `[EnableRateLimiting("auth")]` on `AuthController`, 12-character password complexity, 12-hour JWT lifetime, and uniform `"Registration request received."` response on duplicate / new accounts.

---

### A07:2025 — Authentication Failures

#### High: Telegram webhook secret-token comparison is not constant-time

- **File:** `GordonWorker/Controllers/TelegramController.cs`
- **Line(s):** 83, 107
- **CWE:** CWE-208: Observable Timing Discrepancy / CWE-203: Observable Discrepancy
- **Description:** The `Webhook` route compares the URL-supplied secret token to the SHA-256 hash of the user's bot token using the `==` operator. `==` on `string` short-circuits at the first differing character. An attacker who can submit timed requests against `/telegram/webhook/{token}` can therefore brute-force the 64-character hex token byte-by-byte (~16 candidates × 64 positions on average). The companion `WebhookController.ValidateSecret` (`WebhookController.cs:95-110`) already does the same comparison with `CryptographicOperations.FixedTimeEquals`; `TelegramController` was missed.
- **Evidence:**
  ```csharp
  // TelegramController.cs:79-93
  if (_tokenCache.TryGetValue(token, out var cachedUserId))
  {
      var s = await _settingsService.GetSettingsAsync(cachedUserId);
      var expectedToken = GenerateSecretToken(s.TelegramBotToken ?? "");
      if (token == expectedToken)        // <-- non-constant time
      {
          matchedUserId = cachedUserId;
          matchedSettings = s;
      }
      ...
  }

  // TelegramController.cs:106-112
  var expectedToken = GenerateSecretToken(s.TelegramBotToken);
  if (token != expectedToken) continue;  // <-- non-constant time
  matchedUserId = uid;
  ```
- **Recommendation:** Compare bytes in fixed time and reuse the helper from `WebhookController`.
  ```csharp
  private static bool TokensEqual(string a, string b)
  {
      var aBytes = Encoding.UTF8.GetBytes(a);
      var bBytes = Encoding.UTF8.GetBytes(b);
      return aBytes.Length == bBytes.Length &&
             CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
  }
  // ...
  if (TokensEqual(token, expectedToken)) { ... }
  ```

---

### A08:2025 — Software or Data Integrity Failures

No issues identified. CDN integrity hashes are present (covered under A03). AI-generated SQL runs in a read-only transaction with mandatory user scoping. No unsafe deserialization paths on attacker-controlled input were found.

---

### A09:2025 — Security Logging and Alerting Failures

No new issues identified. The bootstrap admin password is no longer routed through `ILogger`. Auth success/failure events are logged with the remote IP. The `LogsController` is `[Authorize]`-gated and all path inputs are regex-validated.

---

### A10:2025 — Mishandling of Exceptional Conditions

#### Low: Bare `_logger.LogWarning("Heartbeat error: {Msg}", ex.Message)` swallows stack trace

- **File:** `GordonWorker/Services/TelegramChatService.cs`
- **Line(s):** 413
- **CWE:** CWE-703: Improper Check or Handling of Exceptional Conditions
- **Description:** The Telegram chat service heartbeat catch passes only `ex.Message` to the structured logger, dropping the stack trace and inner exceptions. Operationally this loses fault signal — recurring heartbeat failures look like one-line warnings with no actionable context.
- **Evidence:**
  ```csharp
  // TelegramChatService.cs:413
  catch (Exception ex) { _logger.LogWarning("Heartbeat error: {Msg}", ex.Message); }
  ```
- **Recommendation:** Pass the exception object as the first argument so the logger captures the full stack trace.
  ```csharp
  catch (Exception ex) { _logger.LogWarning(ex, "Telegram heartbeat error."); }
  ```

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | 0 | 0 | 0 | 0 | 0 | 0  |
| A02 — Security Misconfiguration    | 0 | 0 | 3 | 0 | 0 | 12 |
| A03 — Supply Chain Failures        | 0 | 0 | 0 | 0 | 0 | 0  |
| A04 — Cryptographic Failures       | 0 | 0 | 0 | 0 | 0 | 0  |
| A05 — Injection                    | 0 | 0 | 0 | 0 | 0 | 0  |
| A06 — Insecure Design              | 0 | 0 | 0 | 0 | 0 | 0  |
| A07 — Authentication Failures      | 0 | 1 | 0 | 0 | 0 | 7  |
| A08 — Data Integrity Failures      | 0 | 0 | 0 | 0 | 0 | 0  |
| A09 — Logging & Alerting Failures  | 0 | 0 | 0 | 0 | 0 | 0  |
| A10 — Exceptional Conditions       | 0 | 0 | 0 | 1 | 0 | 2  |
| **Total**                           | 0 | 1 | 3 | 1 | 0 | **21** |

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

---

## Remediation Priority

1. **Fix the Telegram webhook timing-attack** — replace `==` / `!=` token comparisons in `TelegramController.cs:83,107` with `CryptographicOperations.FixedTimeEquals`. The comparable check in `WebhookController` already does this; mirror it.
2. **Sanitise `AiService.TestConnectionAsync` return value** — replace `(false, ex.Message)` at `AiService.cs:561` with a stable tag so the cached `PrimaryAiError` / `FallbackAiError` cannot be turned into a status oracle.
3. **Sanitise `AiService.GetAvailableModelsAsync` failure paths** — drop `ex.Message` from the model lists at `AiService.cs:402,438,473`.
4. **Sanitise `InvestecClient.TestConnectivityAsync`** — drop `ex.Message` from the `(false, error)` return at `InvestecClient.cs:95`.
5. **Capture full stack trace in the Telegram heartbeat catch** — flip `_logger.LogWarning("Heartbeat error: {Msg}", ex.Message)` to `_logger.LogWarning(ex, "Telegram heartbeat error.")` so operational signal is not lost.

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
