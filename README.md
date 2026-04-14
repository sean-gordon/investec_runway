# Gordon Finance Engine 🦓

**Version 2.8.5** | *Financial insights that actually make sense.*

---

I built Gordon because most finance apps just tell you what you have already spent. I wanted something that could tell me what is coming next - my actual burn rate, how many days of "runway" I have left before my next paycheck, and whether a big purchase today is going to bite me in two weeks.

Gordon connects to Investec Programmable Banking, stores everything in a private database, and uses AI (Ollama, Gemini, or Claude) so you can ask questions about your money in plain English.

---

## AI Disclaimer

AI was used in the following for this project:
- Creation of theming
- Update of email wording and email template
- Readme File
- Temporary Pricing Page and Side Hustle html pages

---

## What Gordon Does

### Financial Foresight
*   **Runway Projections:** Tells you exactly how many days your current balance will last based on how you have been spending lately.
*   **Burn Rate Analysis:** It separates your fixed bills from your "fun money" so you know your true cost of living.
*   **What-If Simulations:** Want to know if you can afford a new car? Plug it in and see how it shifts your payday balance.
*   **Real-time Webhooks:** Receives push notifications from Investec for card transactions the moment they happen.
*   **Auto-Categorisation:** Uses AI to sort your groceries from your gear without you having to lift a finger.
*   **Smart Transfers:** If your runway gets too short, Gordon can automatically move money from your savings to your spending account (with a dry-run mode so you stay in control).

### Communications & Reporting
*   **WhatsApp Chat:** Connect Gordon to WhatsApp via Twilio to check your balance or ask questions while you are on the move.
*   **Telegram Bot:** Get instant pings when you spend too much or when your salary hits your account.
*   **Daily Briefings:** Start your morning with a snapshot of your financial health sent straight to your preferred chat app.
*   **Weekly Performance:** Receive a detailed summary of your spending trends and budget adherence every weekend.

### Brains & Reliability
*   **Multi-Model Support:** Use local AI (Ollama) for privacy or cloud models (Gemini/Claude) for speed.
*   **Built-in Fallback:** If your primary AI is having a bad day, Gordon automatically tries a backup so you are never left hanging.
*   **Chat with your Data:** Ask "What did I spend on coffee last month?" or "Can I afford a PS5?" and get a real answer based on your actual history.

### Security
*   **Self-Hosted:** Everything runs on your own hardware. Your data does not live in my cloud or anyone else's.
*   **Encrypted:** Your API keys and banking secrets are encrypted with AES-256 before they even hit the database.
*   **Private:** Your banking credentials stay inside your network. Period.

---

## How it is Built

Gordon is not a bloated monolith. It is a lean set of services managed by Docker:

1.  **Gordon Worker (.NET 8):** The engine. It handles the banking API, runs the AI logic, processes background reports, and serves the dashboard.
2.  **TimescaleDB:** A high-performance database built for time-series data (perfect for transaction history).
3.  **Frontend (Vue.js 3):** A fast dashboard that works on desktop and mobile. The repository also includes standalone pages like pricing.html and side-hustle.html.

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

Once you are in, head to the **Settings** tab:

*   **Bank Connection:** Plug in your Investec details.
*   **Webhooks:** Point your Investec Programmable Banking webhook URL to `https://<your-host>/api/webhook/investec` for real-time updates.
*   **AI Setup:** Pick your brain (Ollama/Gemini/Claude).
*   **Messaging:** Configure your Telegram bot token or Twilio WhatsApp credentials to enable remote chat and notifications.
*   **Actuarial Logic:** Tell Gordon when you get paid and what your "fixed" costs are to sharpen his projections.

---

## Help & Troubleshooting

| Command | What it does |
|---------|-------------|
| `docker compose up -d` | Start everything |
| `docker compose logs -f` | See what is happening under the hood |
| `docker compose down` | Stop the engine |

**AI is not talking back?** Check the Health indicators on the dashboard. If you are running Ollama, make sure the `OLLAMA_HOST` is reachable from inside the container.

## Screenshots

<img width="1920" height="951" alt="image" src="https://github.com/user-attachments/assets/58d4b632-5d82-48d7-acce-206408d8d144" />

<img width="1920" height="2572" alt="image" src="https://github.com/user-attachments/assets/5a2ae002-b564-466d-bd21-4d1b499089c9" />

<img width="1920" height="1281" alt="image" src="https://github.com/user-attachments/assets/430e0cea-e6d0-4ab8-8f7b-9a77413b9a66" />

<img width="1920" height="1679" alt="image" src="https://github.com/user-attachments/assets/9b868df8-e8b9-4610-8133-d0ec24af27ac" />

<img width="1920" height="1846" alt="image" src="https://github.com/user-attachments/assets/c4187050-c2e5-4094-84fe-e882b649e29d" />

<img width="777" height="334" alt="image" src="https://github.com/user-attachments/assets/c848a8ad-0119-4b5f-90c8-6d607e7104ac" />

---

## API Side Hustle Submission

This project is submitted for the **Investec API Side Hustle 2026**. Here is how it meets the criteria:

*   **[✅] Side Hustle Theme:** I built Gordon as a functional SaaS product. It is not just a demo - it is a structured engine that could be hosted today as a paid service for users who want better control over their cash flow.
*   **[✅] Meaningful API Usage:** Gordon uses two core Investec features. It listens for card transactions via webhooks to update projections instantly. It also uses the `/transfermultiple` endpoint to move money between accounts when the "Runway" logic detects a pending shortfall.
*   **[✅] Solving a Real Problem:** Most banking apps are retrospective. Gordon is predictive. It takes your current balance, subtracts your upcoming fixed costs, and tells you exactly how many days of "runway" you have left. It solves the "can I afford this" question by looking at your actual bills.
*   **[✅] Target Audience:** This is for professionals or families who manage their money across multiple accounts and need a consolidated, forward-looking view that accounting software usually misses.
*   **[✅] Monetisation:** The code includes a subscription-ready architecture. While the engine is open source, a hosted version would charge for access to the automated top-up features, priority AI processing, and real-time alerts.
*   **[✅] Submission Items:** The repository contains the full .NET backend, a functional web dashboard, documentation for the programmable features, and a clear setup guide.

---

## License

MIT License. Do what you want with it, just don't blame me if you spend all your money on Lego.
