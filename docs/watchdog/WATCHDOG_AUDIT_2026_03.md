# Watchdog Audit — March 2026

**Purpose**: Assess whether the watchdog does its job properly and identify gaps or improvements.

---

## 1. Executive Summary

The watchdog is a **standalone monitoring and alerting system** for the trading execution pipeline. It is **observational only** — it does not influence execution or halt trading. Overall it fulfills its design goals, but there are gaps and opportunities for improvement.

| Area | Status | Notes |
|------|--------|-------|
| Engine health | ✅ Working | ENGINE_TICK_CALLSITE liveness, stall detection, hysteresis |
| Stream states | ✅ Working | State machine, stuck detection, timetable merge |
| Risk gates | ✅ Working | Recovery, kill switch, timetable, slot validation |
| Unprotected positions | ✅ Working | 10s timeout, protective order tracking |
| Data health | ✅ Working | Per-instrument bar tracking, stall detection |
| Connection status | ✅ Working | Auto-clear on engine alive |
| Identity invariants | ✅ Working | Phase 3.1, expiry handling |
| Fill health | ✅ Working | fill_metrics, unmapped/null_td rates |
| Event feed | ✅ Working | Rate limiting, timetable trim (30 min) |
| Ingestion | ✅ Working | Single tail read, degradation mode, telemetry |

---

## 2. Architecture Overview

```
Robot (NinjaTrader) → robot_*.jsonl → EventFeedGenerator → frontend_feed.jsonl
                                                                    ↓
Aggregator ← EventProcessor ← WatchdogStateManager
     ↓
REST API / WebSocket → Frontend
```

- **EventFeedGenerator**: Reads raw `logs/robot/robot_*.jsonl`, filters live-critical events, rate-limits noisy types, writes to `frontend_feed.jsonl`
- **EventProcessor**: Processes events from feed, updates state manager
- **WatchdogStateManager**: In-memory state, computes derived fields (engine_alive, stuck_streams, risk_gates, etc.)
- **TimetablePoller**: Polls `timetable_current.json` every 60s for trading_date and enabled streams

---

## 3. What the Watchdog Does Well

### 3.1 Engine Liveness
- Uses **ENGINE_TICK_CALLSITE** (rate-limited to 5s in feed) as primary signal
- Fallback to **ENGINE_ALIVE** when ENGINE_TICK_CALLSITE not emitted
- Hysteresis (15s) to avoid flickering
- Stale tick rejection on init (90s max age)
- Grace period (5 min) after engine start before declaring stall

### 3.2 Data Pipeline
- **INGESTION INVARIANTS**: Single tail read per cycle, no full-file reads
- Degradation mode when loop > 900ms for 10 consecutive cycles
- Telemetry: `WATCHDOG_INGESTION_STATS` (avg tail read, loop duration, ingestion lag)
- Rate limiting: ENGINE_TICK_CALLSITE (5s), BAR_RECEIVED_NO_STREAMS (60s), timetable events (30 min)

### 3.3 State Recovery
- Rebuilds connection status, stream states, bar tracking, identity from snapshot on startup
- Hydrates intent exposures from execution journals (carry-over positions)
- Hydrates stream states from slot journals when event-derived state missing
- Hydrates range data from `ranges_{date}.jsonl` for RANGE_LOCKED streams

### 3.4 Fill Health (Phase 4.3)
- `compute_fill_metrics()` scans robot logs for EXECUTION_FILLED/PARTIAL_FILL
- Tracks: fill_coverage_rate, unmapped_rate, null_trading_date_rate
- Exposed via `/api/watchdog/fill-metrics` and in status `fill_health`

### 3.5 Observability
- WebSocket with snapshot + streaming, heartbeat, backpressure handling
- Events cache TTL (1s) for /events endpoint
- Live feed filters noisy types (tick/bar heartbeats, diagnostics)

---

## 4. Gaps and Issues

### 4.1 Fill Metrics Data Source Mismatch
- **fill_metrics.py** reads from **raw robot logs** (`robot_*.jsonl`)
- **Ledger builder** also reads raw robot logs for EXECUTION_FILLED
- **frontend_feed.jsonl** receives EXECUTION_FILLED only if it's in LIVE_CRITICAL_EVENT_TYPES (it is)
- **Gap**: Fill metrics scan raw logs directly. If robot writes to a different path or format changes, fill_metrics could diverge. Consider whether fill_metrics should also consume from `frontend_feed.jsonl` for consistency, or document that raw logs are the source of truth for fill hygiene.

### 4.2 EXECUTION_FILL_BLOCKED / EXECUTION_FILL_UNMAPPED
- Both are in LIVE_CRITICAL_EVENT_TYPES and reach the feed
- **EventProcessor**: No explicit handler for these — they are not counted in fill_metrics (which only counts EXECUTION_FILLED/PARTIAL_FILL)
- **Gap**: These anomaly events could be aggregated into fill_health (e.g. `blocked_count`, `unmapped_count` from events) to complement the scan-based metrics. Currently fill_metrics only infers unmapped from `mapped=False` on fills.

### 4.3 LEDGER_INVARIANT_VIOLATION / EXECUTION_GATE_INVARIANT_VIOLATION
- Both are live-critical and reach the feed
- **EventProcessor**: No dedicated handler — they go to the ring buffer for WebSocket
- **Gap**: These could be aggregated (e.g. `invariant_violation_count` in status) for dashboard visibility. Ledger builder logs them but watchdog status does not surface a count.

### 4.4 Fill Metrics: execution_sequence / fill_group_id
- Per EXECUTION_LOGGING_CANONICAL_SPEC, fills should have `execution_sequence` and `fill_group_id`
- **fill_metrics** does not validate these fields
- **Gap**: Could add `missing_execution_sequence_count` or `missing_fill_group_id_count` to fill_health for Phase 4.3 completeness.

### 4.5 Broker Flatten / Untracked Fill Gaps (from EXECUTION_LOGGING_GAPS_ASSESSMENT)
- **BROKER_FLATTEN_FILL_RECOGNIZED**: No EXECUTION_FILLED emitted — fill_metrics and ledger cannot see these
- **EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL**: No EXECUTION_FILLED
- **EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL**: No EXECUTION_FILLED
- **Watchdog role**: Watchdog cannot fix these — they are engine-side gaps. But watchdog could:
  - Track `BROKER_FLATTEN_FILL_RECOGNIZED` and `EXECUTION_UPDATE_*_CRITICAL` event counts
  - Surface "fills not in ledger" or "critical fill anomalies" in status

### 4.6 WebSocket Issues (from WATCHDOG_SUMMARY)
- **Status**: May still occur — connection closes with 1006
- **Causes**: Middleware, CORS, route handler
- **Recommendation**: Verify `WS_CONNECT_ATTEMPT` in logs when frontend connects

### 4.7 No Persistence
- State is in-memory only — lost on restart
- **Mitigation**: Startup rebuild from snapshot + journal hydration works well
- **Gap**: No historical trend of engine_alive, stuck_streams, etc. Could add optional persistence (e.g. status snapshots to file) for post-incident analysis.

### 4.8 Duplicate Instance / Execution Policy Failures
- State manager tracks these and exposes in status
- **EventProcessor**: Handles DUPLICATE_INSTANCE_DETECTED and EXECUTION_POLICY_VALIDATION_FAILED
- **Status**: Working — no gap

---

## 5. Recommendations

### 5.1 High Value, Low Effort
1. **Add invariant violation counts to status**: Aggregate LEDGER_INVARIANT_VIOLATION and EXECUTION_GATE_INVARIANT_VIOLATION from events, expose `invariant_violation_count` in status.
2. **Add critical fill anomaly counts**: Track BROKER_FLATTEN_FILL_RECOGNIZED, EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL, EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL; expose in status or fill_health.
3. **Document fill_metrics data source**: Clarify in config or fill_metrics that raw robot logs are the source of truth for fill hygiene.

### 5.2 Medium Value
4. **Fill metrics: execution_sequence / fill_group_id**: Add optional validation and report missing fields in fill_health.
5. **EXECUTION_FILL_BLOCKED / EXECUTION_FILL_UNMAPPED**: Add event-based counts to fill_health to complement scan-based unmapped_rate.
6. **Optional status snapshot persistence**: Write status to `data/watchdog_status_snapshots.jsonl` every N minutes for debugging (rotate by size).

### 5.3 Lower Priority
7. **WebSocket debugging**: Add more diagnostic logging if 1006 persists.
8. **Complexity reduction**: Per COMPLEXITY_ANALYSIS — remove websocket_minimal.py if still present, consider splitting websocket.py.

---

## 6. Checklist: Does the Watchdog Do Its Job?

| Responsibility | Done? | Evidence |
|-----------------|-------|----------|
| Monitor engine liveness | ✅ | ENGINE_TICK_CALLSITE, stall detection, hysteresis |
| Monitor stream states | ✅ | STREAM_STATE_TRANSITION, stuck detection, timetable merge |
| Monitor risk gates | ✅ | Recovery, kill switch, timetable, slot validation |
| Monitor unprotected positions | ✅ | 10s timeout, protective order tracking |
| Monitor data health | ✅ | Bar tracking per instrument, stall detection |
| Monitor connection status | ✅ | CONNECTION_* events, auto-clear on engine alive |
| Monitor identity invariants | ✅ | IDENTITY_INVARIANTS_STATUS, expiry |
| Monitor fill health | ✅ | fill_metrics, unmapped/null_td rates |
| Provide event feed to frontend | ✅ | frontend_feed.jsonl, rate limiting, filtering |
| Provide REST/WebSocket API | ✅ | /status, /events, /risk-gates, etc. |
| Recover from restart | ✅ | Snapshot rebuild, journal hydration |
| Degrade gracefully under load | ✅ | Degradation mode, single tail read |
| Not influence execution | ✅ | Observational only (per design) |

**Verdict**: The watchdog does its job. The main gaps are **enhancements** (invariant counts, critical fill anomaly tracking, optional persistence) rather than core failures.

---

## 7. Files Reference

| Component | Path |
|-----------|------|
| Config | `modules/watchdog/config.py` |
| Aggregator | `modules/watchdog/aggregator.py` |
| Event feed | `modules/watchdog/event_feed.py` |
| Event processor | `modules/watchdog/event_processor.py` |
| State manager | `modules/watchdog/state_manager.py` |
| Fill metrics | `modules/watchdog/pnl/fill_metrics.py` |
| Ledger builder | `modules/watchdog/pnl/ledger_builder.py` |
| API router | `modules/watchdog/backend/routers/watchdog.py` |
| Timetable poller | `modules/watchdog/timetable_poller.py` |
