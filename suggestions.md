# Future Roadmap: Making Gordon Smarter

Gordon has come a long way, but there's still plenty to build. Here is what's on the horizon:

---

## 🔮 What's Coming Next

### 1. Better AI Categorisation
Right now, Gordon uses basic rules to sort your spending. I want to move this entirely to the AI Brain.
*   **Goal:** Group transactions like "CHECKERS RIVONIA" and "CHECKERS CPT" into "Groceries" automatically with 100% accuracy.
*   **Priority:** High

### 2. Seasonality & Holiday Planning
Most of us spend more in December or when annual insurance premiums hit. 
*   **Goal:** Gordon should look at your history from *last* year to warn you about upcoming annual spikes before they happen.
*   **Priority:** Medium

### 3. "Black Swan" Risk Modeling
Standard math doesn't account for life's weird curveballs.
*   **Goal:** Use more advanced statistical distributions (Student's t) to predict the likelihood of your balance actually hitting zero if a major unexpected expense hits.
*   **Priority:** Medium

### 4. Interactive Charts in Chat
Charts are currently static images. I want them to be interactive.
*   **Goal:** Ask "Show me my grocery spend" and get a bar chart you can actually hover over and explore.
*   **Priority:** High

### 5. Multi-Currency Support
If you have accounts in different countries, Gordon should handle them.
*   **Goal:** A single dashboard that converts everything to your home currency (ZAR) using live exchange rates.
*   **Priority:** Low

### 6. Transaction Audit Trail
Who changed what?
*   **Goal:** A simple log showing when a category was manually overridden or when a top-up was triggered.
*   **Priority:** Medium

---

## ✅ Recent Wins

### Reliability (v2.1.0)
- [x] **Dynamic AI Discovery**: Gordon now asks the AI what it can do before sending a request.
- [x] **Auto-Fallback**: If your primary brain is down, Gordon automatically tries a backup.
- [x] **Telegram Queue**: No more lost messages. Everything is queued and processed reliably.
- [x] **Health Checks**: Live status indicators for the Bank, Database, and AI.

### Security (v2.0.0)
- [x] **Hardened Secrets**: 32-character minimum for JWT keys.
- [x] **Domain Locking**: Prevent unauthorized domains from accessing your dashboard.
- [x] **Encryption**: All your API keys are encrypted at rest with AES-256.

---

## 🎯 The Timeline

### Q2 2026 (Focus: Accuracy)
- 🔮 Smarter AI Categorisation
- 🔮 Basic Transaction Audit Trail
- 🔮 Better Error Handling for Bank Outages

### Q3 2026 (Focus: Visibility)
- 🔮 Interactive Web Charts
- 🔮 Admin Health Dashboard
- 🔮 Annual Spending Comparisons

---

**Last Updated:** 2026-04-07

*Got an idea? Open an issue on GitHub or ping me on Telegram.*
