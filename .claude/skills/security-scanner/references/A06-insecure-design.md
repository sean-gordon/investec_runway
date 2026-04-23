# A06:2025 — Insecure Design

## Overview

Insecure Design is #6 in OWASP Top 10:2025. This category focuses on architectural and design flaws — not implementation bugs. "A secure design can still have implementation defects, but an insecure design cannot be fixed by a perfect implementation." It encompasses 39 CWEs with 729,882 total occurrences. The focus is on missing or ineffective control design around requirements, secure design methodology, and secure development lifecycle.

## Key CWEs

- **CWE-256**: Unprotected Storage of Credentials
- **CWE-269**: Improper Privilege Management
- **CWE-434**: Unrestricted Upload of File with Dangerous Type
- **CWE-501**: Trust Boundary Violation
- **CWE-522**: Insufficiently Protected Credentials
- **CWE-657**: Violation of Secure Design Principles
- **CWE-799**: Improper Control of Interaction Frequency
- **CWE-807**: Reliance on Untrusted Inputs in Security Decisions

## What to Look For

### General Patterns
- Missing rate limiting on sensitive endpoints (login, registration, password reset, OTP verification)
- No input validation or schema validation on API endpoints
- Business logic flaws (no password complexity requirements, unlimited retries)
- Missing account lockout mechanisms after failed login attempts
- Lack of defense in depth (single layer of protection)
- Reset/verification tokens that are guessable, short, or never expire
- Missing CAPTCHA or bot protection on public-facing forms
- Unrestricted file upload (no type/size validation)
- Security decisions based on client-side data
- Missing tenant isolation in multi-tenant applications
- No threat modeling evidence (design assumes trusted users)

### Grep Patterns

```
# Missing rate limiting
rateLimit|rate.?limit|throttle|express-rate-limit|slowDown
login|signin|sign-in|authenticate|register|signup|sign-up
reset.*password|forgot.*password|verify.*otp|verify.*code

# Missing input validation
body\.|req\.body\.|request\.body
zod|yup|joi|ajv|validate|validator|schema
express-validator|class-validator

# Password policy
password.*length|minLength|maxLength|complexity|strength
passwordPolicy|password.*requirements

# File upload without validation
multer|formidable|busboy|upload
fileFilter|allowedTypes|mimeType|fileSize|maxSize

# Token expiration
expires|expiry|expiration|ttl|maxAge
resetToken|verificationToken|otpExpiry

# Account lockout
lockout|maxAttempts|failedAttempts|loginAttempts|accountLock
```

### JavaScript / TypeScript / Node.js
- Express/Next.js API routes without `express-rate-limit` or equivalent middleware
- Login/register endpoints accepting any password (no length/complexity check)
- Password reset tokens using short numeric codes without expiration
- File uploads via `multer` without `fileFilter` or size limits
- Missing input validation — `req.body` used directly without Zod/Joi/Yup schema

### Python (Django/Flask)
- Views without `@ratelimit` decorator on auth endpoints
- Missing `AUTH_PASSWORD_VALIDATORS` in Django settings
- File uploads without `ALLOWED_EXTENSIONS` check
- No `django-axes` or equivalent brute-force protection

### Java (Spring)
- Missing `@RateLimiter` on authentication controllers
- No password policy configuration in `SecurityConfig`
- `MultipartFile` accepted without content type validation

## Prevention Measures

1. Establish a secure development lifecycle with AppSec professionals
2. Build and maintain libraries of secure design patterns and components
3. Use threat modeling for critical authentication, access control, and business logic
4. Integrate security requirements into user stories
5. Write unit and integration tests that validate threat resistance
6. Implement rate limiting on all sensitive endpoints
7. Enforce input validation at every tier (client, API, database)
8. Segregate system layers based on exposure and protection needs
9. Implement robust multi-tenant isolation across all tiers

## Example Attack Scenarios

**Scenario 1:** Recovery workflows using knowledge-based questions ("security questions") — multiple people can know the answers, violating NIST 800-63b.

**Scenario 2:** Cinema booking system allows unlimited group discount reservations without deposit or rate limiting — attacker books hundreds of seats, causing revenue loss.

**Scenario 3:** E-commerce platform lacks bot protection — scalpers buy all limited inventory in seconds using automated tools.

## Fix Examples

**Before (no rate limiting on login):**
```typescript
export async function POST(req) {
  const { email, password } = await req.json();
  const user = await db.get('SELECT * FROM users WHERE email = ?', email);
  // ... verify password and return token
}
```

**After (with rate limiting):**
```typescript
import rateLimit from 'express-rate-limit';

const loginLimiter = rateLimit({
  windowMs: 15 * 60 * 1000, // 15 minutes
  max: 5, // 5 attempts per window
  message: { error: 'Too many login attempts, please try again later' }
});

// Apply loginLimiter middleware to the login route
```

**Before (no password policy):**
```typescript
const { password } = await req.json();
const hash = await bcrypt.hash(password, 12);
```

**After (with password policy):**
```typescript
const { password } = await req.json();
if (password.length < 12) return Response.json({ error: 'Password must be at least 12 characters' }, { status: 400 });
if (!/[A-Z]/.test(password) || !/[0-9]/.test(password)) {
  return Response.json({ error: 'Password must contain uppercase and numbers' }, { status: 400 });
}
const hash = await bcrypt.hash(password, 12);
```

## References

- [OWASP A06:2025](https://owasp.org/Top10/2025/A06_2025-Insecure_Design/)
- OWASP Secure Design Principles Cheat Sheet
- OWASP SAMM Design
- The Threat Modeling Manifesto
