# Gordon Finance Engine 🦓

**Version 2.0.0** | Enterprise-Grade Personal Finance Analytics

Gordon Finance Engine is a self-hosted, production-ready financial analytics platform designed to provide professional-grade insights into your personal finances by connecting directly to your bank account and using advanced data analysis techniques with AI-powered intelligence.

The system connects to Investec Programmable Banking to retrieve your transaction history, stores it in a specialised time-series database (TimescaleDB), and uses a Large Language Model (LLM) with automatic fallback to ensure you always get answers to your financial questions.

---

## 🌟 Key Features

### Core Functionality
*   **Automated Ingestion:** Automatically pulls your latest transactions from Investec with deterministic deduplication and rate-limited sync to prevent API throttling.
*   **Actuarial Logic:** Calculates your "Burn Rate" and projected financial runway using weighted averages that adapt to your recent spending habits.
*   **AI Analyst with Fallback:** Chat with your financial data using local LLM (Ollama) or Google Gemini. If the primary AI fails, automatically switches to fallback provider to ensure you **always get a response**.
*   **Encrypted Persistent Settings:** All configuration (Bank keys, AI settings, Email) is stored securely in the database with AES-256 encryption at rest.
*   **Weekly Reports:** Proactive email reports that summarise your weekly spending, upcoming bills, and financial health.
*   **Privacy First:** Self-hosted on your own hardware (Windows/Linux Docker). Your banking data stays with you.

### New in Version 2.0 🆕
*   **🤖 AI Fallback System:** Primary + secondary AI providers with automatic retry and exponential backoff
*   **📱 Telegram Bot (Bulletproof):** Background queue processing ensures messages are never lost and always get responses
*   **❤️ Health Checks:** Monitor system health via `/health`, `/health/ready`, and `/health/live` endpoints
*   **🛡️ Rate Limiting:** Protects API from abuse (100 requests/minute per user/IP)
*   **⚡ Performance Cache:** IMemoryCache with automatic expiration (5-minute TTL)
*   **🔒 Enhanced Security:** JWT validation at startup, configurable allowed domains, HTTPS enforcement in production

---

## 🏗️ Architecture

The system is built using enterprise-grade technologies:

*   **Core Application:** .NET 8 (C#) Worker Service with background job processing
*   **Database:** TimescaleDB (PostgreSQL) for efficient storage of time-series transaction data
*   **Frontend:** Lightweight, single-page web interface built with Vue.js 3 and Tailwind CSS
*   **AI:** Multi-provider support (Ollama/Gemini) with automatic fallback and retry logic
*   **Messaging:** Telegram Bot with Channel-based queue for reliable message processing
*   **Deployment:** Docker Compose for easy orchestration of all services
*   **Monitoring:** Built-in health checks for database, AI services, and external APIs
*   **Security:** JWT authentication, rate limiting, configurable domain restrictions

---

## 🚀 Getting Started

### Prerequisites

*   Docker and Docker Compose installed on your machine
*   An Investec Programmable Banking account with API credentials
*   (Optional) An Ollama instance for local AI, or a Google Gemini API key
*   (Recommended) A secondary AI provider for fallback reliability

### Installation

1.  **Clone the Repository**
    ```bash
    git clone https://github.com/sean-gordon/investec_runway.git
    cd investec_runway
    ```

2.  **Configure Environment**
    Create your configuration file from the template:

    **Linux / Mac:**
    ```bash
    cp .env.template .env
    # Set DB_PASSWORD and TZ in .env
    ```

    **Windows (PowerShell):**
    ```powershell
    Copy-Item .env.template .env
    # Set DB_PASSWORD and TZ in .env
    ```

3.  **Configure Security (REQUIRED)**

    **Generate JWT Secret:**
    ```bash
    openssl rand -base64 64
    ```

    Edit `GordonWorker/appsettings.json`:
    ```json
    {
      "Jwt": {
        "Secret": "YOUR_64_CHARACTER_SECRET_HERE"
      },
      "Security": {
        "AllowedDomains": [
          "localhost",
          "127.0.0.1",
          "yourdomain.com"
        ]
      }
    }
    ```

4.  **Start the System**
    Launch the application in detached mode:
    ```bash
    docker compose up -d --build
    ```

5.  **Access the Dashboard**
    Open your browser to: [http://localhost:52944](http://localhost:52944)

6.  **Verify Health**
    Check system health:
    ```bash
    curl http://localhost:52944/health
    ```

---

## ⚙️ Configuration

Once the application is running, you can fine-tune its behaviour via the "Configuration" tab in the web interface. **All settings entered here are encrypted and saved to the database.**

### Configuration Sections

*   **Profile:** Set your name and preferred currency format
*   **Connections:** Manage your Investec API credentials and history depth
*   **Brain (Primary AI):** Choose between local (Ollama) or cloud (Gemini) AI providers and select your model
*   **Brain (Fallback AI) 🆕:** Configure secondary AI provider for automatic failover
    - Enable/disable fallback
    - Set fallback provider (Ollama/Gemini)
    - Configure fallback credentials
    - Set retry attempts (default: 2)
    - Set timeout (default: 90 seconds)
*   **Math:** Adjust the sensitivity of the financial models
*   **Email:** Configure SMTP settings for weekly reports
*   **Telegram:** Configure Telegram bot token and authorized chat IDs

### AI Fallback Example Configuration

**Recommended Setup:**
- **Primary:** Local Ollama with `deepseek-coder` (fast, private)
- **Fallback:** Google Gemini with API key (reliable, cloud-based)
- **Retry Attempts:** 2
- **Timeout:** 90 seconds

This ensures you **always get a response** even if your local Ollama instance is down.

---

## 🔍 Monitoring & Health

### Health Check Endpoints

Gordon exposes several health check endpoints for monitoring:

| Endpoint | Description | Use Case |
|----------|-------------|----------|
| `/health` | Overall system health | General monitoring |
| `/health/ready` | Readiness check | Kubernetes readiness probe |
| `/health/live` | Liveness check (DB only) | Kubernetes liveness probe |

### Health Check Components

- ✅ **Database:** TimescaleDB connectivity
- ✅ **AI Service:** Primary AI provider reachability
- ✅ **Investec API:** Banking API availability

### Example Health Response

```json
{
  "status": "Healthy",
  "totalDuration": "00:00:01.2345678",
  "entries": {
    "database": { "status": "Healthy" },
    "ai_service": { "status": "Healthy" },
    "investec_api": { "status": "Healthy" }
  }
}
```

---

## 🛠️ Troubleshooting

### AI Service Not Responding

**Symptoms:** Chat or Telegram not getting responses

**Solution:**
1. Check primary AI provider is running:
   ```bash
   curl http://localhost:11434/api/tags  # For Ollama
   ```
2. Verify fallback provider is configured in UI
3. Check logs:
   ```bash
   docker compose logs -f gordon-worker --tail=100
   ```
4. Look for "AI request attempt X/Y" messages

### Ollama (Windows)

If Gordon cannot connect to Ollama after an upgrade, it is likely because Ollama is only listening on localhost.
1. Quit Ollama from the System Tray
2. Set the Environment Variable `OLLAMA_HOST` to `0.0.0.0`
3. Restart Ollama

### Linux Permissions

If you see "Permission Denied" errors in the logs regarding the encryption keys, run:
```bash
sudo chown -R 0:0 keys
```

### Database "finance" Does Not Exist

If the database was not automatically created, run:
```bash
docker exec -it investec_runway-timescaledb-1 psql -U postgres -c "CREATE DATABASE finance;"
docker exec -it investec_runway-timescaledb-1 psql -U postgres -d finance -f /docker-entrypoint-initdb.d/init.sql
```

### Rate Limiting Issues

If you receive HTTP 429 (Too Many Requests):
- Current limit: 100 requests per minute per user/IP
- Wait 1 minute and try again
- If you need higher limits, adjust `Program.cs` rate limiter configuration

### Telegram Not Responding

**Version 2.0+ uses background queue processing:**
1. Messages are queued and processed reliably
2. Check logs for "Telegram request enqueued" messages
3. Verify bot token in configuration
4. Check chat ID is in authorized list

---

## 📡 API Usage

Gordon exposes a REST API that you can use to integrate his financial intelligence into other local services.

### Endpoints

**Chat Endpoint:**
```bash
POST /chat
Authorization: Bearer {JWT_TOKEN}
Content-Type: application/json

{
  "Message": "How much did I spend on Uber last month?"
}
```

**Health Endpoint:**
```bash
GET /health
# No authentication required
```

**Example Request:**
```bash
curl -X POST http://localhost:52944/chat \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -d '{"Message": "How much did I spend on Uber last month?"}'
```

### Rate Limits

- **Authenticated Users:** 100 requests/minute
- **Unauthenticated:** 100 requests/minute per IP
- **Response:** HTTP 429 if limit exceeded

---

## 🔐 Security Features

### Authentication
- JWT-based authentication with configurable secret
- Minimum 32-character secret required
- HTTPS enforcement in production
- Token expiration and refresh

### Authorization
- Domain-based access control
- Configurable allowed domains list
- Telegram chat ID authorization
- WhatsApp number authorization

### Data Protection
- AES-256 encryption for sensitive settings
- Encrypted at rest in database
- Encryption keys persisted to filesystem
- Secure credential storage

### Rate Limiting
- Global rate limiter (100 req/min)
- Per-user and per-IP tracking
- Automatic replenishment
- Protection against abuse

---

## 🐳 Docker Commands

**Start:**
```bash
docker compose up -d
```

**Stop:**
```bash
docker compose down
```

**Rebuild:**
```bash
docker compose build
docker compose up -d
```

**View Logs:**
```bash
docker compose logs -f gordon-worker
docker compose logs -f timescaledb
```

**Check Status:**
```bash
docker compose ps
```

---

## 📊 Performance & Scalability

### Optimizations in v2.0

- ✅ **Settings Cache:** IMemoryCache with 5-minute expiration (reduces DB queries)
- ✅ **Rate-Limited Sync:** Max 5 concurrent transaction syncs (prevents API throttling)
- ✅ **AI Retry Logic:** Exponential backoff (2s, 4s delays)
- ✅ **Background Queue:** Channel-based Telegram processing (handles bursts)
- ✅ **Connection Pooling:** Efficient database connection management

### Resource Requirements

| Component | CPU | Memory | Storage |
|-----------|-----|--------|---------|
| gordon-worker | 1-2 cores | 512MB-1GB | Minimal |
| timescaledb | 1-2 cores | 1-2GB | 10GB+ |
| **Total** | **2-4 cores** | **1.5-3GB** | **10GB+** |

---

## 📚 Documentation

- **CHANGELOG.md:** Version history and upgrade notes
- **CHANGES.md:** Detailed v2.0 release notes
- **GEMINI.md:** Development guidelines and architecture
- **suggestions.md:** Future roadmap and completed features

---

## 🤝 Contributing

Contributions are welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Submit a pull request

---

## 📄 License

This project is open-source. Please see the LICENSE file for details.

---

## 🎯 What's Next?

See `suggestions.md` for the roadmap. Current priorities:

1. Advanced ML categorisation
2. Multi-cycle seasonality analysis
3. Black swan risk modelling
4. Interactive chart visualisation
5. Multi-currency support

---

## 🙏 Credits

Built with ❤️ using .NET 8, TimescaleDB, Vue.js, and AI magic.

Security and reliability improvements by Claude Sonnet 4.5.

---

**Gordon Finance Engine** - Your Personal Chief Financial Officer 🦓
