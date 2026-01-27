# Gordon Finance Engine

A self-hosted, containerised C#.NET application that synchronises Investec Programmable Banking transactions into TimescaleDB and provides a natural language chat interface using an external Ollama instance (e.g., via Tailscale).

## Features

- **Multi-Account Sync**: Automatically fetches transactions for all linked Investec accounts every 60 seconds.
- **Predictive Runway**: Calculates daily burn rate and estimated runway based on configurable Actuarial logic.
- **AI Chat**: Connects to your own Ollama server (local or remote) to answer financial questions in plain English.
- **Weekly Reports**: Sends "Junior High" style summary emails to keep you on track.
- **Web Dashboard**: Modern UI to configure schedules, AI settings, and Actuarial parameters.

## Prerequisites

- Docker Desktop
- Investec Programmable Banking credentials
- An **Ollama** instance running externally (e.g., on another server via Tailscale).

## Setup

1.  **Clone the repository**.
2.  **Environment Variables**:
    Create a `.env` file in the `investec_runway` directory with your Investec credentials:

    ```env
    INVESTEC_CLIENT_ID=your_client_id
    INVESTEC_SECRET=your_secret
    INVESTEC_API_KEY=your_api_key
    ```
    *(Note: SMTP settings for email can also be added here, see source code for keys).*

3.  **Run with Docker Compose**:

    ```bash
    docker-compose up -d --build
    ```

4.  **Configure AI Connection**:
    - Open the dashboard at `http://localhost:52944`.
    - Go to the **AI Brain** section.
    - Set the **Ollama Base URL** to your Tailscale IP (e.g., `http://100.x.y.z:11434`).
    - Ensure the model (default: `deepseek-coder`) is pulled on that remote server.

## Usage

### Dashboard
Visit `http://localhost:52944` to view system status and configure settings.

### Chat API
Send a POST request to the chat endpoint:

**Endpoint**: `http://localhost:52944/chat`
**Method**: `POST`
**Body**:
```json
{
  "message": "What is my current runway?"
}
```

## Architecture

- **timescaledb**: PostgreSQL with Timescale extension for time-series data.
- **gordon-worker**: .NET 8 Service handling ingestion, API, and UI.
    - Connects to external Ollama via HTTP.
    - Persists settings to `app_data/settings.json`.

## Notes

- The system uses "Safe Upsert" to avoid duplicate transactions.
- All logs and outputs are in **English UK**.