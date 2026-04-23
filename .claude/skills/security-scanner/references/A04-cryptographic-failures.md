# A04:2025 — Cryptographic Failures

## Overview

Cryptographic Failures is #4 in OWASP Top 10:2025 (down from #2). It covers failures related to lack of cryptography, insufficiently strong cryptography, leaking of cryptographic keys, and related errors. 32 CWEs mapped, 1,665,348 total occurrences, 2,185 CVEs.

## Key CWEs

- **CWE-261**: Weak Encoding for Password
- **CWE-319**: Cleartext Transmission of Sensitive Information
- **CWE-321**: Use of Hard-coded Cryptographic Key
- **CWE-326**: Inadequate Encryption Strength
- **CWE-327**: Use of Broken or Risky Cryptographic Algorithm
- **CWE-328**: Reversible One-Way Hash
- **CWE-330**: Use of Insufficiently Random Values
- **CWE-338**: Use of Cryptographically Weak PRNG
- **CWE-759**: Use of One-Way Hash Without a Salt
- **CWE-916**: Use of Password Hash With Insufficient Computational Effort

## What to Look For

### General Patterns
- Weak hashing algorithms used for passwords (MD5, SHA1, SHA256 without key stretching)
- Missing salt in password hashing
- Hardcoded cryptographic keys, secrets, or API keys in source code
- Sensitive data transmitted without encryption (HTTP, FTP, SMTP)
- Weak random number generation for security tokens (Math.random, rand())
- Cookies missing `Secure` flag (sent over HTTP)
- Sensitive data in logs (passwords, tokens, credit cards, PII)
- Base64 encoding used as "encryption" for tokens or secrets
- Deprecated crypto algorithms (DES, 3DES, RC4, MD5, SHA1)
- Missing HSTS headers
- Hardcoded IVs or nonces in encryption

### Grep Patterns

```
# Weak hashing
createHash\(['"]md5['"]\)|createHash\(['"]sha1['"]\)
hashlib\.md5|hashlib\.sha1
MessageDigest\.getInstance\(['"]MD5['"]\)|MessageDigest\.getInstance\(['"]SHA-1['"]\)
md5\(|sha1\(

# Weak randomness
Math\.random|random\.random|rand\(\)|Random\(\)
uuid.*v1|Date\.now

# Hardcoded secrets/keys
SECRET.*=\s*['"][^'"]{8,}|KEY.*=\s*['"][^'"]{8,}|PASSWORD.*=\s*['"][^'"]{4,}
private.?key|secret.?key|api.?key|access.?token

# Base64 as "encryption"
Buffer\.from.*base64|btoa\(|atob\(
base64\.encode|base64\.decode

# Cookie security flags
httpOnly\s*:\s*false|secure\s*:\s*false|sameSite.*none
Set-Cookie(?!.*Secure)(?!.*HttpOnly)

# Cleartext protocols
http:\/\/(?!localhost)|ftp:\/\/|smtp:\/\/
```

### JavaScript / TypeScript / Node.js
- `crypto.createHash('md5')` or `crypto.createHash('sha1')` for password hashing
- `Math.random()` used for tokens, session IDs, or reset codes
- `Buffer.from(data).toString('base64')` used as a "token" (trivially decodable)
- Session cookies set without `httpOnly: true`, `secure: true`, `sameSite: 'strict'`
- JWT secrets hardcoded in source files
- Missing `bcrypt`, `argon2`, or `scrypt` for password hashing

### Python
- `hashlib.md5()` or `hashlib.sha1()` for passwords
- `random.random()` or `random.randint()` for security tokens (should use `secrets` module)
- `base64.b64encode()` used as encryption

### Java
- `MessageDigest.getInstance("MD5")` or `MessageDigest.getInstance("SHA-1")`
- `java.util.Random` instead of `java.security.SecureRandom`
- Hardcoded keys in `KeySpec` constructors

## Prevention Measures

1. Classify data and identify what needs encryption per privacy laws and regulations
2. Don't store sensitive data unnecessarily — data not retained cannot be stolen
3. Encrypt all sensitive data at rest using strong algorithms (AES-256)
4. Use TLS 1.2+ for all data in transit; enforce with HSTS
5. Store passwords with strong adaptive hashing: Argon2, scrypt, bcrypt, or PBKDF2
6. Always use salts and appropriate work factors
7. Use CSPRNG for all security-sensitive random values
8. Never reuse IVs/nonces with the same key
9. Use authenticated encryption (GCM mode, not ECB/CBC)
10. Rotate cryptographic keys regularly
11. Disable caching for responses containing sensitive data

## Example Attack Scenarios

**Scenario 1 — Weak Password Hashing:**
Password database uses unsalted MD5. Attacker retrieves database via another vulnerability, cracks all passwords via rainbow tables in minutes.

**Scenario 2 — Predictable Tokens:**
Password reset tokens generated with `Math.random()`. Attacker predicts tokens and resets other users' passwords.

## Fix Examples

**Before (MD5 password hashing):**
```typescript
import crypto from 'crypto';
function hashPassword(password: string) {
  return crypto.createHash('md5').update(password).digest('hex');
}
```

**After (bcrypt with salt):**
```typescript
import bcrypt from 'bcrypt';
async function hashPassword(password: string) {
  return bcrypt.hash(password, 12);
}
async function verifyPassword(password: string, hash: string) {
  return bcrypt.compare(password, hash);
}
```

**Before (predictable token):**
```typescript
const resetToken = Math.random().toString(36).substring(2);
```

**After (cryptographically secure token):**
```typescript
import crypto from 'crypto';
const resetToken = crypto.randomBytes(32).toString('hex');
```

## References

- [OWASP A04:2025](https://owasp.org/Top10/2025/A04_2025-Cryptographic_Failures/)
- OWASP Cheat Sheet: Password Storage
- OWASP Cheat Sheet: Cryptographic Storage
- OWASP Cheat Sheet: Transport Layer Protection
