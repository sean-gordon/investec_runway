# A07:2025 — Authentication Failures

## Overview

Authentication Failures is #7 in OWASP Top 10:2025. It encompasses 36 CWEs with 1,120,673 total occurrences and 7,147 CVEs. Authentication failures occur when systems incorrectly validate user identity, allowing attackers to compromise passwords, keys, session tokens, or exploit implementation flaws to assume other users' identities.

## Key CWEs

- **CWE-259**: Use of Hard-coded Password
- **CWE-287**: Improper Authentication
- **CWE-297**: Improper Validation of Certificate with Host Mismatch
- **CWE-307**: Improper Restriction of Excessive Authentication Attempts
- **CWE-384**: Session Fixation
- **CWE-521**: Weak Password Requirements
- **CWE-613**: Insufficient Session Expiration
- **CWE-798**: Use of Hard-coded Credentials
- **CWE-640**: Weak Password Recovery Mechanism

## What to Look For

### General Patterns
- Weak session token generation (predictable, sequential, or base64-encoded user data)
- Sessions that never expire or have excessively long lifetimes
- Credentials appearing in URLs, logs, or error messages
- No password strength requirements
- Session cookies without `HttpOnly`, `Secure`, or `SameSite` flags
- User enumeration via different error responses (valid vs invalid username)
- Password reset tokens returned in API responses (should be sent via email/SMS only)
- Reset tokens that don't expire or can be reused
- Missing multi-factor authentication on critical operations
- Hard-coded credentials in source code
- Custom authentication instead of using established frameworks
- Sessions not invalidated on logout or password change

### Grep Patterns

```
# Session/token generation
session|sessionId|session_id|sessionToken
token.*=.*base64|token.*=.*encode|token.*=.*Math\.random
uuid.*v1|Date\.now.*toString

# Session cookie flags
httpOnly|HttpOnly|http_only
secure\s*:|Secure|secure.*false
sameSite|SameSite|same_site

# Session expiration
maxAge|max_age|expires|expiry|SESSION_LIFETIME
session.*timeout|session.*expire

# User enumeration
user.*not.*found|invalid.*user|no.*account|email.*not.*registered
incorrect.*password|wrong.*password|invalid.*credentials

# Password in logs/URLs
console\.log.*password|logger.*password|log.*password
password.*=.*req\.query|password.*=.*params

# Hard-coded credentials
password.*=.*['"][^'"]+['"]|credential.*=.*['"]
admin.*admin|root.*root|test.*test
DEFAULT_PASSWORD|ADMIN_PASSWORD

# Reset tokens
resetToken|reset_token|verification_token|verificationToken
token.*response|json.*token.*reset
```

### JavaScript / TypeScript / Node.js
- Session token built as `Buffer.from(userId + ':' + timestamp).toString('base64')` — trivially decodable
- `Math.random()` used for session or reset tokens
- Cookie set without `{ httpOnly: true, secure: true, sameSite: 'strict' }`
- Login endpoint returns different messages for "user not found" vs "wrong password"
- `console.log` including password or token values
- Reset token returned directly in JSON response body
- No session invalidation in logout handler

### Python (Django/Flask)
- Custom session management instead of Django's built-in
- `SESSION_COOKIE_HTTPONLY = False` or `SESSION_COOKIE_SECURE = False`
- Different error messages for invalid username vs invalid password
- Flask `session.permanent = True` without `PERMANENT_SESSION_LIFETIME`

### Java (Spring)
- `HttpSession` without timeout configuration
- Custom `AuthenticationProvider` without proper credential validation
- Missing `invalidateHttpSession(true)` in logout configuration
- `BCryptPasswordEncoder` with low strength parameter

## Prevention Measures

1. Implement multi-factor authentication to counter automated attacks
2. Never ship with default or hard-coded credentials
3. Validate passwords against breached credential databases (Have I Been Pwned)
4. Follow NIST 800-63b guidelines for password policies
5. Harden registration and login endpoints against enumeration and brute-force
6. Use consistent, generic error messages ("Invalid credentials" for all auth failures)
7. Rate-limit failed login attempts with logging and alerting
8. Use secure, server-side session managers with high-entropy session IDs
9. Set session cookies with `HttpOnly`, `Secure`, `SameSite=Strict`
10. Invalidate sessions on logout and password change
11. Use established authentication frameworks rather than building custom solutions
12. Send reset tokens only via email/SMS — never return them in API responses

## Example Attack Scenarios

**Scenario 1 — Credential Stuffing:** Attacker uses known username/password pairs from breaches, modified with predictable patterns (Winter2025 → Winter2026). Without brute-force protection, the app becomes a password oracle.

**Scenario 2 — Session Hijacking:** Session cookie set without `HttpOnly` flag. XSS vulnerability allows JavaScript to read `document.cookie` and send session token to attacker's server.

**Scenario 3 — Session Persistence:** User closes browser without explicit logout. Session is never invalidated server-side. Next user on shared device accesses the previous user's authenticated session.

## Fix Examples

**Before (user enumeration):**
```typescript
if (!user) return Response.json({ error: 'User not found' }, { status: 401 });
if (!validPassword) return Response.json({ error: 'Incorrect password' }, { status: 401 });
```

**After (consistent error message):**
```typescript
if (!user || !validPassword) {
  return Response.json({ error: 'Invalid credentials' }, { status: 401 });
}
```

**Before (insecure session cookie):**
```typescript
response.cookies.set('session', token, { httpOnly: false, path: '/' });
```

**After (secure session cookie):**
```typescript
response.cookies.set('session', token, {
  httpOnly: true,
  secure: true,
  sameSite: 'strict',
  maxAge: 3600,
  path: '/'
});
```

## References

- [OWASP A07:2025](https://owasp.org/Top10/2025/A07_2025-Authentication_Failures/)
- OWASP Authentication Cheat Sheet
- OWASP Session Management Cheat Sheet
- NIST SP 800-63b Digital Identity Guidelines
