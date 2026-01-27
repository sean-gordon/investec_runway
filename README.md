# Gordon Finance Engine

A self-hosted, containerised financial analytics platform that acts as your personal "Actuarial AI". It synchronises Investec Programmable Banking transactions into a TimescaleDB hypertable, performs advanced statistical analysis (Burn Rate, Volatility, Runway Probability), and provides natural language insights via a local LLM (Ollama).

## Features

-   **Multi-Account Sync**: Automatically fetches transactions for all linked Investec accounts every 60 seconds.
-   **Advanced Actuarial Logic**:
    -   **Runway Prediction**: "How many days until I run out of money?"
    -   **Monte Carlo Simulation**: Calculates the *probability* (0-100%) of surviving the next 30 days.
    -   **Linear Regression**: Forecasts month-end spending based on current daily pace.
    -   **Volatility Analysis**: Measures how erratic your spending is.
-   **AI Analyst**: Connects to an external Ollama instance (via Tailscale/LAN) to interpret data and answer questions in plain English.
-   **Weekly Reports**: Sends beautiful, high-quality HTML emails with "Vital Signs" and AI summaries.
-   **Web Dashboard**: A modern, zero-build Vue.js interface for configuration, manual triggers, and system status.

## Prerequisites

-   Docker Desktop
-   Investec Programmable Banking credentials (Client ID, Secret, API Key)
-   An **Ollama** instance running externally (e.g., on another server via Tailscale).
-   SMTP Server details (e.g., Gmail) for email reports.

## Quick Start

1.  **Clone the repository**:
    ```bash
    git clone https://github.com/sean-gordon/investec_runway.git
    cd investec_runway
    ```

2.  **Environment Variables**:
    Create a `.env` file in the root directory:
    ```env
    INVESTEC_CLIENT_ID=your_client_id
    INVESTEC_SECRET=your_secret
    INVESTEC_API_KEY=your_api_key
    ```

3.  **Run with Docker Compose**:
    ```bash
    docker-compose up -d --build
    ```

4.  **Access the Dashboard**:
    Open your browser to `http://localhost:52944`.

## Configuration (via Dashboard)

Once the app is running, use the dashboard (`http://localhost:52944`) to configure:

*   **AI Brain**: Point to your Ollama URL (e.g., `http://100.x.y.z:11434`) and set the model (e.g., `deepseek-coder`).
*   **Data Settings**: Set "History Depth" (days to sync) and choose your Currency (ZAR, USD, GBP).
*   **Notifications**: Enter SMTP details to enable email reports.
*   **Actuarial Logic**: Adjust sensitivity (Alpha) to make the burn rate more or less reactive to recent spending.

## Usage

### Manual Report
Click **"Generate & Send Report Now"** on the dashboard to trigger an immediate analysis and email.

### Chat API
Send a POST request to query your financial data:
```http
POST http://localhost:52944/chat
Content-Type: application/json

{
  "message": "What is my current runway and risk level?"
}
```

### Force Re-Sync
If data looks incorrect or you want to fetch more history, go to the **Data** section in the dashboard and click **"Force Full Re-Sync"**. This will wipe local data and re-download transactions from Investec.

## Architecture

-   **timescaledb**: PostgreSQL with Timescale extension. Stores transactions and persistent system settings.
-   **gordon-worker**: .NET 8 Service.
    -   **Ingestion**: Background service polling Investec.
    -   **Analysis**: `ActuarialService` performing math.
    -   **API/UI**: Serves the Vue.js frontend and REST endpoints.

## Troubleshooting

-   **"Investec Offline"**: Check the dashboard header. Hover over the badge to see the specific error (e.g., HTTP 401).
-   **No Email**: Use the "Test Email" button in the dashboard to verify SMTP settings.
-   **0 Days Runway**: Ensure you have enough history. Use "Force Full Re-Sync" to backfill up to 180 days.
