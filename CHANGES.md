# Gordon Finance Engine: Security & Reliability Release

## Version: 2.0.0
## Date: 2026-02-16

This release is all about making Gordon faster, safer, and more reliable. We've focused on hardening the security and ensuring the AI engine never leaves you hanging.

---

## 🔴 Security Hardening

### JWT Secret Validation
We now strictly enforce a 32-character minimum for your JWT secret. Gordon won't start if it's missing or too short. We've also removed the hardcoded fallback key to prevent token forgery.

### Domain Locking
You can now configure which domains are allowed to talk to your Gordon instance via the `Security:AllowedDomains` setting. This includes wildcard support for subdomains.

---

## 🟢 AI Reliability

### Auto-Fallback & Retries
If your primary AI (like Ollama) is busy or down, Gordon will now automatically try a backup (like Gemini) with exponential backoff. This ensures you always get a response in Telegram or the dashboard.

### Reliable Telegram Processing
We've moved Telegram messages into a proper background queue (`Channel<T>`). No more lost messages or silent failures—you'll see a progress heartbeat while Gordon is "thinking."

---

## 🚀 Performance

### Smarter Settings Caching
We've replaced the old, unbounded cache with a proper `IMemoryCache`. Settings now auto-refresh every 5 minutes, reducing database load by ~95% while keeping your config up to date.

### Rate-Limited Banking Sync
Background transaction syncs are now limited to 5 concurrent users. This prevents Gordon from being throttled by the Investec API and keeps your database connection pool healthy.

---

## 🛡️ Operations

### Built-in Health Checks
New endpoints at `/health`, `/ready`, and `/live` give you instant feedback on the health of your database, AI services, and banking connection. Perfect for monitoring tools or Kubernetes.

### Global Rate Limiting
Public API endpoints now have a global limit of 100 requests per minute to protect your instance from brute-force or DoS attacks.

---

## 🔧 What you need to do

1. **Update JWT Secret**: Generate a 64-character secret and put it in your environment variables.
2. **Set Allowed Domains**: Update `appsettings.json` with your actual domain names.
3. **Rebuild Docker**: Run `docker compose build` to pull in the new security infrastructure.

---

**Questions?** Check the logs with `docker compose logs -f gordon-worker`.
