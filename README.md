# Gordon Finance Engine 🦓

Gordon Finance Engine is a self-hosted, personal financial analytics platform. It is designed to provide professional-grade insights into your personal finances by connecting directly to your bank account and using advanced data analysis techniques.

The system connects to Investec Programmable Banking to retrieve your transaction history, stores it in a specialised time-series database (TimescaleDB), and uses a Large Language Model (LLM) to allow you to ask questions about your money in plain English.

## Key Features

*   **Automated Ingestion:** Automatically pulls your latest transactions from Investec with deterministic deduplication.
*   **Actuarial Logic:** Calculates your "Burn Rate" and projected financial runway using weighted averages that adapt to your recent spending habits.
*   **AI Analyst:** Chat with your financial data using a local LLM (Ollama) or Google Gemini. Ask questions like "How much did I spend on coffee last month?" or "Will I make it to payday?".
*   **Encrypted Persistent Settings:** All configuration (Bank keys, AI settings, Email) is stored securely in the database with encryption at rest, allowing for a seamless experience across multiple devices.
*   **Weekly Reports:** Proactive email reports that summarise your weekly spending, upcoming bills, and financial health.
*   **Privacy First:** Self-hosted on your own hardware (Windows/Linux Docker). Your banking data stays with you.

## Architecture

The system is built using the following technologies:

*   **Core Application:** .NET 8 (C#) Worker Service.
*   **Database:** TimescaleDB (PostgreSQL) for efficient storage of time-series transaction data.
*   **Frontend:** A lightweight, single-page web interface built with Vue.js 3 and Tailwind CSS.
*   **Deployment:** Docker Compose for easy orchestration of all services.

## Getting Started

### Prerequisites

*   Docker and Docker Compose installed on your machine.
*   An Investec Programmable Banking account with API credentials.
*   (Optional) An Ollama instance for local AI, or a Google Gemini API key.

### Installation

1.  **Clone the Repository**
    ```bash
    git clone https://github.com/sean-gordon/investec_runway.git
    cd investec_runway
    ```

2.  **Configure Environment**
    Create your configuration file from the template and generate a random database password.
    
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

3.  **Start the System**
    Launch the application in detached mode.
    ```bash
    docker compose up -d --build
    ```

4.  **Access the Dashboard**
    Open your browser to: [http://localhost:52944](http://localhost:52944)

## Configuration

Once the application is running, you can fine-tune its behavior via the "Configuration" tab in the web interface. **All settings entered here are encrypted and saved to the database.**

*   **Profile:** Set your name and preferred currency format.
*   **Connections:** Manage your Investec API credentials and history depth.
*   **Brain:** Choose between local (Ollama) or cloud (Gemini) AI providers and select your model.
*   **Math:** Adjust the sensitivity of the financial models.
*   **Email:** Configure SMTP settings for weekly reports.

## Troubleshooting

### Ollama (Windows)
If Gordon cannot connect to Ollama after an upgrade, it is likely because Ollama is only listening on localhost.
1. Quit Ollama from the System Tray.
2. Set the Environment Variable `OLLAMA_HOST` to `0.0.0.0`.
3. Restart Ollama.

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

## API Usage

Gordon exposes a REST API that you can use to integrate his financial intelligence into other local services.

**Endpoint:** `POST /chat`
**Port:** `52944` (Default)

**Example Request:**
```bash
curl -X POST http://localhost:52944/chat \
  -H "Content-Type: application/json" \
  -d '{"Message": "How much did I spend on Uber last month?"}'
```

## License

This project is open-source. Please see the LICENSE file for details.