# Monetising Gordon Finance Engine

The Gordon Finance Engine is primarily open source, but it has been specifically adapted to support a "Side Hustle" monetisation strategy in compliance with the Investec API Bounty requirements.

## Our Strategy: Hosted Premium (SaaS) Pattern

Gordon provides "Actuarial-grade" financial insights. The easiest way to monetise this is to offer a hosted version of the application for users who lack the technical expertise or infrastructure to self-host the Docker containers.

### Premium Features

We have built specific feature flags directly into the engine's core configuration model (`AppSettings`):
1. **`IsPremiumUser`**: A boolean flag stored securely in TimescaleDB settings.
2. **`StripeSubscriptionId`**: A reference to the external billing system.

When a user is flagged as Premium, they unlock:
- **Active Programmable Banking**: Automated Runway Top-Ups (moving money between Savings and Spending accounts when the runway drops below a critical threshold).
- **Advanced AI Analysis**: Real-time spending analysis utilizing more advanced AI models or more frequent API syncs without getting rate-limited.
- **Priority Telegram Support**: Guaranteed fastest processing queue for chat reports.

### Implementation Blueprint

The current codebase is ready for integration with a payment provider (like Stripe or Paddle). To complete the loop:

1. Deploy the Gordon UI on a public domain with a landing page advertising the "Premium Actuarial Analysis" features.
2. Use the `/api/users/register` endpoint to create accounts.
3. Upon checkout, issue a webhook to update the user's `config` JSONB column in TimescaleDB:
   ```json
   {
       "IsPremiumUser": true,
       "StripeSubscriptionId": "sub_123456789"
   }
   ```
4. Gordon's built-in `SettingsService` automatically maps these to the `AppSettings` class, seamlessly unlocking the premium dashboard panels.

### Future Expansion
- **Affiliate Links**: The dashboard can suggest high-yield savings accounts or credit cards when the user's spending habits match a specific demographic, generating referral revenue.
- **White-labeling**: Offering Gordon to independent financial advisors as a tool they use to manage their clients (B2B SaaS model).
