# A10:2025 — Mishandling of Exceptional Conditions

## Overview

Mishandling of Exceptional Conditions is #10 in OWASP Top 10:2025 — a newly introduced category. It covers 24 CWEs with 769,581 total occurrences and 3,416 CVEs. This category addresses deficiencies in error management: programs that fail to prevent, detect, or respond to unusual and unpredictable situations. These failures threaten confidentiality (info disclosure), availability (denial of service), and integrity (data corruption).

## Key CWEs

- **CWE-209**: Generation of Error Message Containing Sensitive Information
- **CWE-234**: Failure to Handle Missing Parameter
- **CWE-274**: Improper Handling of Insufficient Privileges
- **CWE-280**: Improper Handling of Insufficient Permissions
- **CWE-476**: NULL Pointer Dereference
- **CWE-636**: Not Failing Securely (Fail-Open)
- **CWE-252**: Unchecked Return Value
- **CWE-754**: Improper Check for Unusual or Exceptional Conditions
- **CWE-755**: Improper Handling of Exceptional Conditions

## What to Look For

### General Patterns
- Empty catch blocks that swallow errors silently
- Generic error handling that hides root causes
- Missing error handling on async operations (unhandled promise rejections)
- Error responses that expose stack traces, SQL queries, or internal paths to users
- Fail-open patterns: errors cause the system to grant access instead of denying it
- Unchecked return values from security-critical functions
- Missing error handling on file I/O, network calls, database operations
- Partial transaction failures without rollback
- Resource leaks when exceptions occur (file handles, DB connections, memory)
- Missing global/unhandled exception handlers
- Inconsistent error handling across the application

### Grep Patterns

```
# Empty catch blocks
catch\s*\([^)]*\)\s*\{\s*\}|catch\s*\(\s*\)\s*:|except\s*:.*pass
catch.*\{\s*\/\/|catch.*\{\s*\n\s*\}

# Stack trace / internal info exposure
err\.stack|error\.stack|e\.getStackTrace|traceback
err\.message|error\.message|e\.getMessage
res\.json.*err|response.*stack|render.*error

# Fail-open patterns
catch.*return true|catch.*allow|catch.*grant|catch.*next\(\)
catch.*continue|on_error.*pass|rescue.*true

# Unhandled async
\.then\((?!.*\.catch)|async.*(?!try)
unhandledRejection|uncaughtException

# Unchecked returns
=\s*(await\s+)?.*\(.*\)\s*;?\s*$(?!.*if|.*\?|.*throw|.*return)

# Resource cleanup
finally|dispose|close|cleanup|release
try.*open|try.*connect|try.*acquire
```

### JavaScript / TypeScript / Node.js
- `catch (e) {}` — empty catch block, error silently swallowed
- `catch (e) { return res.json({ error: e.message, stack: e.stack }) }` — info disclosure
- Missing `.catch()` on Promises or missing try/catch around `await`
- Express error middleware missing or exposing internals
- `process.on('uncaughtException')` handler missing
- Database/file operations without try/catch in async handlers

### Python (Django/Flask)
- `except: pass` or `except Exception: pass` — swallowing all errors
- `traceback.format_exc()` returned in HTTP response
- Missing `finally` blocks for resource cleanup
- Django `DEBUG = True` in production exposing full tracebacks

### Java (Spring)
- Empty catch blocks: `catch (Exception e) {}`
- `e.printStackTrace()` in production code
- Missing `@ControllerAdvice` global exception handler
- `@ExceptionHandler` returning `e.getMessage()` to client

## Prevention Measures

1. Catch and handle errors at their point of origin with meaningful responses
2. Provide user-friendly error messages — never expose internal details
3. Log all errors with sufficient context for debugging
4. Use global exception handlers as a safety net for unhandled errors
5. Roll back transactions completely on failure — no partial state
6. Apply rate limiting and resource quotas to prevent resource exhaustion
7. Implement proper resource cleanup in `finally` blocks
8. Default to deny (fail-closed) — never grant access on error
9. Conduct stress testing and penetration testing to find edge cases
10. Aggregate repeated identical errors as statistics to prevent log flooding

## Example Attack Scenarios

**Scenario 1 — Denial of Service:** File upload exception leaves resources unreleased. Repeated uploads exhaust system resources, causing downtime until restart.

**Scenario 2 — Information Disclosure:** Database error message exposes table names, column names, and query structure. Attacker uses this to craft targeted SQL injection attacks.

**Scenario 3 — Financial Transaction Compromise:** Network interruption during multi-step transfer. Missing rollback allows attacker to drain accounts or create duplicate transfers.

## Fix Examples

**Before (empty catch + info disclosure):**
```typescript
try {
  const results = db.all(query);
  return Response.json(results);
} catch (e) {
  return Response.json({ error: e.message, sql: query, stack: e.stack });
}
```

**After (proper error handling):**
```typescript
try {
  const results = db.all(query);
  return Response.json(results);
} catch (e) {
  logger.error('Database query failed', { error: e.message, query, stack: e.stack });
  return Response.json({ error: 'An internal error occurred' }, { status: 500 });
}
```

**Before (fail-open):**
```typescript
try {
  const isAuthorized = await checkPermission(user, resource);
  if (!isAuthorized) return deny();
} catch (e) {
  // Auth service is down, let them through
}
return allow();
```

**After (fail-closed):**
```typescript
try {
  const isAuthorized = await checkPermission(user, resource);
  if (!isAuthorized) return deny();
  return allow();
} catch (e) {
  logger.error('Authorization check failed', { user: user.id, resource, error: e.message });
  return deny(); // Default to deny on error
}
```

## References

- [OWASP A10:2025](https://owasp.org/Top10/2025/A10_2025-Mishandling_of_Exceptional_Conditions/)
- OWASP Error Handling Cheat Sheet
- OWASP Logging Cheat Sheet
- OWASP ASVS V16.5 Error Handling
