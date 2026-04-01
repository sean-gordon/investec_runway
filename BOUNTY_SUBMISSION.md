# Investec API Side Hustle Bounty Submission

*Here is the message and description you can copy and paste to submit your project for the bounty:*

---

## 🚀 Project Name: Gordon Finance Engine (Actuarial Runway)

**Description:**
Gordon is an open-source, actuarial-grade financial engine that connects to Investec Programmable Banking. Unlike simple ledger apps that look backward, Gordon uses statistical modeling, AI categorization (Gemini/Ollama), and Monte Carlo simulations (Student's t-distributions) to project your "Financial Runway" and survival probability until payday. 

**How it qualifies for the "Side Hustle API Bounty":**

Our integration fulfills both the **Active Programmable Banking** and **Monetisation** criteria required for the bounty:

1. **Active Programmable Banking (Automated Runway Top-Ups)**
   Gordon goes beyond read-only analytics. It actively prevents overdrafts and manages cash flow via the `/za/pb/v1/accounts/transfermultiple` API. A background orchestrator continuously checks the actuarial projection. If a user's runway drops below their configured threshold (e.g., 5 days), Gordon autonomously executes a transfer from their Savings to their Spending account. It features a complete `Sandbox`/`Live` environment toggle and a `Dry-Run Mode` to safely test the logic without moving real money. 

2. **Monetisation Strategy (SaaS Hosted Platform)**
   Gordon forms the backbone of a premium financial advisory SaaS. The engine configuration natively supports `IsPremiumUser` and `StripeSubscriptionId` flags. While the core engine is open-source for developers, non-technical users can subscribe to a hosted tier. The premium subscription unlocks the Active Programmable Banking automations (Auto Top-Ups), priority Telegram bot processing queues, and real-time AI spending queries.

**GitHub Repository:** [Insert your GitHub URL here, e.g., https://github.com/sean-gordon/investec_runway]
**Documentation:** 
- [Monetisation Strategy (MONETISATION.md)](https://github.com/sean-gordon/investec_runway/blob/main/MONETISATION.md)
- [Programmable Capabilities (PROGRAMMABLE.md)](https://github.com/sean-gordon/investec_runway/blob/main/PROGRAMMABLE.md)

---
*Feel free to adjust the GitHub links before submitting!*
