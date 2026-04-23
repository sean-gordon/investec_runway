# A09:2025 — Security Logging and Alerting Failures

## Overview

Security Logging and Alerting Failures is #9 in OWASP Top 10:2025. This category covers insufficient logging, monitoring, and alerting that prevent detection of security breaches. It maps to 5 CWEs with 723 CVEs. Real-world impact is severe: a healthcare provider went 7 years undetected after a breach affecting 3.5M records; a major airline suffered a decade-long data breach at a third-party provider.

## Key CWEs

- **CWE-117**: Improper Output Neutralization for Logs
- **CWE-221**: Information Loss or Omission
- **CWE-223**: Omission of Security-relevant Information
- **CWE-532**: Insertion of Sensitive Information into Log File
- **CWE-778**: Insufficient Logging

## What to Look For

### General Patterns
- Sensitive data written to logs (passwords, tokens, credit cards, PII, session IDs)
- Missing audit logging for security-relevant events (login, failed auth, privilege changes, data access)
- No logging on authentication failures or access control failures
- Error messages that expose sensitive information to end users
- Logs without timestamps, request IDs, or sufficient context for forensics
- Log files stored without integrity protection (can be tampered with)
- No centralized log aggregation or monitoring
- Missing alerting on suspicious patterns (brute force, unusual access)
- Unencoded log entries vulnerable to log injection attacks
- Logging only to console/stdout without persistence

### Grep Patterns

```
# Sensitive data in logs
console\.log.*password|console\.log.*token|console\.log.*secret
console\.log.*session|console\.log.*cookie|console\.log.*key
logger\.info.*password|logger\.debug.*token|log\.info.*credential
print\(.*password|print\(.*token|logging\.info.*password

# Missing security event logging
login.*fail|auth.*fail|access.*denied|unauthorized
audit|auditLog|audit_log|security_log|securityEvent

# Log injection risk
console\.log\(.*req\.|logger\.info\(.*req\.body|log\(.*user_input

# Logging framework usage
winston|bunyan|pino|morgan|log4js|logging|logger
console\.log|console\.error|console\.warn

# Error exposure to users
res\.json.*err|response.*error.*message|render.*error.*stack
```

### JavaScript / TypeScript / Node.js
- `console.log('Login attempt:', email, password)` — passwords in logs
- `console.log('Session created:', sessionToken)` — tokens in logs
- Using only `console.log` without a logging framework (no levels, no persistence, no structure)
- Missing logging on failed authentication, failed authorization, and input validation failures
- Error responses including `err.message` or `err.stack` sent to client

### Python (Django/Flask)
- `print(f"User {email} login with password {password}")` — passwords in logs
- Missing `LOGGING` configuration in Django settings
- No audit trail for admin actions
- `logging.debug()` containing sensitive request data

### Java (Spring)
- `logger.info("Auth token: " + token)` — tokens in logs
- Missing Spring Security audit events configuration
- No `@EventListener` for `AuthenticationFailureBadCredentialsEvent`

## Prevention Measures

1. Log all authentication events (success and failure) with sufficient context for forensic analysis
2. Log all access control failures and input validation failures
3. Use structured, machine-readable log formats compatible with log management tools
4. Encode log data properly to prevent log injection attacks
5. Use append-only audit trails with integrity controls for critical events
6. Never log sensitive data: passwords, tokens, credit card numbers, PII
7. Establish monitoring and alerting for suspicious patterns (brute force, mass data access)
8. Implement error-triggered transaction rollbacks where appropriate
9. Use centralized log aggregation (ELK stack, Splunk, Datadog, etc.)
10. Create incident response playbooks tied to alerting thresholds
11. Ensure logs include timestamps, user IDs, IP addresses, and request context

## Example Attack Scenarios

**Scenario 1:** Healthcare provider breached for 7 years undetected due to absent monitoring — 3.5M children's health records compromised.

**Scenario 2:** Major airline suffered a decade-long data breach at third-party cloud provider, discovered only through external investigation.

**Scenario 3:** European airline fined EUR 20M under GDPR after payment system breach exposed 400,000+ customer records — insufficient logging delayed detection.

## Fix Examples

**Before (sensitive data in logs):**
```typescript
console.log(`Login attempt: ${email} / ${password}`);
// ...
console.log(`Session created: ${sessionToken}`);
```

**After (safe logging):**
```typescript
import { logger } from './logger';
logger.info('Login attempt', { email, ip: req.ip, timestamp: new Date().toISOString() });
// ...
logger.info('Session created', { userId: user.id, ip: req.ip });
// Never log passwords, tokens, or session IDs
```

**Before (no security event logging):**
```typescript
if (!user) return Response.json({ error: 'Invalid credentials' }, { status: 401 });
```

**After (with audit logging):**
```typescript
if (!user) {
  logger.warn('Failed login attempt', { email, ip: req.ip, reason: 'user_not_found' });
  return Response.json({ error: 'Invalid credentials' }, { status: 401 });
}
```

## References

- [OWASP A09:2025](https://owasp.org/Top10/2025/A09_2025-Security_Logging_and_Alerting_Failures/)
- OWASP Proactive Controls: C9 Security Logging and Monitoring
- OWASP Cheat Sheet: Application Logging Vocabulary
- OWASP ASVS V16 Security Logging and Error Handling
- NIST SP 800-61r2: Computer Security Incident Handling Guide
