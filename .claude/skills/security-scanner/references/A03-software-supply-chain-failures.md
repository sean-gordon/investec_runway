# A03:2025 — Software Supply Chain Failures

## Overview

Software Supply Chain Failures is #3 in OWASP Top 10:2025. This category covers compromises in building, distributing, or updating software — vulnerabilities or malicious changes embedded in third-party code, tools, or dependencies. Notable incidents include SolarWinds (2019, 18,000 orgs compromised), Bybit ($1.5B theft, 2025), and Log4Shell (CVE-2021-44228).

## Key CWEs

- **CWE-937**: Using Components with Known Vulnerabilities
- **CWE-1035**: Using Components from Untrusted Sources
- **CWE-1104**: Use of Unmaintained Third-Party Components
- **CWE-829**: Inclusion of Functionality from Untrusted Control Sphere
- **CWE-494**: Download of Code Without Integrity Check
- **CWE-506**: Embedded Malicious Code

## What to Look For

### General Patterns
- Known vulnerable dependency versions in package manifests
- Unpinned or wildcard dependency versions (`*`, `^`, `~` with major ranges)
- CDN scripts loaded without Subresource Integrity (SRI) hashes
- Missing lock files or significantly outdated lock files
- Dependencies from unofficial or untrusted registries
- No Software Bill of Materials (SBOM) tracking
- Single-person deployment without review gates
- CI/CD pipeline configs with weaker security than production
- Transitive dependencies not tracked or audited

### Grep Patterns

```
# Wildcard or loose versioning
"\*"|"latest"|"\^0\."
">="|"<="|"~"

# CDN scripts without integrity
<script.*src=.*cdn|<link.*href=.*cdn
integrity=|crossorigin=

# Known vulnerable patterns (check versions)
lodash.*4\.17\.(0|1[0-1])  # prototype pollution
axios.*0\.21\.[0-1]         # SSRF
jsonwebtoken.*[5-8]\.       # various CVEs
log4j.*2\.(0|1[0-6])        # Log4Shell

# Package manifest files to check
package\.json|requirements\.txt|Gemfile|go\.mod|pom\.xml|Cargo\.toml|\.csproj
```

### JavaScript / TypeScript / Node.js
- Check `package.json` dependency versions against known CVEs
- Look for `<script src="https://cdn...">` without `integrity` attribute in HTML/JSX
- Run `npm audit` or `yarn audit` mentally — flag packages with known issues
- Check for `package-lock.json` / `yarn.lock` existence and freshness
- Flag use of deprecated packages (e.g., `request`, `querystring`)

### Python
- Check `requirements.txt` for pinned versions with known CVEs
- Look for `pip install` without `--require-hashes`
- Check for `Pipfile.lock` or `poetry.lock`

### Java
- Check `pom.xml` dependency versions against known CVEs
- Look for `<repository>` entries pointing to unofficial Maven repos
- Flag old Spring, Log4j, Jackson, or Apache Commons versions

## Prevention Measures

1. Generate and maintain Software Bill of Materials (SBOM)
2. Track all direct and transitive dependencies
3. Remove unused dependencies and unnecessary components
4. Continuously monitor for CVEs (OWASP Dependency Check, Snyk, npm audit)
5. Obtain components only from official, trusted sources via secure channels
6. Implement Subresource Integrity (SRI) for all CDN-loaded resources
7. Pin dependency versions and use lock files
8. Implement staged rollouts, not simultaneous deployments
9. Harden CI/CD pipelines with MFA and access controls
10. Require code review for all changes before merge

## Example Attack Scenarios

**SolarWinds (2019):** Trusted vendor infiltrated — malware propagated to 18,000 orgs via software updates.

**Log4Shell (2021):** CVE-2021-44228 in Apache Log4j enabled remote code execution, affecting millions of Java applications.

**Shai-Hulud (2025):** First self-propagating npm worm infected 500+ package versions, harvesting developer credentials.

## Fix Examples

**Before (CDN without SRI):**
```html
<script src="https://cdn.example.com/lib.min.js"></script>
```

**After (CDN with SRI):**
```html
<script src="https://cdn.example.com/lib.min.js"
  integrity="sha384-abc123..."
  crossorigin="anonymous"></script>
```

**Before (loose dependency versions):**
```json
{ "lodash": "^4.17.0", "axios": "*" }
```

**After (pinned versions, updated):**
```json
{ "lodash": "4.17.21", "axios": "1.7.2" }
```

## References

- [OWASP A03:2025](https://owasp.org/Top10/2025/A03_2025-Software_Supply_Chain_Failures/)
- OWASP Dependency Check / Dependency Track
- CycloneDX SBOM Standard
- OWASP ASVS: Component Verification
