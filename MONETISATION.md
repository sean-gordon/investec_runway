# Making Money with Gordon

While Gordon is open source, I've built him with a clear path to becoming a profitable side hustle. This matches the Investec API Bounty requirements for "monetisation potential."

## The Plan: A Hosted SaaS

Gordon provides high-level actuarial insights that are usually locked behind expensive financial advisors. Most people won't want to mess with Docker or a Linux server, so the most obvious way to make money is a hosted version (Software as a Service).

### Premium Features

The codebase already has hooks for a "Premium" tier built into the `AppSettings`:
1. **`IsPremiumUser`**: A toggle in the database that identifies paid subscribers.
2. **`StripeSubscriptionId`**: A field to link the account to a real payment.

### What Premium Users Get:
- **Auto-Top Ups**: The engine can automatically move money from savings to spending if your runway gets too short. This is "Active Programmable Banking" in action.
- **Better Brains**: Access to faster, smarter cloud AI models (like Claude 3.5 Sonnet or GPT-4o) without having to bring your own API key.
- **Priority Bot**: Their Telegram messages hit the front of the queue, giving them instant answers.

### Implementation Blueprint

The engine is already "subscription-ready." Here is how we'd go live:

1. Host the dashboard on a public URL with a "Pricing" page.
2. When someone pays via Stripe, a webhook updates their user config:
   ```json
   {
       "IsPremiumUser": true,
       "StripeSubscriptionId": "sub_123456789"
   }
   ```
3. Gordon's `SettingsService` picks this up immediately, unlocking the premium features in the dashboard.

### Other Ideas
- **Referrals**: If Gordon sees you're paying a high interest rate on a loan, he could suggest a better bank or credit card using an affiliate link.
- **Advisor Tools**: Financial advisors could use a "White Label" version of Gordon to manage their clients' portfolios and automate their reporting.
