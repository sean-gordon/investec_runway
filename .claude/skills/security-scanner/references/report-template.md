# Security Audit Report

**Project:** [PROJECT_NAME]
**Date:** [YYYY-MM-DD]
**Auditor:** Claude Code Security Scanner
**Framework:** OWASP Top 10:2025
**Scope:** [LIST_OF_DIRECTORIES_AND_FILES_ANALYZED]
**Technology Stack:** [LANGUAGES_FRAMEWORKS_DETECTED]

---

## Executive Summary

[2-3 paragraph overview: what was analyzed, key risk areas found, overall risk posture, most urgent items to address]

**Overall Risk Score:** [SCORE] ([Low/Moderate/High/Critical] Risk)

| Severity | Count |
|----------|-------|
| Critical | [X]   |
| High     | [X]   |
| Medium   | [X]   |
| Low      | [X]   |
| Info     | [X]   |
| **Total**| **[X]** |

---

## Findings

### A01:2025 — Broken Access Control

[If findings exist, list each one using the format below. If no findings, write: "No issues identified. Checked: [list what was checked]."]

#### [SEVERITY] [Finding Title]
- **File:** `[path/to/file.ext]`
- **Line(s):** [XX-YY]
- **CWE:** [CWE-XXX: Name]
- **Description:** [What the vulnerability is and why it matters]
- **Evidence:**
  ```[language]
  // vulnerable code snippet from the actual codebase
  ```
- **Recommendation:**
  ```[language]
  // fixed code snippet showing the remediation
  ```

---

### A02:2025 — Security Misconfiguration

[Same format as above]

---

### A03:2025 — Software Supply Chain Failures

[Same format as above]

---

### A04:2025 — Cryptographic Failures

[Same format as above]

---

### A05:2025 — Injection

[Same format as above]

---

### A06:2025 — Insecure Design

[Same format as above]

---

### A07:2025 — Authentication Failures

[Same format as above]

---

### A08:2025 — Software or Data Integrity Failures

[Same format as above]

---

### A09:2025 — Security Logging and Alerting Failures

[Same format as above]

---

### A10:2025 — Mishandling of Exceptional Conditions

[Same format as above]

---

## Risk Score Breakdown

Scoring: Critical = 10 pts, High = 7 pts, Medium = 4 pts, Low = 2 pts, Info = 0 pts.

| Category | Critical | High | Medium | Low | Info | Points |
|----------|----------|------|--------|-----|------|--------|
| A01 — Broken Access Control        | [X] | [X] | [X] | [X] | [X] | [XX] |
| A02 — Security Misconfiguration    | [X] | [X] | [X] | [X] | [X] | [XX] |
| A03 — Supply Chain Failures        | [X] | [X] | [X] | [X] | [X] | [XX] |
| A04 — Cryptographic Failures       | [X] | [X] | [X] | [X] | [X] | [XX] |
| A05 — Injection                    | [X] | [X] | [X] | [X] | [X] | [XX] |
| A06 — Insecure Design              | [X] | [X] | [X] | [X] | [X] | [XX] |
| A07 — Authentication Failures      | [X] | [X] | [X] | [X] | [X] | [XX] |
| A08 — Data Integrity Failures      | [X] | [X] | [X] | [X] | [X] | [XX] |
| A09 — Logging & Alerting Failures  | [X] | [X] | [X] | [X] | [X] | [XX] |
| A10 — Exceptional Conditions       | [X] | [X] | [X] | [X] | [X] | [XX] |
| **Total**                           |     |     |     |     |     | **[XX]** |

**Risk Rating:** 0-10 = Low | 11-30 = Moderate | 31-60 = High | 61+ = Critical

---

## Remediation Priority

[Ordered list of the most critical items to fix first, with brief rationale]

1. **[Most critical finding]** — [why this is urgent and what to do]
2. **[Second most critical]** — [why and what]
3. **[Third most critical]** — [why and what]
[Continue as needed...]

---

## Methodology

This audit was performed using static analysis against the OWASP Top 10:2025 framework. Each category was evaluated using pattern-matching (grep), code review (file reading), dependency analysis, and configuration inspection. The analysis covered source code, configuration files, dependency manifests, and environment settings.

**Limitations:** This is a static analysis — it does not include dynamic/runtime testing, penetration testing, or network-level analysis. Some vulnerabilities may only be discoverable through dynamic testing.

## References

- [OWASP Top 10:2025](https://owasp.org/Top10/2025/)
- [OWASP Application Security Verification Standard](https://owasp.org/www-project-application-security-verification-standard/)
- [OWASP Cheat Sheet Series](https://cheatsheetseries.owasp.org/)
