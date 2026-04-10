# Gordon Finance Engine 🦓

**Version 2.8.5** | *Financial insights that actually make sense.*

---

I built Gordon because most finance apps just tell you what you've already spent. I wanted something that could tell me what's coming next - my actual burn rate, how many days of "runway" I have left before my next paycheck, and whether a big purchase today is going to bite me in two weeks.

Gordon connects to **Investec Programmable Banking**, stores everything in a private database, and uses AI (Ollama, Gemini, or Claude) so you can just ask questions about your money in plain English.

---

## What Gordon Does

### Financial Foresight
*   **Runway Projections:** Tells you exactly how many days your current balance will last based on how you've been spending lately.
*   **Burn Rate Analysis:** It automatically separates your fixed bills from your "fun money" so you know your true cost of living.
*   **What-If Simulations:** Want to know if you can afford a new car? Plug it in and see how it shifts your payday balance.
*   **Auto-Categorisation:** Uses AI to sort your groceries from your gear without you having to lift a finger.
*   **Smart Transfers:** If your runway gets too short, Gordon can automatically move money from your savings to your spending account (with a dry-run mode so you stay in control).

### Brains & Reliability
*   **Multi-Model Support:** Use local AI (Ollama) for privacy or cloud models (Gemini/Claude) for speed.
*   **Built-in Fallback:** If your primary AI is having a bad day, Gordon automatically tries a backup so you're never left hanging.
*   **Chat with your Data:** Ask "What did I spend on coffee last month?" or "Can I afford a PS5?" and get a real answer based on your actual history.

### Security
*   **Self-Hosted:** Everything runs on your own hardware. Your data doesn't live in my cloud or anyone else's.
*   **Encrypted:** Your API keys and banking secrets are encrypted with AES-256 before they even hit the database.
*   **Private:** Your banking credentials stay inside your network. Period.

---

## How it's Built

Gordon isn't a bloated monolith. It's a lean set of services managed by Docker:

1.  **Gordon Worker (.NET 8):** The engine. It handles the banking API, runs the AI logic, and serves the dashboard.
2.  **TimescaleDB:** A high-performance database built for time-series data (perfect for transaction history).
3.  **Frontend (Vue.js 3):** A fast, "no-build" dashboard that works on desktop and mobile.

---

## Get it Running

### You'll Need
*   **Docker & Docker Compose**
*   **Investec API Credentials** (Client ID & Secret)
*   An AI provider (Local **Ollama**, **Gemini**, or **Claude**)

### Installation

1.  **Clone the repo**
    ```bash
    git clone https://github.com/sean-gordon/investec_runway.git
    cd investec_runway
    ```

2.  **Setup your environment**
    Copy the template to a new `.env` file:
    ```bash
    cp .env.template .env
    ```
    *Open `.env` and set a `DB_PASSWORD`.*

3.  **Configure Security**
    Edit `GordonWorker/appsettings.json` and set a long, random string for `Jwt:Secret`.

4.  **Fire it up**
    ```bash
    docker compose up -d --build
    ```

5.  **Log in**
    Go to [http://localhost:52944](http://localhost:52944) and start exploring.

---

## Tuning Gordon

Once you're in, head to the **Settings** tab:

*   **Bank Connection:** Plug in your Investec details.
*   **AI Setup:** Pick your brain (Ollama/Gemini/Claude). If using Claude Code, run `npx @anthropic-ai/claude-code setup-token` locally to get your access key.
*   **Telegram:** Connect a bot so Gordon can ping you when you spend too much or your salary hits.
*   **Actuarial Logic:** Tell Gordon when you get paid and what your "fixed" costs are to sharpen his projections.

---

## Help & Troubleshooting

| Command | What it does |
|---------|-------------|
| `docker compose up -d` | Start everything |
| `docker compose logs -f` | See what's happening under the hood |
| `docker compose down` | Stop the engine |

**AI isn't talking back?** Check the Health indicators on the dashboard. If you're running Ollama, make sure the `OLLAMA_HOST` is reachable from inside the container.

---

## License

MIT License. Do what you want with it, just don't blame me if you spend all your money on Lego.
