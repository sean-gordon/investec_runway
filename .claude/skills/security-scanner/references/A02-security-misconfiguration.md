# A02:2025 — Security Misconfiguration

## Overview

Security Misconfiguration is #2 in OWASP Top 10:2025. 100% of applications tested showed some form of misconfiguration with 719,084 total occurrences across 16 CWEs. This occurs when systems lack proper security setup — missing hardening, unnecessary features enabled, default credentials, verbose errors, or insecure settings.

## Key CWEs

- **CWE-16**: Configuration
- **CWE-260**: Password in Configuration File
- **CWE-489**: Active Debug Code
- **CWE-526**: Exposure of Environment Variables
- **CWE-547**: Use of Hard-Coded Security-Relevant Constants
- **CWE-611**: Improper Restriction of XML External Entity Reference
- **CWE-614**: Sensitive Cookie in HTTPS Session Without 'Secure' Attribute
- **CWE-942**: Permissive Cross-domain Policy
- **CWE-1004**: Sensitive Cookie Without 'HttpOnly' Flag

## What to Look For

### General Patterns
- Debug/development mode enabled in production configs
- Default credentials left in code or config (admin/admin, root/root, test/test)
- Verbose error messages exposing stack traces, SQL queries, or internal paths to users
- Unnecessary features/services enabled (directory listing, debug endpoints, sample apps)
- Missing security headers (CSP, HSTS, X-Frame-Options, X-Content-Type-Options)
- Overly permissive CORS (Access-Control-Allow-Origin: *)
- Server/framework version headers enabled (X-Powered-By, Server)
- Hardcoded secrets in source code (API keys, passwords, tokens)
- Environment variables exposed via debug endpoints or error pages
- XML external entity processing enabled

### Grep Patterns

```
# Debug/development mode
DEBUG\s*=\s*[Tt]rue|debug\s*:\s*true|NODE_ENV.*development
poweredByHeader|x-powered-by

# Default credentials
admin.*admin|password.*password|root.*root|test.*test
default.*password|default.*credential

# Verbose errors returned to client
err\.stack|error\.stack|stackTrace|stack_trace
err\.message|error\.message|e\.getMessage

# Missing security headers
Content-Security-Policy|X-Frame-Options|X-Content-Type-Options
Strict-Transport-Security|Referrer-Policy

# Exposed environment/config
process\.env|os\.environ|System\.getenv
/debug|/health|/status|/info|/env|/actuator

# Hardcoded secrets
SECRET.*=.*['"]|API_KEY.*=.*['"]|PASSWORD.*=.*['"]
private_key|secret_key|access_token
```

### JavaScript / TypeScript / Node.js
- `next.config.js` with `poweredByHeader: true` or missing security headers
- Express without `helmet` middleware
- `.env` or `.env.local` files with secrets not in `.gitignore`
- Debug routes like `/api/debug` or `/api/health` exposing internal state
- `console.log` of sensitive config values
- Error handlers returning `err.stack` or `err.message` to client

### Python (Django/Flask)
- `DEBUG = True` in production settings
- `ALLOWED_HOSTS = ['*']`
- `SECRET_KEY` hardcoded in settings.py
- Flask debug mode: `app.run(debug=True)`

### Java (Spring)
- `spring.jpa.show-sql=true` in production
- Actuator endpoints exposed without authentication (`/actuator/env`, `/actuator/beans`)
- `server.error.include-stacktrace=always`

## Prevention Measures

1. Automate deployment of locked-down environments with unique credentials per environment
2. Remove unnecessary features, components, samples, and documentation
3. Review and update configurations with each security patch
4. Implement segmented architecture (containerization, cloud security groups)
5. Send security directives to clients via headers (CSP, HSTS, etc.)
6. Automate configuration verification across all environments
7. Centralize error handling — never expose stack traces or internal details to users
8. Use identity federation and short-lived credentials instead of static secrets

## Example Attack Scenarios

**Scenario 1:** Sample applications with known vulnerabilities remain on production servers. Default admin credentials unchanged.

**Scenario 2:** Directory listing enabled, allowing attackers to download compiled classes for reverse engineering.

**Scenario 3:** Detailed error messages with stack traces and component versions returned to users.

**Scenario 4:** Cloud storage defaults to public access, exposing sensitive data.

## Fix Examples

**Before (debug endpoint exposing environment):**
```typescript
export async function GET() {
  return Response.json({
    env: process.env,
    nodeVersion: process.version,
    uptime: process.uptime()
  });
}
```

**After (remove debug endpoint entirely, or protect it):**
```typescript
// Delete the debug endpoint entirely in production.
// If needed for ops, protect with admin auth and filter sensitive values:
export async function GET(req) {
  const session = await getAdminSession(req);
  if (!session?.isAdmin) return Response.json({ error: 'Forbidden' }, { status: 403 });
  return Response.json({ uptime: process.uptime(), nodeEnv: process.env.NODE_ENV });
}
```

## References

- [OWASP A02:2025](https://owasp.org/Top10/2025/A02_2025-Security_Misconfiguration/)
- OWASP Testing Guide: Configuration Management
- OWASP ASVS V13 Configuration
- CIS Security Configuration Guides
