---
name: security-scanner
description: >-
  Performs comprehensive OWASP Top 10:2025 security vulnerability analysis on any codebase.
  Use this skill whenever the user asks to: review code for security, perform a security audit,
  scan for vulnerabilities, find security issues, improve application security, check for OWASP
  compliance, do a penetration test review, assess security posture, look for security flaws,
  scan for security risks, harden an application, or check code for exploits. Also trigger when
  the user mentions OWASP, CVEs, CWEs, security hardening, vulnerability assessment, or asks
  for a security report — even if they don't explicitly say "security scan." This skill works
  on any codebase in any language (JavaScript, TypeScript, Python, Java, Go, Ruby, C#, PHP, etc.).
---

# Security Scanner — OWASP Top 10:2025

Performs a systematic security audit of any codebase against all 10 OWASP 2025 categories. Produces a structured markdown report with severity ratings, code locations, and actionable remediation guidance.

## Execution Flow

Follow these four steps in order. Do not skip any step.

### Step 1: Detect Project Context

Determine whether you are working within an existing project or a blank workspace.

Check for source code by looking for common project indicators:
- `package.json`, `requirements.txt`, `go.mod`, `pom.xml`, `Cargo.toml`, `Gemfile`, `*.csproj`, `composer.json`
- Or any `src/`, `app/`, `lib/` directory containing code files

**If source code is found:** Use the current working directory as the analysis target. Proceed to Step 2.

**If NO source code is found:** Ask the user for a GitHub repository URL. Then clone it:
```bash
gh repo clone <url> ./audit-target
```
Use `./audit-target` as the analysis target directory. Proceed to Step 2.

### Step 2: Reconnaissance

Before scanning for vulnerabilities, understand what you're analyzing. This context shapes which patterns matter most.

1. **Identify the tech stack** — Read the main dependency manifest (package.json, requirements.txt, etc.) to determine language(s), framework(s), and key libraries
2. **Map the project structure** — Use Glob to find all source files and understand the directory layout
3. **Locate entry points** — Find API routes, controllers, handlers, page components (e.g., `**/api/**/*.ts`, `**/routes/**`, `**/controllers/**`, `**/views/**`)
4. **Find config files** — Glob for `**/*.config.*`, `**/.env*`, `**/settings.*`, `**/application.*`
5. **Identify auth modules** — Search for authentication/authorization logic, session management, middleware
6. **Find database access** — Locate ORM models, raw query files, database connection setup

Record your findings — they guide which detection patterns to prioritize in Step 3.

### Step 3: Systematic Analysis

For each OWASP category A01 through A10:

1. **Read the reference file** for that category from `references/` to load the relevant CWEs, detection patterns, and grep expressions
2. **Search the codebase** using the patterns from the reference file — use Grep for pattern matching and Glob for file discovery
3. **Read flagged files** to confirm findings and get exact line numbers
4. **Record each finding** with: file path, line number(s), severity level, CWE, description, evidence (code snippet), and recommended fix

Analyze each category in order:

#### A01: Broken Access Control
See [references/A01-broken-access-control.md](references/A01-broken-access-control.md) for CWEs, detection patterns, and fix examples.

Focus on: missing auth middleware on routes, IDOR (user-controlled IDs without ownership checks), permissive CORS, directory traversal, missing CSRF protection, privilege escalation, force browsing to admin/debug endpoints.

#### A02: Security Misconfiguration
See [references/A02-security-misconfiguration.md](references/A02-security-misconfiguration.md).

Focus on: debug mode in production, default credentials, verbose error messages exposing internals, unnecessary features enabled, missing security headers, hardcoded secrets, exposed environment variables.

#### A03: Software Supply Chain Failures
See [references/A03-software-supply-chain-failures.md](references/A03-software-supply-chain-failures.md).

Focus on: known vulnerable dependency versions, unpinned dependencies, CDN scripts without SRI, missing lock files, dependencies from untrusted sources.

#### A04: Cryptographic Failures
See [references/A04-cryptographic-failures.md](references/A04-cryptographic-failures.md).

Focus on: weak password hashing (MD5, SHA1), missing salt, hardcoded keys/secrets, weak randomness (Math.random for tokens), cookies missing Secure flag, sensitive data in logs, base64 used as "encryption."

#### A05: Injection
See [references/A05-injection.md](references/A05-injection.md).

Focus on: SQL injection (string concatenation in queries), command injection (exec/spawn with user input), XSS (dangerouslySetInnerHTML, innerHTML), eval() with user input, SSRF (fetching user-supplied URLs), template injection.

#### A06: Insecure Design
See [references/A06-insecure-design.md](references/A06-insecure-design.md).

Focus on: missing rate limiting on auth endpoints, no input validation, no password complexity requirements, missing account lockout, unrestricted file uploads, guessable/non-expiring tokens.

#### A07: Authentication Failures
See [references/A07-authentication-failures.md](references/A07-authentication-failures.md).

Focus on: weak/predictable session tokens, sessions that never expire, credentials in logs/URLs, user enumeration via different error messages, reset tokens in API responses, cookies without HttpOnly/Secure/SameSite, hard-coded credentials.

#### A08: Software or Data Integrity Failures
See [references/A08-software-data-integrity-failures.md](references/A08-software-data-integrity-failures.md).

Focus on: eval()/Function() with user input, deserialization of untrusted data, CDN scripts without integrity hashes, mass assignment/prototype pollution, auto-updates without signature verification.

#### A09: Security Logging and Alerting Failures
See [references/A09-security-logging-alerting-failures.md](references/A09-security-logging-alerting-failures.md).

Focus on: passwords/tokens/PII in logs, missing audit logging for auth events, no logging on access control failures, error details exposed to users, console.log-only logging without persistence.

#### A10: Mishandling of Exceptional Conditions
See [references/A10-mishandling-exceptional-conditions.md](references/A10-mishandling-exceptional-conditions.md).

Focus on: empty catch blocks, stack traces returned to users, fail-open patterns, missing error handling on async operations, resource leaks on exceptions, missing transaction rollbacks.

### Step 4: Generate Report

1. Get today's date and create the output directory:
   ```bash
   mkdir -p ./audit/YYYY-MM-DD/
   ```

2. Read the report template from [references/report-template.md](references/report-template.md)

3. Fill in the template with all findings from Step 3 and write the completed report to:
   ```
   ./audit/YYYY-MM-DD/security-report.md
   ```

4. Present a brief summary to the user: total findings by severity, overall risk score, and the top 3 most critical items to address immediately.

## Severity Classification

Assign each finding one of these severity levels:

- **Critical** (10 pts): Actively exploitable with immediate data breach risk. Examples: SQL injection, remote code execution, authentication bypass, exposed credentials, command injection.

- **High** (7 pts): Exploitable with moderate effort, significant impact. Examples: XSS, CSRF, weak cryptography, IDOR, SSRF, known vulnerable dependencies.

- **Medium** (4 pts): Requires specific conditions or must be chained with other vulnerabilities. Examples: missing security headers, verbose errors, user enumeration, missing rate limiting.

- **Low** (2 pts): Defense-in-depth issues, best-practice deviations. Examples: weak password policy, console-only logging, missing SRI on CDN scripts.

- **Info** (0 pts): Observations and recommendations with no direct exploit path. Examples: outdated but non-vulnerable dependencies, missing SBOM, code quality notes.

## Risk Score

Sum all finding points to calculate the overall risk score:
- **0–10**: Low Risk
- **11–30**: Moderate Risk
- **31–60**: High Risk
- **61+**: Critical Risk

## Important Guidelines

- **Read-only analysis**: Never modify any source files in the target project. The audit directory is the only location where files should be written.
- **Cover all 10 categories**: If a category has no findings, still include it in the report with "No issues identified" and note what was checked.
- **Be specific**: Every finding must reference a specific file path and line number(s). Include the actual vulnerable code snippet as evidence.
- **Provide fixes**: Every finding must include an actionable remediation recommendation with a code example showing the fix.
- **No false positives**: Read and understand the code context before flagging. A `console.log` in a build script is not the same as a `console.log` leaking passwords in a login handler.
- **Prioritize**: Order the remediation priority section by actual exploitability and impact, not just severity label.
