# Active Programmable Banking with Gordon

The Gordon Finance Engine does not just track expenses; it actively helps you survive until payday by utilizing the Investec Programmable Banking transfer capabilities.

## Automated Runway Top-Ups

By enabling the "Auto Top-Up" feature in the Connections dashboard, Gordon will actively monitor your runway (via `ActuarialService`) and initiate inter-account transfers to prevent overdrafts.

### How It Works
1. **The Actuarial Assessment**: Every day, the `RunwayTopUpWorker` recalculates your expected runway based on current account balance, history, and fixed costs.
2. **Threshold Monitoring**: If your runway drops below a configurable threshold (e.g., 5 days), a top-up sequence is triggered.
3. **The Transfer**: Gordon uses the `/za/pb/v1/accounts/transfermultiple` Investec API endpoint to move your configured "Top-Up Amount" from your Savings to your Spending account safely.
4. **Alerts**: You are immediately notified via Telegram with a full breakdown of the event.

### Safety Features
- **Dry-Run Mode**: You can test the entire pipeline without actually moving money by toggling the "Dry Run" switch in the dashboard.
- **Environment Targeting**: A dropdown setting dictates whether Gordon points to the Sandbox API (`https://openapi.sandbox.investec.com`) or the Live API.
- **Background Orchestration**: Top-ups run as a safe decoupled background process (`RunwayTopUpWorker`), ensuring that UI interactions are lightning-fast and API rate limits are respected.

---
*“Gordon gives you the data, Investec provides the power.”*
