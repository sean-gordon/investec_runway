# A01:2025 — Broken Access Control

## Overview

Broken Access Control is the #1 vulnerability in OWASP Top 10:2025. 100% of applications tested showed some form of broken access control. It encompasses 40 CWEs with 1,839,701 total occurrences and 32,654 CVEs. Access control enforces policy preventing users from exceeding their permissions — failures enable unauthorized data disclosure, modification, or destruction.

## Key CWEs

- **CWE-200**: Exposure of Sensitive Information to Unauthorized Actor
- **CWE-284**: Improper Access Control
- **CWE-285**: Improper Authorization
- **CWE-352**: Cross-Site Request Forgery (CSRF)
- **CWE-425**: Direct Request (Forced Browsing)
- **CWE-639**: Authorization Bypass Through User-Controlled Key (IDOR)
- **CWE-862**: Missing Authorization
- **CWE-863**: Incorrect Authorization
- **CWE-918**: Server-Side Request Forgery (SSRF)
- **CWE-22**: Path Traversal

## What to Look For

### General Patterns
- Routes/endpoints missing authentication middleware or guards
- Missing authorization/role checks on protected routes (any authenticated user can access admin routes)
- IDOR: user-controlled IDs in URLs or request bodies used to fetch records without ownership verification
- CORS misconfiguration (wildcard `*` or overly permissive origins)
- Directory traversal in file paths (user input used in `path.join`, `fs.readFile`, etc.)
- CSRF: state-changing operations (POST/PUT/DELETE) without CSRF token validation
- Privilege escalation: missing role checks, role stored client-side or in JWT without verification
- Force browsing: admin/debug/internal endpoints accessible without auth

### Grep Patterns

```
# Missing auth middleware on routes
Access-Control-Allow-Origin.*\*
Access-Control-Allow-Credentials.*true

# IDOR patterns — user-controlled ID without ownership check
params\.id|params\.userId|req\.query\.id
request\.getParameter\("acct"\)
findById|findOne.*id

# Path traversal
path\.join.*req\.|path\.resolve.*req\.
\.\.\/|\.\.\\

# Missing CSRF
method.*(POST|PUT|DELETE|PATCH)
csrf|csrfToken|_csrf

# Force browsing / unprotected admin
/admin|/debug|/internal|/api/admin
```

### JavaScript / TypeScript / Node.js
- Express/Next.js routes without auth middleware (`getSession`, `getServerSession`, `requireAuth`)
- API routes that read `params.id` or `query.id` and fetch records without checking ownership against session user
- `next.config.js` with permissive CORS headers
- Missing `withAuth` or session validation wrappers on API handlers

### Python (Django/Flask)
- Views without `@login_required` or `@permission_required` decorators
- `request.GET['id']` used directly in queries without ownership filter
- Missing `CSRF_COOKIE_SECURE` or `CSRF_COOKIE_HTTPONLY` settings
- `CORS_ALLOW_ALL_ORIGINS = True`

### Java (Spring)
- Controllers without `@PreAuthorize` or `@Secured` annotations
- Missing `SecurityFilterChain` configuration
- `@CrossOrigin(origins = "*")`
- Direct use of `request.getParameter()` in database queries without authorization

## Prevention Measures

1. Deny by default — restrict access except for public resources
2. Implement centralized, reusable access control mechanisms
3. Enforce record ownership — users can only access their own records
4. Apply business logic constraints through domain models
5. Disable directory listing; remove metadata/backups from web roots
6. Log access control failures; alert administrators on suspicious patterns
7. Rate limit API/controller access
8. Invalidate sessions server-side on logout; use short-lived JWTs
9. Include functional access control tests in unit and integration suites

## Example Attack Scenarios

**Scenario 1 — Parameter Tampering:**
```
https://example.com/app/accountInfo?acct=notmyacct
```
Attacker modifies the `acct` parameter to access any user's account.

**Scenario 2 — Forced Browsing:**
```
https://example.com/app/admin_getappInfo
```
Unauthenticated users access admin pages via direct URL.

**Scenario 3 — Client-Side Only Controls:**
```bash
curl https://example.com/app/admin_getappInfo
```
Frontend JavaScript protections bypassed via direct API calls.

## Fix Examples

**Before (IDOR vulnerability):**
```typescript
// Any authenticated user can access any note
export async function GET(req, { params }) {
  const note = await db.get('SELECT * FROM notes WHERE id = ?', params.id);
  return Response.json(note);
}
```

**After (ownership check):**
```typescript
export async function GET(req, { params }) {
  const session = await getSession(req);
  if (!session) return Response.json({ error: 'Unauthorized' }, { status: 401 });
  const note = await db.get(
    'SELECT * FROM notes WHERE id = ? AND user_id = ?',
    [params.id, session.userId]
  );
  if (!note) return Response.json({ error: 'Not found' }, { status: 404 });
  return Response.json(note);
}
```

## References

- [OWASP A01:2025](https://owasp.org/Top10/2025/A01_2025-Broken_Access_Control/)
- OWASP Proactive Controls: C1 Access Control
- OWASP ASVS V8 Authorization
- OWASP Authorization Cheat Sheet
