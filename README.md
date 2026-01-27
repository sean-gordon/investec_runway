# Gordon Finance Engine

A self-hosted, containerised C#.NET application that synchronises Investec Programmable Banking transactions into TimescaleDB and provides a natural language chat interface using Ollama.

## Features

- **Transaction Ingestion**: Automatically fetches transactions from Investec API every 60 seconds and stores them in a TimescaleDB hypertable.
- **Predictive Runway**: Calculates daily burn rate and estimated runway based on the last 30 days of activity.
- **AI Chat**: Natural language interface powered by local LLM (Ollama with deepseek-coder) to query financial data.

## Prerequisites

- Docker Desktop
- Investec Programmable Banking credentials (Client ID, Secret, API Key, Account ID)

## Setup

1.  **Clone the repository**.
2.  **Environment Variables**:
    Create a `.env` file in the root directory (or set these variables in your environment) with your Investec credentials:

    ```env
    INVESTEC_CLIENT_ID=your_client_id
    INVESTEC_SECRET=your_secret
    INVESTEC_API_KEY=your_api_key
    INVESTEC_ACCOUNT_ID=your_account_id
    ```

    *Note: Ensure `INVESTEC_SECRET` is kept secure.*

3.  **Run with Docker Compose**:

    ```bash
    docker-compose up -d --build
    ```

4.  **Initialize Ollama Model**:
    The system uses `deepseek-coder`. You need to pull this model in the Ollama container once it is running:

    ```bash
    docker exec -it investecrunwayapp-ollama-1 ollama pull deepseek-coder
    ```
    *(Note: Adjust the container name `investecrunwayapp-ollama-1` if different on your system, usually `folder_name-ollama-1`)*

## Usage

### Transaction Sync
The `GordonWorker` service will automatically start syncing transactions. Check the logs to see ingestion status:

```bash
docker logs -f investecrunwayapp-gordon-worker-1
```

### Chat Interface
Send a POST request to the chat endpoint:

**Endpoint**: `http://localhost:52944/chat`
**Method**: `POST`
**Body**:
```json
{
  "message": "What is my current runway?"
}
```

Or for general queries:
```json
{
  "message": "Show me the total spent on groceries last month"
}
```

## Architecture

- **timescaledb**: PostgreSQL with Timescale extension for time-series data.
- **ollama**: Hosting `deepseek-coder` for Text-to-SQL and natural language formatting.
- **gordon-worker**: .NET 8 BackgroundService for ingestion and API for chat.

## Notes

- The system adheres to "Safe Upsert" to avoid duplicate transactions.
- Runway calculation uses the formula: `Runway = Current Balance / (Sum(abs(amount_last_30_days)) / 30)`.
