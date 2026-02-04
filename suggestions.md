# Future Improvements: Gordon Finance Engine

To evolve the Gordon Finance Engine into a world-class actuarial assistant, the following enhancements are suggested:

### 1. Advanced Machine Learning Categorisation
Utilise the AI Brain (Ollama/Gemini) to perform semantic categorisation of messy bank descriptions.
*   **Goal:** Group transactions like "CHECKERS RIVONIA" and "CHECKERS CPT" into a single "Groceries" bucket with 100% accuracy, bypassing unreliable Regex.

### 2. Multi-Cycle Seasonality Analysis
Incorporate Year-over-Year (YoY) comparisons to account for major annual spikes (e.g., December holidays or annual tax events).
*   **Goal:** Proactive alerts for upcoming "high-spend" periods based on multi-year historical patterns.

### 3. "Fat Tail" & Black Swan Risk Modeling
Move beyond simple variance for risk. Financial shocks often come from rare, large-magnitude events.
*   **Goal:** Use Student's t-distributions in Monte Carlo simulations to better account for the probability of catastrophic balance-depleting events.

### 4. Interactive Chat Visualisation
Allow the Chat API to return data that the frontend can render as charts.
*   **Goal:** Ask "Show me my grocery spend for the last 6 months" and have Gordon respond with a bar chart in the chat interface.

### 5. Multi-Currency Support
Handle accounts from different regions with automatic exchange rate conversion.
*   **Goal:** A unified dashboard that shows your total net worth in your primary currency (ZAR) regardless of where the accounts are based.

### Completed Enhancements:
- [x] **Salary-Centric Lifecycle**: Projections now align with payday rather than the 1st of the month.
- [x] **Upcoming Fixed Cost Reservation**: Automatically reserves funds for large recurring bills that haven't hit yet.
- [x] **Intelligent Stability Logic**: Fixed costs are identified and ignored for cut-back suggestions.
- [x] **Cloud AI Integration**: Support for Google Gemini as a secondary brain.
- [x] **Deterministic Fingerprinting**: Resolved data duplication issues using content-based IDs.
- [x] **Encrypted Persistent Settings**: All configuration is now stored securely in the database and remembered across devices.
- [x] **Linux Docker Compatibility**: Hardened orchestration for running on Oracle Cloud / Linux instances.
- [x] **Dynamic Model Discovery**: Real-time model listing for both Ollama and Gemini.
