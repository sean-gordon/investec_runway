# Gordon Finance Engine 🦓

**Version 2.9.0** | Self-hosted personal finance with actuarial projections.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Status](https://img.shields.io/badge/status-production-green.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Database](https://img.shields.io/badge/TimescaleDB-PostgreSQL-blue.svg)

---

Gordon is a containerised personal finance app you run yourself. It pulls transactions from Investec Programmable Banking, stores them in TimescaleDB, and does the actuarial maths so you can see your real burn rate and how many days of runway you have.

It also talks to AI providers (Ollama, Gemini, OpenAI, Anthropic) so you can ask questions about your money in plain English. If one provider is down it falls back to another.

---

## What it does

### Actuarial maths

Calculates your runway in days from current balance, fixed costs, and discretionary spend. Fixed costs are detected by looking for actual South African banking syntax in the transaction description (`DEBIT ORDER`, `MAGTAPE`, `NAEDO`, `EFT`) rather than trusting Investec's loose category tag, which classifies a card swipe at Pizza Hut and a monthly bond payment under the same `DEBIT` label.

There's a What-If tab where you can drop in hypothetical income or expenses and watch the runway shift in real time. Monte Carlo sits underneath the projections. Transactions auto-categorise with ML.

### AI, with proper fallback

Four provider slots: Primary, Fallback, and a separate Thinking Model that reviews the primary's output before it goes back to you. You can mix Ollama, Gemini, OpenAI and Anthropic across the slots however you want.

Model discovery is dynamic for Gemini and OpenAI (queries `/v1/models` live), curated for Anthropic since they don't expose a public list endpoint. The Thinking Model can bounce a bad answer back up to 3 times before giving up, and that loop is decoupled from network retries so a flaky connection doesn't kill the review.

### Bots

Telegram and WhatsApp. Both went through a reliability pass: cached Telegram clients (was creating a new `HttpClient` on every message and exhausting sockets), cached Twilio clients, and an in-memory webhook-token-to-user lookup so latency stays low even with lots of users.

### Security

JWT secrets come from the `JWT_SECRET` env var only — the old `appsettings.json` fallback is gone. Settings (API keys, bank credentials, bot tokens) are encrypted at rest with AES-256 via .NET Data Protection. OWASP Top 10 audit fixes were applied across middleware, auth, and deployment in April 2026 — see `audit/` for the reports.

Your data stays on your hardware.

### Operational

Live KPI dashboard for Bank API, DB and AI provider health. Idempotent transaction sync with a 7-day buffer so duplicates and gaps don't creep in. TimescaleDB hypertables for fast queries over years of history.

---

## Architecture

Three services in Docker Compose:

1. **GordonWorker (.NET 8)** — API ingestion, AI orchestration, actuarial engine, HTTP API
2. **TimescaleDB (PostgreSQL)** — transaction hypertables and encrypted settings
3. **Frontend (Vue.js 3)** — no-build Vue served directly by the backend

---

## Getting started

You need Docker, Investec API credentials, and at least one AI provider (local Ollama or a key for Gemini, OpenAI, or Anthropic).

```bash
git clone https://github.com/sean-gordon/investec_runway.git
cd investec_runway

cp .env.template .env
# edit .env: set DB_PASSWORD and JWT_SECRET

docker compose up -d --build
```

Need a strong JWT secret?

```bash
openssl rand -base64 64
```

Visit [http://localhost:52944](http://localhost:52944) and configure your bank, AI providers, and bot tokens in Settings. Everything you put in there is encrypted before it hits the database.

---

## Common operations

```bash
docker compose up -d                          # start
docker compose logs -f                        # tail logs
docker compose down                           # stop
git pull && docker compose up -d --build      # update
```

A few things that catch people out:

- **429 from Investec.** You're being rate-limited; wait a minute. AI health checks poll every 4 hours now (used to be every 5 minutes) to keep this from compounding.
- **AI not responding.** Check the Health tab first. If you're on Ollama, make sure `OLLAMA_HOST` is reachable from inside the container — `localhost` from your shell isn't `localhost` from inside Docker.
- **Telegram silent.** The webhook double-path bug from 2.7.9 is fixed, but if you upgraded from before then, re-register the webhook from the Settings tab once.

---

## Contributing

Fork, branch, commit, push, PR. Pretty standard.

---

## License

MIT. See [LICENSE](LICENSE).
