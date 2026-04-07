# Smart Automation with Gordon

Gordon doesn't just watch your bank account; he actively manages it. Using Investec's Programmable Banking API, Gordon can move money for you so you never run out of cash before payday.

## Auto-Top Ups

If you turn on "Auto Top-Up" in the dashboard, Gordon will keep an eye on your runway. If he sees you're about to hit zero, he'll automatically move money from your savings to your spending account.

### How it works
1. **The Daily Check**: Every day, a background worker looks at your balance, your upcoming bills, and your recent spending to figure out your "Runway" (how many days of cash you have left).
2. **The Red Line**: If that runway drops below your limit (e.g., 5 days), Gordon starts a top-up.
3. **The Transfer**: Gordon calls the Investec API to move your chosen top-up amount from your Savings account into your Spending account.
4. **The Ping**: You'll get an instant message on Telegram letting you know exactly what happened and why.

### Keeping it safe
- **Dry-Run Mode**: You can leave this on to see Gordon's "decisions" without actually moving any real money. Perfect for testing.
- **Sandbox Support**: You can point Gordon at Investec's Sandbox environment to play around with fake money and fake accounts.
- **Background Only**: Transfers happen in a dedicated background process, so the dashboard stays fast and we never spam the bank's API.

---
*"Gordon handles the math, Investec handles the money."*
