# A08:2025 — Software or Data Integrity Failures

## Overview

Software or Data Integrity Failures is #8 in OWASP Top 10:2025. This category covers failures to maintain trust boundaries and verify that software, code, and data are trustworthy before treating them as valid. It encompasses 14 CWEs with 501,327 total occurrences and 3,331 CVEs. Key concerns include insecure deserialization, code execution from untrusted sources, and CI/CD pipeline integrity.

## Key CWEs

- **CWE-502**: Deserialization of Untrusted Data
- **CWE-829**: Inclusion of Functionality from Untrusted Control Sphere
- **CWE-915**: Improperly Controlled Modification of Dynamically-Determined Object Attributes
- **CWE-494**: Download of Code Without Integrity Check
- **CWE-345**: Insufficient Verification of Data Authenticity
- **CWE-353**: Missing Support for Integrity Check

## What to Look For

### General Patterns
- Deserialization of untrusted data (user-submitted serialized objects)
- `eval()` or `Function()` executing user-provided code
- CDN/external scripts loaded without Subresource Integrity (SRI) hashes
- Auto-update mechanisms without signature verification
- CI/CD pipelines without integrity verification steps
- Unsigned firmware or software packages
- Object property injection via mass assignment
- Dynamic code generation from untrusted input
- Missing digital signatures on critical data exchanges

### Grep Patterns

```
# Deserialization
deserialize|unserialize|pickle\.load|yaml\.load|readObject
JSON\.parse.*req\.|JSON\.parse.*body|JSON\.parse.*user
ObjectInputStream|Marshal\.load|php.*unserialize
fromJson.*untrusted|Gson.*fromJson

# Code execution
eval\(|Function\(|new Function|vm\.runInContext
exec\(|execSync\(|compile\(
setTimeout\(.*['"]|setInterval\(.*['"]

# CDN without integrity
<script.*src=.*http|<link.*href=.*http
integrity=|crossorigin=

# Mass assignment / prototype pollution
Object\.assign\(.*req\.|\.\.\.req\.body|Object\.merge
__proto__|prototype\[|constructor\[

# Auto-update without verification
update.*download|download.*update|auto.*update
checksum|signature|verify.*hash|gpg.*verify
```

### JavaScript / TypeScript / Node.js
- `eval(req.body.data)` or `new Function(req.body.code)()` — executes arbitrary user code
- `JSON.parse()` on untrusted input without schema validation (prototype pollution risk)
- `Object.assign(target, req.body)` — mass assignment allows property injection
- External `<script>` or `<link>` tags without `integrity` attribute
- `__proto__` or `constructor.prototype` manipulation via user input

### Python
- `pickle.load()` or `yaml.load()` (without `Loader=SafeLoader`) on untrusted data
- `eval()` or `exec()` with user input
- `marshal.load()` on untrusted data

### Java
- `ObjectInputStream.readObject()` without input validation — Java deserialization attacks
- `XMLDecoder` with untrusted XML
- Libraries like Apache Commons Collections with known gadget chains

## Prevention Measures

1. Use digital signatures to verify software and data source authenticity
2. Restrict library/dependency consumption to trusted, vetted repositories
3. Use tools like OWASP Dependency Check to verify components are free of known vulnerabilities
4. Enforce code review processes to minimize malicious code introduction
5. Ensure CI/CD pipeline has proper segregation, configuration, and access controls
6. Never deserialize untrusted data — or use serialization formats that don't allow code execution (JSON instead of Java serialization, `yaml.safe_load` instead of `yaml.load`)
7. Add Subresource Integrity (SRI) to all externally loaded scripts/styles
8. Validate and sanitize all input before processing

## Example Attack Scenarios

**Scenario 1:** External service provider gains access to authentication cookies through DNS mapping, enabling session hijacking.

**Scenario 2:** Unsigned firmware update on router/device used as attack vector with no remediation path.

**Scenario 3:** Developers install packages from untrusted sources lacking signature verification, introducing malware.

**Scenario 4:** Java deserialization attack — attacker crafts malicious serialized object that executes arbitrary code upon deserialization.

## Fix Examples

**Before (eval with user input):**
```typescript
export async function POST(req) {
  const { data } = await req.json();
  const result = eval(data); // Arbitrary code execution
  return Response.json({ result });
}
```

**After (safe data processing):**
```typescript
export async function POST(req) {
  const { data } = await req.json();
  const parsed = JSON.parse(data); // Parse as data only, never as code
  const validated = schema.parse(parsed); // Validate against schema
  return Response.json({ result: processData(validated) });
}
```

**Before (Python pickle deserialization):**
```python
import pickle
data = pickle.loads(request.data)  # Arbitrary code execution
```

**After (safe deserialization):**
```python
import json
data = json.loads(request.data)  # JSON cannot execute code
validated = DataSchema(**data)    # Validate against schema
```

## References

- [OWASP A08:2025](https://owasp.org/Top10/2025/A08_2025-Software_or_Data_Integrity_Failures/)
- OWASP Cheat Sheet: Deserialization
- OWASP ASVS: Data Integrity
