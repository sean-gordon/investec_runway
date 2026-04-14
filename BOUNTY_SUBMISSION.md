# Investec API Side Hustle bounty submission 🦓

Use this description when submitting the project for the bounty.

---

## Project Name: Gordon Finance Engine

**What is Gordon?**
Gordon is an open-source financial dashboard built for Investec Programmable Banking. Most apps just show a list of what you have already spent. Gordon looks forward. It uses AI (Gemini, Claude, or Ollama) and statistical modelling to calculate your "Financial Runway" - telling you exactly how many days your current balance will last before your next paycheck hits.

---

## Why Gordon qualifies for the Side Hustle bounty

Gordon meets both the **Active Programmable Banking** and **Monetisation** criteria:

### 1. Active Programmable Banking (Auto-Top Ups)
Gordon does not just read your data; it acts on it. Using the `/transfermultiple` API, Gordon can automatically move money from your savings to your spending account if your projected runway drops below a safe limit (like 5 days). 

It includes a full **Dry-Run Mode** and a **Sandbox/Live** toggle, so you can test the automation logic safely before moving real money.

### 2. Monetisation strategy (SaaS)
Gordon is designed as the engine for a premium financial advisory service. The backend already includes hooks for `IsPremiumUser` and `StripeSubscriptionId`. 

While the engine is open-source for developers, I have planned a hosted tier for non-technical users. Subscribers get access to the auto-top-up automations, priority AI processing, and real-time spending alerts via Telegram and WhatsApp.

---

**GitHub Repo:** [Insert URL]
