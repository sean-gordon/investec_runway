# Future Improvements: Gordon Finance Engine

To evolve the Gordon Finance Engine into a world-class actuarial assistant, the following enhancements are suggested:

---

## 🔮 Planned Features

### 1. Advanced Machine Learning Categorisation
Utilise the AI Brain (Ollama/Gemini) to perform semantic categorisation of messy bank descriptions.
*   **Goal:** Group transactions like "CHECKERS RIVONIA" and "CHECKERS CPT" into a single "Groceries" bucket with 100% accuracy, bypassing unreliable Regex.
*   **Status:** Planned
*   **Priority:** High
*   **Effort:** Medium (2-3 days)

### 2. Multi-Cycle Seasonality Analysis
Incorporate Year-over-Year (YoY) comparisons to account for major annual spikes (e.g., December holidays or annual tax events).
*   **Goal:** Proactive alerts for upcoming "high-spend" periods based on multi-year historical patterns.
*   **Status:** Planned
*   **Priority:** Medium
*   **Effort:** High (1 week)

### 3. "Fat Tail" & Black Swan Risk Modeling
Move beyond simple variance for risk. Financial shocks often come from rare, large-magnitude events.
*   **Goal:** Use Student's t-distributions in Monte Carlo simulations to better account for the probability of catastrophic balance-depleting events.
*   **Status:** Planned
*   **Priority:** Medium
*   **Effort:** High (1 week)

### 4. Interactive Chat Visualisation
Allow the Chat API to return data that the frontend can render as charts.
*   **Goal:** Ask "Show me my grocery spend for the last 6 months" and have Gordon respond with a bar chart in the chat interface.
*   **Status:** Partially Complete (Telegram supports charts)
*   **Priority:** High
*   **Effort:** Medium (3-4 days for web UI)

### 5. Multi-Currency Support
Handle accounts from different regions with automatic exchange rate conversion.
*   **Goal:** A unified dashboard that shows your total net worth in your primary currency (ZAR) regardless of where the accounts are based.
*   **Status:** Planned
*   **Priority:** Low
*   **Effort:** High (1-2 weeks)

### 6. Transaction Audit Trail
Add comprehensive audit logging for all transaction modifications.
*   **Goal:** Track who changed what and when, enable compliance and debugging
*   **Status:** Planned
*   **Priority:** Medium
*   **Effort:** Medium (2-3 days)
*   **Added:** v2.0.0 release notes

### 7. Prometheus Metrics Integration
Export application metrics in Prometheus format for advanced monitoring.
*   **Goal:** Integrate with Grafana for real-time dashboards
*   **Status:** Planned
*   **Priority:** Medium
*   **Effort:** Medium (3-4 days)
*   **Added:** v2.0.0 release

### 8. Circuit Breaker for Investec API
Implement circuit breaker pattern to handle Investec API outages gracefully.
*   **Goal:** Prevent cascade failures, automatically retry after cooldown
*   **Status:** Planned
*   **Priority:** High
*   **Effort:** Low (1-2 days)
*   **Added:** v2.0.0 release

### 9. Distributed Tracing (OpenTelemetry)
Add distributed tracing for request flows across services.
*   **Goal:** Better debugging and performance analysis
*   **Status:** Planned
*   **Priority:** Low
*   **Effort:** High (1 week)
*   **Added:** v2.0.0 release

### 10. Admin Dashboard
Create administrative dashboard for system health monitoring.
*   **Goal:** Visualize health checks, user activity, AI performance
*   **Status:** Planned
*   **Priority:** Medium
*   **Effort:** High (1-2 weeks)
*   **Added:** v2.0.0 release

---

## ✅ Completed Enhancements

### Infrastructure & Reliability (v2.1.0 - 2026-02-17)
- [x] **Truly Dynamic AI Discovery**: Real-time capability checking for Gemini models (`generateContent`)
- [x] **AI Fallback System**: Primary + secondary AI providers with automatic retry and exponential backoff
- [x] **Telegram Background Queue**: Channel-based reliable message processing with guaranteed responses
- [x] **Health Check Endpoints**: `/health`, `/health/ready`, `/health/live` for monitoring
- [x] **API Rate Limiting**: 100 req/min per user/IP to prevent abuse
- [x] **Settings Cache**: IMemoryCache with 5-minute expiration (replaced Dictionary)
- [x] **Rate-Limited Sync**: Max 5 concurrent transaction syncs to prevent API throttling

### Security (v2.0.0 - 2026-02-16)
- [x] **JWT Validation**: Startup validation with 32-character minimum requirement
- [x] **Configurable Domains**: Move hardcoded domains to `Security:AllowedDomains` configuration
- [x] **HTTPS Enforcement**: RequireHttpsMetadata=true in production environments

### Core Features (v1.x)
- [x] **Salary-Centric Lifecycle**: Projections now align with payday rather than the 1st of the month
- [x] **Upcoming Fixed Cost Reservation**: Automatically reserves funds for large recurring bills that haven't hit yet
- [x] **Intelligent Stability Logic**: Fixed costs are identified and ignored for cut-back suggestions
- [x] **Cloud AI Integration**: Support for Google Gemini as a secondary brain
- [x] **Deterministic Fingerprinting**: Resolved data duplication issues using content-based IDs
- [x] **Encrypted Persistent Settings**: All configuration is now stored securely in the database and remembered across devices
- [x] **Linux Docker Compatibility**: Hardened orchestration for running on Oracle Cloud / Linux instances
- [x] **Dynamic Model Discovery**: Real-time model listing for both Ollama and Gemini

---

## 📊 Feature Priority Matrix

| Feature | Priority | Effort | Impact | Status |
|---------|----------|--------|--------|--------|
| AI Fallback System | Critical | Medium | Very High | ✅ Complete |
| Telegram Reliability | Critical | High | Very High | ✅ Complete |
| Health Checks | High | Medium | High | ✅ Complete |
| JWT Security | Critical | Low | Very High | ✅ Complete |
| Rate Limiting | High | Medium | High | ✅ Complete |
| ML Categorisation | High | Medium | High | 🔮 Planned |
| Circuit Breaker | High | Low | Medium | 🔮 Planned |
| Interactive Charts (Web) | High | Medium | Medium | 🔮 Planned |
| Audit Trail | Medium | Medium | Medium | 🔮 Planned |
| Admin Dashboard | Medium | High | Medium | 🔮 Planned |
| Prometheus Metrics | Medium | Medium | Medium | 🔮 Planned |
| Seasonality Analysis | Medium | High | Low | 🔮 Planned |
| Black Swan Modeling | Medium | High | Low | 🔮 Planned |
| OpenTelemetry | Low | High | Low | 🔮 Planned |
| Multi-Currency | Low | High | Low | 🔮 Planned |

---

## 🎯 Roadmap

### Q1 2026 ✅ **COMPLETED**
- ✅ AI Fallback System
- ✅ Telegram Reliability
- ✅ Security Hardening
- ✅ Performance Optimization
- ✅ Health Checks & Monitoring

### Q2 2026 (Planned)
- 🔮 Advanced ML Categorisation
- 🔮 Circuit Breaker Implementation
- 🔮 Interactive Web Charts
- 🔮 Transaction Audit Trail

### Q3 2026 (Planned)
- 🔮 Admin Dashboard
- 🔮 Prometheus Metrics
- 🔮 Seasonality Analysis

### Q4 2026 (Future)
- 🔮 Black Swan Risk Modeling
- 🔮 OpenTelemetry Integration
- 🔮 Multi-Currency Support

---

## 💡 Community Suggestions

**Want to suggest a feature?** Open an issue on GitHub with:
1. Feature description
2. Use case / problem it solves
3. Proposed implementation approach
4. Expected impact (High/Medium/Low)

---

## 🛠️ Implementation Notes

### For ML Categorisation:
- Consider fine-tuning a small LLM on South African merchant names
- Create a feedback loop where users can correct categorisations
- Store learned categorisations in database for reuse

### For Interactive Charts:
- Extend existing `ChartService` to support web-based rendering
- Consider Chart.js or ApexCharts for frontend
- Add chart type selection to chat interface

### For Audit Trail:
- Create `transaction_audit_log` table
- Trigger on UPDATE to transactions table
- Include: user_id, change_type, old_value, new_value, timestamp

### For Prometheus:
- Add `prometheus-net` NuGet package
- Expose `/metrics` endpoint
- Track: request counts, AI latency, sync duration, error rates

---

**Last Updated:** 2026-02-17 (v2.1.0 Release)

See `CHANGELOG.md` for detailed version history.
