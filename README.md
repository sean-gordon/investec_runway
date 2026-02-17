# Gordon Finance Engine 🦓

**Version 2.2.3** | Your Personal, AI-Powered Chief Financial Officer

Gordon is a self-hosted financial assistant designed to give you professional-grade insights into your money. He doesn't just track your spending; he understands it. By connecting directly to your bank and using advanced AI, Gordon helps you see where your money is going and, more importantly, where it's taking you.

He connects to your Investec account, remembers your history in a secure database, and is always ready to chat about your finances via a web dashboard or Telegram.

---

## 🌟 What Gordon Does for You

### The Essentials
*   **Automatic Sync:** Gordon keeps a watchful eye on your Investec account, pulling in new transactions so you don't have to. He's smart enough to never double-count a single cent.
*   **Smart Spending Insights:** He calculates your "Burn Rate"—how fast you're spending—and tells you exactly how many days of "runway" you have left before your next payday.
*   **A Brain That Never Sleeps:** Chat with your data using local AI (Ollama) or Google Gemini. If one service is having a bad day, Gordon automatically switches to a backup so you're never left in the dark.
*   **Your Data, Your Rules:** Gordon is self-hosted. Your banking details and spending habits stay on your own hardware, not in someone else's cloud. Everything sensitive is locked away with enterprise-grade encryption.
*   **Weekly Briefings:** Every week, Gordon sends you a friendly email summary of your wins, your upcoming bills, and a health check on your savings.

### Fresh in Version 2.x 🆕
*   **🤖 Double-Layer AI:** Gordon now has a "backup brain." If his primary AI fails, he'll try again and then flip to a secondary provider automatically.
*   **✨ Dynamic AI Discovery:** Gordon now automatically finds the best AI models for your needs by checking their capabilities in real-time.
*   **📱 Reliable Telegram Chat:** We've rebuilt the Telegram connection from the ground up. Messages are now queued up, meaning Gordon will never "forget" to reply to you, even if things get busy.
*   **❤️ Live Health Checks:** You can now see at a glance if Gordon's connection to your bank or his AI "brain" is healthy.
*   **🛡️ Built-in Protection:** We've added layers of security and "rate limiting" to keep your engine running smoothly and safely.

---

## 🏗️ How It's Built

Gordon is powered by modern, reliable tech that's easy to run:

*   **The Engine:** A robust .NET 8 (C#) service that handles all the heavy lifting in the background.
*   **The Memory:** TimescaleDB (PostgreSQL), specifically designed to handle long histories of transactions efficiently.
*   **The Face:** A clean, fast dashboard built with Vue.js 3 and Tailwind CSS.
*   **The Brains:** Flexible AI support (Ollama/Gemini) with built-in "failover" reliability.
*   **The Setup:** Everything is packaged into Docker, making it a breeze to get started on Windows, Linux, or a Mac.

---

## 🚀 Getting Started

### What You'll Need

1.  **Docker:** Installed and running on your computer or server.
2.  **Investec API Access:** You'll need your Client ID and Secret from the Investec Programmable Banking portal.
3.  **An AI Provider:** Either Ollama running locally or a Google Gemini API key. We recommend having both for maximum reliability!

### Installation

1.  **Grab the Code**
    ```bash
    git clone https://github.com/sean-gordon/investec_runway.git
    cd investec_runway
    ```

2.  **Set Up Your Environment**
    Gordon needs a few basic settings to start. Copy the template and fill in a database password:

    **Windows (PowerShell):**
    ```powershell
    Copy-Item .env.template .env
    ```
    **Linux / Mac:**
    ```bash
    cp .env.template .env
    ```

3.  **Secure Your App (Don't skip this!)**
    Generate a unique secret key for your login tokens:
    ```bash
    openssl rand -base64 64
    ```
    Paste that key into `GordonWorker/appsettings.json` under the `Jwt:Secret` section.

4.  **Start the Engine**
    ```bash
    docker compose up -d --build
    ```

5.  **Say Hello to Gordon**
    Open your browser to: [http://localhost:52944](http://localhost:52944)

---

## ⚙️ Making Gordon Yours

Once you're logged in, head to the **Configuration** tab. This is where you give Gordon his credentials. **Don't worry—everything you enter here is encrypted before it ever touches the database.**

*   **Connections:** Put in your Investec details here to start the sync.
*   **The Brain:** Set up your primary AI (like a local Ollama instance) and your fallback (like Gemini).
*   **Math & Logic:** Fine-tune how Gordon thinks. You can tell him which keywords mean "Salary" or "Fixed Bills" so his math is spot-on.
*   **Telegram:** Link your Telegram bot so you can check your balance while you're out and about.

---

## 🛠️ When Things Go Wrong

### Gordon isn't replying to my chats
- Check if your AI service (Ollama or Gemini) is reachable.
- If using Ollama on Windows, make sure it's allowed to talk to Docker (set `OLLAMA_HOST` to `0.0.0.0`).
- Look at the "Health" indicators on the dashboard.

### I'm seeing "Too Many Requests" (Error 429)
- Gordon limits how fast you can talk to him to keep things stable. Wait 60 seconds and try again.

### Telegram is quiet
- Make sure your Bot Token is correct and that you've authorized your specific Chat ID in the settings.

---

## 🔐 Keeping You Safe

Security isn't an afterthought for Gordon; it's his foundation:
- **Private by Default:** Everything runs on your hardware. Your data never leaves your system unless it's to talk to the bank or an AI you've chosen.
- **Top-Tier Encryption:** We use AES-256 encryption (the industry standard) for your API keys and secrets.
- **Secure Login:** Your dashboard is protected by JWT tokens and strict domain controls.

---

## 🐳 Useful Commands

| Action | Command |
|----------|-------------|
| **Start** | `docker compose up -d` |
| **Stop** | `docker compose down` |
| **Update** | `git pull` then `docker compose up -d --build` |
| **See Logs** | `docker compose logs -f gordon-worker` |

---

## 🤝 Join the Project

Want to make Gordon even smarter? We'd love your help!
1. Fork the repo
2. Create your feature branch
3. Send us a pull request

---

## 🙏 Credits

Built with ❤️ using .NET 8, TimescaleDB, and a dash of AI magic. Special thanks to Claude Sonnet 4.5 for the security and reliability polish.

**Gordon Finance Engine** - Because your money deserves an actuary. 🦓
