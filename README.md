# Gordon Finance Engine

Gordon Finance Engine is a self-hosted, personal financial analytics platform. It is designed to provide professional-grade insights into your personal finances by connecting directly to your bank account and using advanced data analysis techniques.

The system connects to Investec Programmable Banking to retrieve your transaction history, stores it in a specialized time-series database, and uses a Large Language Model (LLM) to allow you to ask questions about your money in plain English.

## Key Features

*   **Automated Ingestion:** Automatically pulls your latest transactions from Investec.
*   **Actuarial Logic:** Calculates your "Burn Rate" and projected financial runway using weighted averages that adapt to your recent spending habits.
*   **AI Analyst:** Chat with your financial data using a local LLM (Ollama) or Google Gemini. Ask questions like "How much did I spend on coffee last month?" or "Will I make it to payday?".
*   **Weekly Reports:** specific email reports that summarize your weekly spending, upcoming bills, and financial health.
*   **Privacy First:** Self-hosted on your own hardware. Your banking data stays with you.

## Architecture

The system is built using the following technologies:

*   **Core Application:** .NET 8 (C#) Worker Service.
*   **Database:** TimescaleDB (PostgreSQL) for efficient storage of time-series transaction data.
*   **Frontend:** A lightweight, single-page web interface built with Vue.js and Tailwind CSS.
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
    sed -i "s/change_this_password/$(openssl rand -hex 12)/g" .env
    ```
    
    **Windows (PowerShell):**
    ```powershell
    Copy-Item .env.template .env
    (Get-Content .env) -replace 'change_this_password', 'MySecurePassword123!' | Set-Content .env
    ```

3.  **Update Credentials**
    Open the `.env` file and paste your Investec API keys.
    ```bash
    nano .env
    # Or use Notepad
    notepad .env
    ```

4.  **Start the System**
    Launch the application in detached mode.
    ```bash
    docker-compose up -d --build
    ```

5.  **Access the Dashboard**
    Open your browser to: [http://localhost:52944](http://localhost:52944)

## Security Notes

*   **Database Password:** The default configuration uses a placeholder password. You must change the `DB_PASSWORD` in your `.env` file before running the application.
*   **API Keys:** Your Investec API keys provide access to your banking data. Keep your `.env` file secure and never commit it to version control.
*   **Network Access:** By default, the application is exposed on port 52944. Ensure your firewall is configured to restrict access to this port if running on a public server.

## Configuration

Once the application is running, you can fine-tune its behavior via the "Configuration" tab in the web interface:

*   **Profile:** Set your name and preferred currency format.
*   **Connections:** Manage your Investec API settings and history depth.
*   **Brain:** Choose between local (Ollama) or cloud (Gemini) AI providers.
*   **Math:** Adjust the sensitivity of the financial models (e.g., how quickly the system reacts to new spending trends).
*   **Email:** Configure SMTP settings to receive weekly email reports.

## API Usage

Gordon exposes a REST API that you can use to integrate his financial intelligence into other local services (like Home Assistant, Node-RED, or custom scripts).

**Endpoint:** `POST /chat`
**Port:** `52944` (Default)

**Example Request:**
```bash
curl -X POST http://localhost:52944/chat \
  -H "Content-Type: application/json" \
  -d '{"Message": "How much did I spend on Uber last month?"}'
```

**Response:**
```json
{
    "response": "You spent R450.00 on Uber last month. This is R50 less than your average."
}
```

## License

This project is open-source. Please see the LICENSE file for details.
