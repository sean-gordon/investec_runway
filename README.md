# Gordon Finance Engine 🦓

**Version 2.4.5** | *Your Personal, Actuarial-Grade Financial Platform*

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Status](https://img.shields.io/badge/status-production-green.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Database](https://img.shields.io/badge/TimescaleDB-PostgreSQL-blue.svg)

---

Gordon is a self-hosted, containerised financial analytics platform designed to provide "actuarial-grade" insights into your personal finances. Unlike simple trackers, Gordon helps you understand your "burn rate," project your "runway," and simulate future solvency using advanced Monte Carlo simulations.

He connects directly to **Investec Programmable Banking**, stores history in a robust time-series database, and uses a Multi-Provider AI system (Ollama/Gemini) to offer a natural language interface for your money.

---

## 🌟 Key Features

### 🧠 Actuarial Intelligence
*   **Runway Projections:** Calculates exactly how many days of solvency you have based on current spending and burn rate.
*   **Burn Rate Analysis:** Distinguishes between fixed costs and discretionary spending to give you a true "cost of living."
*   **Simulation Engine:** Runs "what-if" scenarios to see how big purchases impact your long-term financial health.
*   **AI Auto-Categorisation:** Uses advanced machine learning to semantically classify transactions (e.g., "Groceries", "Bills") automatically.

### 🤖 Robust AI Integration
*   **Multi-Provider Support:** Seamlessly switches between local AI (Ollama) and cloud AI (Google Gemini) for maximum reliability.
*   **Automatic Fallback:** If the primary AI is down, Gordon automatically retries and switches providers.
*   **Natural Language Querying:** Ask "How much did I spend on coffee last month?" and get an instant, data-backed answer.

### 🛡️ Enterprise-Grade Security
*   **Self-Hosted:** Your data lives on *your* hardware. No third-party clouds.
*   **Encryption at Rest:** Sensitive configuration (API keys, passwords) is encrypted using AES-256 via .NET Data Protection.
*   **Private by Design:** Banking credentials never leave your secure container environment.

### ⚡ Technical Excellence
*   **TimescaleDB:** Uses a specialized time-series database for lightning-fast querying of years of transaction history.
*   **Deterministic Sync:** Smart ingestion engine that never duplicates transactions, even if the bank API acts up.
*   **Resilient Messaging:** Background queue-based Telegram bot ensures you never miss a notification or reply.
*   **Live KPI Dashboard:** Real-time visibility into the health of your Bank API, Database, and AI providers.

---

## 🏗️ Architecture

Gordon is built as a set of micro-services orchestrated by Docker Compose:

1.  **Gordon Worker (.NET 8):** The core brain. Handles API ingestion, AI orchestration, report generation, and the HTTP API.
2.  **TimescaleDB (PostgreSQL):** Stores financial transactions in hypertables and encrypted user settings.
3.  **Frontend (Vue.js 3):** A "no-build" global Vue dashboard served directly by the backend for simplicity.

---

## 🚀 Getting Started

### Prerequisites
*   **Docker & Docker Compose** installed.
*   **Investec API Credentials** (Client ID & Secret).
*   **(Optional) Google Gemini API Key** or a local **Ollama** instance.

### Installation

1.  **Clone the Repository**
    ```bash
    git clone https://github.com/sean-gordon/investec_runway.git
    cd investec_runway
    ```

2.  **Environment Setup**
    Copy the template to a new `.env` file:
    ```bash
    # Windows (PowerShell)
    Copy-Item .env.template .env
    
    # Linux / Mac
    cp .env.template .env
    ```
    *Edit `.env` and set a strong `DB_PASSWORD`.*

3.  **Security Configuration**
    Open `GordonWorker/appsettings.json` and set a unique, 32+ character string for `Jwt:Secret`.
    ```bash
    # Generate a key if needed:
    openssl rand -base64 64
    ```

4.  **Launch**
    ```bash
    docker compose up -d --build
    ```

5.  **Access**
    Visit [http://localhost:52944](http://localhost:52944) to open the dashboard.

---

## ⚙️ Configuration

Once running, navigate to the **Settings** tab in the dashboard.

*   **Bank Connection:** Enter your Investec OAuth credentials.
*   **AI Providers:** Configure your Primary (e.g., Ollama) and Fallback (e.g., Gemini) providers.
*   **Telegram:** Add your Bot Token to enable chat functionality.
*   **Actuarial Settings:** Define your "Payday" and "Fixed Cost" logic for accurate tuning.

*Note: All settings entered here are encrypted before storage.*

---

## 🛠️ Management & Troubleshooting

| Command | Description |
|---------|-------------|
| `docker compose up -d` | Start services in background |
| `docker compose logs -f` | View live logs |
| `docker compose down` | Stop all services |
| `git pull && docker compose up -d --build` | Update to latest version |

**Common Issues:**
*   **429 Too Many Requests:** You are being rate-limited. Wait a minute and try again.
*   **AI Not Responding:** Check the "Health" tab in the dashboard. Verify `OLLAMA_HOST` is accessible if running locally.

---

## 🤝 Contributing

Contributions are welcome!
1.  Fork the Project
2.  Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3.  Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4.  Push to the Branch (`git push origin feature/AmazingFeature`)
5.  Open a Pull Request

---

## 📄 License

Distributed under the MIT License. See `LICENSE` for more information.
