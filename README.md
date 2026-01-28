# Gordon Finance Engine

A self-hosted, containerised financial analytics platform that acts as your personal "Actuarial AI". It synchronises Investec Programmable Banking transactions into a TimescaleDB hypertable, performs advanced statistical analysis (Burn Rate, Volatility, Runway Probability), and provides natural language insights via local LLMs (Ollama) or Cloud AI (Google Gemini).

## Features

-   **Multi-Account Sync**: Automatically fetches transactions for all linked Investec accounts every 60 seconds with deterministic deduplication.
-   **Salary-to-Salary Lifecycle**: Reports are grounded in your actual paycheck dates (e.g. "TCP 131") rather than arbitrary calendar months.
-   **Forward-Looking Projections**:
    -   **Upcoming Expected Payments**: Automatically identifies recurring large overheads (School, Mortgage, Insurance) that haven't hit yet and reserves them in your projections.
    -   **Projected Payday Balance**: Calculates exactly what will be left in your account the moment before your next salary arrives.
-   **Advanced Actuarial Logic**:
    -   **Payday-Targeted Runway**: Conservative estimates of how long your money will truly last, accounting for unpaid overhead.
    -   **Like-for-Like PTD Comparison**: Compares current spending strictly against the same point in the *previous* salary cycle.
    -   **Volatility & Trend Analysis**: Chronological EMA-weighted burn rate and variance.
-   **AI Analyst (Dual-Brain)**: Connect to **Ollama** (local) or **Google Gemini** (cloud) to interpret data and provide actionable recommendations.
-   **Weekly Reports**: Sends high-quality HTML emails with "Financial Vital Signs" and AI summaries.

## Prerequisites

-   Docker Desktop
-   Investec Programmable Banking credentials (Client ID, Secret, API Key)
-   **AI Provider**: Either an Ollama instance or a Google Gemini API Key.
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

*   **Intelligence Engine**: Switch between Ollama and Gemini. Test connections and select models from dynamic dropdowns.
*   **Data Settings**: Set "History Depth" and choose your Currency Culture (e.g., en-ZA for Rands).
*   **Notifications**: Enter SMTP details to enable email reports.

## Usage

### Manual Report
Click **"Generate & Send Report Now"** on the dashboard to trigger an immediate analysis and email.

### Chat API
Send a POST request to query your financial data:
```http
POST http://localhost:52944/chat
Content-Type: application/json

{
  "message": "What is my projected balance for the next payday?"
}
```

### Force Re-Sync
If data looks incorrect, click **"Force Full Re-Sync"** in the Data section. This clears local data and re-calculates all transaction IDs using stable deterministic fingerprinting.

## Architecture

-   **timescaledb**: PostgreSQL with Timescale extension. Stores transactions and persistent system settings.
-   **gordon-worker**: .NET 8 Service.
    -   **Ingestion**: Background service with deterministic deduplication.
    -   **Analysis**: `ActuarialService` performing payday-targeted math.
    -   **AI Service**: Abstracted LLM communication (Ollama/Gemini).
    -   **API/UI**: Serves the Vue.js frontend and REST endpoints.