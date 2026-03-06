# Watchdog Full Capability Audit for Robot Failure Detection and Alerting

**Date**: 2026-03-06  
**Objective**: Determine whether the Watchdog can act as the primary external monitoring and alerting layer for the Robot (NinjaTrader execution engine), enabling failure detection even when NinjaTrader or the Robot is not running.

**Scope**: Investigation and design report only — no code modifications.

---

## Section 1 — Watchdog Architecture Overview

### 1.1 Backend Location and Structure

| Component | Location |
|-----------|----------|
| Main module | `modules/watchdog/` |
| FastAPI server | `modules/watchdog/backend/main.py` (Port 8002) |
| Event feed | `modules/watchdog/event_feed.py` |
| Event processor | `modules/watchdog/event_processor.py` |
| State manager | `modules/watchdog/state_manager.py` |
| Aggregator | `modules/watchdog/aggregator.py` |
| Config | `modules/watchdog/config.py` |
| Timetable poller | `modules/watchdog/timetable_poller.py` |

### 1.2 FastAPI Server Components

- **Entry point**: `modules/watchdog/backend/main.py`
- **Port**: 8002 (configurable via `WATCHDOG_PORT`)
- **Routers**:
  - `watchdog.router` — REST API (`/api/watchdog/*`)
  - `websocket.router` — WebSocket (`/ws/events`, `/ws/events/{run_id}`)
- **Endpoints**: `/status`, `/events`, `/risk-gates`, `/stream-states`, `/active-intents`, `/unprotected-positions`, `/open-journals`, `/fill-metrics`, `/stream-pnl`, etc.

### 1.3 Event Ingestion Pipeline

```
Robot (NinjaTrader) → logs/robot/robot_*.jsonl
                              ↓
                    EventFeedGenerator._read_log_file_incremental()
                              ↓
                    Filter LIVE_CRITICAL_EVENT_TYPES, rate-limit noisy events
                              ↓
                    Append to logs/robot/frontend_feed.jsonl
                              ↓
                    WatchdogAggregator._process_feed_events_sync()
                              ↓
                    EventProcessor.process_event()
                              ↓
                    WatchdogStateManager (in-memory state)
```

### 1.4 Log Ingestion Mechanisms

| Mechanism | Implementation |
|-----------|----------------|
| **Read mode** | Incremental tail (byte-position based) |
| **Read frequency** | Every 1 second (aggregator loop) |
| **Lines per cycle** | All new lines since last position (no fixed cap per read) |
| **Feed tail read** | Last 3,000 lines (`TAIL_LINE_COUNT`) from `frontend_feed.jsonl` |
| **JSON parsing** | Once per line (no repeated parsing) |
| **Concurrency** | Single-threaded in ThreadPoolExecutor (1 worker) — `process_new_events` and `_process_feed_events_sync` run sequentially in executor |

### 1.5 Event Processor / Aggregator

- **EventProcessor**: Handles 50+ event types; updates state manager (engine tick, connection, stream states, intent exposures, bar tracking, identity invariants, etc.).
- **WatchdogStateManager**: Computes derived fields (`engine_alive`, `stuck_streams`, `risk_gate_status`, `watchdog_status`, `unprotected_positions`).
- **WatchdogAggregator**: Coordinates event feed, processing loop, timetable polling, and API responses.

### 1.6 Polling Loops and Background Workers

| Loop | Interval | Purpose |
|------|----------|---------|
| `_process_events_loop` | 1 second | Read robot logs → write to feed → read feed → process events |
| `_poll_timetable_loop` | 60 seconds | Poll `timetable_current.json` for trading_date and enabled streams |
| `_cleanup_stale_streams_periodic` | 60 seconds | Remove stale streams from state |

### 1.7 WebSocket and API Outputs

- **WebSocket** (`/ws/events`): Sends important events from in-memory ring buffer (200 events max), server heartbeat every 30 seconds.
- **REST**: `/api/watchdog/status`, `/events`, `/risk-gates`, `/stream-states`, etc. — all read from in-memory state.

### 1.8 Configuration Files

- `modules/watchdog/config.py` — paths, thresholds, event types
- `configs/execution_policy.json` — canonical→execution instrument mapping (used by aggregator)
- `data/robot_log_read_positions.json` — persisted read positions for robot logs
- `data/frontend_cursor.json` — cursor for event tailing

### 1.9 Log Sources Currently Read

| Log Source | Read? | Notes |
|------------|-------|-------|
| `logs/robot/robot_ENGINE*.jsonl` | ✅ Yes | Via `ROBOT_LOGS_DIR.glob("robot_*.jsonl")` |
| `logs/robot/robot_<instrument>.jsonl` | ✅ Yes | Same glob pattern |
| `logs/robot/hydration_*.jsonl` | ❌ No | Not in EventFeedGenerator; robot writes to it, watchdog does not ingest |
| `logs/robot/strategy_lifecycle.log` | ❌ No | Robot writes for "stuck in Calculating" diagnosis; watchdog does not read |
| `logs/robot/frontend_feed.jsonl` | ✅ Yes | Output of EventFeedGenerator; input to aggregator |
| `logs/robot/journal/*.json` | ✅ Yes | Slot journals for stream state hydration |
| `data/execution_journals/*.json` | ✅ Yes | Intent exposure hydration |
| `logs/robot/ranges_{date}.jsonl` | ✅ Yes | Range data hydration for RANGE_LOCKED streams |
| `timetable_current.json` | ✅ Yes | Via TimetablePoller |

### 1.10 Ingestion Details

- **Tailed continuously**: Yes — incremental read from last byte position.
- **Lines per cycle**: All new lines since last position (unbounded per file).
- **JSON parsing**: Once per line when reading; once per line when processing feed.
- **Single-threaded**: Yes — both feed generation and feed processing run in a single ThreadPoolExecutor worker.

---

## Section 2 — Current Failure Detection Abilities

### 2.1 Failure Signals the Watchdog Can Observe

| Event Name | Origin | Watchdog Processing | Action Taken |
|------------|--------|---------------------|--------------|
| **ENGINE_TICK_STALL_DETECTED** | robot_ENGINE.jsonl | EventProcessor (pass-through) | State manager computes `engine_alive=False`; WebSocket ring buffer |
| **ENGINE_TICK_STALL_RECOVERED** | robot_ENGINE.jsonl | `update_engine_tick()` | Resets liveness |
| **CONNECTION_LOST** | robot_ENGINE.jsonl | `update_connection_status("ConnectionLost")` | UI shows ConnectionLost |
| **CONNECTION_LOST_SUSTAINED** | robot_ENGINE.jsonl | Same | UI update |
| **CONNECTION_RECOVERED** | robot_ENGINE.jsonl | `update_connection_status("Connected")` | UI update |
| **DISCONNECT_FAIL_CLOSED_ENTERED** | robot_ENGINE.jsonl | `update_recovery_state()` | Recovery state in status |
| **DISCONNECT_RECOVERY_*** | robot_ENGINE.jsonl | `update_recovery_state()` | Recovery state tracking |
| **KILL_SWITCH_ACTIVE** | robot_ENGINE.jsonl | `update_kill_switch(True)` | Risk gate status |
| **STREAM_STATE_TRANSITION** | robot_*.jsonl | `update_stream_state()` | Stream state display |
| **ENGINE_START** | robot_ENGINE.jsonl | Cleanup stale streams, init tick | Reset on new run |
| **ENGINE_STOP** | robot_ENGINE.jsonl | In LIVE_CRITICAL_EVENT_TYPES | Passed to feed |
| **DATA_LOSS_DETECTED** | robot_*.jsonl | `mark_data_loss()` | Data stall detection |
| **DATA_STALL_RECOVERED** | robot_*.jsonl | `update_last_bar()` | Resets bar tracking |
| **PROTECTIVE_ORDERS_FAILED_FLATTENED** | robot_*.jsonl | `record_protective_failure()` | Status count |
| **EXECUTION_BLOCKED** | robot_*.jsonl | `record_execution_blocked()` | Status count |
| **DUPLICATE_INSTANCE_DETECTED** | robot_*.jsonl | `record_duplicate_instance()` | Status display |
| **EXECUTION_POLICY_VALIDATION_FAILED** | robot_*.jsonl | `record_execution_policy_failure()` | Status display |
| **TRADE_RECONCILED** | robot_*.jsonl | Stream → DONE | Orphaned journal closed |
| **IDENTITY_INVARIANTS_STATUS** | robot_*.jsonl | `update_identity_invariants()` | Identity status |

### 2.2 Derived Failure Detection (Computed, Not Event-Driven)

| Detection | Logic | Threshold |
|-----------|-------|-----------|
| **Engine tick stall** | No ENGINE_TICK_CALLSITE/ENGINE_ALIVE for 60s + 15s hysteresis | `ENGINE_TICK_STALL_THRESHOLD_SECONDS`=60, `ENGINE_TICK_STALL_HYSTERESIS_SECONDS`=15 |
| **Data stall** | No bar events for instrument with bars_expected for 120s | `DATA_STALL_THRESHOLD_SECONDS`=120 |
| **Stuck stream** | Stream in non-DONE state for 300s (or 2h for ARMED, 30min for PRE_HYDRATION) | `STUCK_STREAM_THRESHOLD_SECONDS`=300 |
| **Unprotected position** | Entry filled, no protective order submitted, >10s | `UNPROTECTED_TIMEOUT_SECONDS`=10 |
| **Recovery timeout** | RECOVERY_RUNNING for >600s without DISCONNECT_RECOVERY_COMPLETE | `RECOVERY_TIMEOUT_SECONDS`=600 |

---

## Section 3 — Push Notification Capability

### 3.1 Watchdog Push Notifications

**The Watchdog does NOT send push notifications.**

- No Pushover, Slack, email, or SMS integration in the watchdog module.
- All push notifications are sent by the **Robot** (NinjaTrader) via `HealthMonitor` and `NotificationService` (Pushover).

### 3.2 Robot Push Notifications (Current)

| Service | Pushover |
|---------|----------|
| **Location** | `RobotCore_For_NinjaTrader/Notifications/PushoverClient.cs`, `HealthMonitor.cs` |
| **Supported events** | CONNECTION_LOST_SUSTAINED, ENGINE_TICK_STALL, MID_SESSION_RESTART, EXECUTION_GATE_INVARIANT_VIOLATION, DISCONNECT_FAIL_CLOSED_ENTERED (via ReportCritical) |
| **Rate limiting** | 5 min per event type for emergency; 10 min default |
| **Deduplication** | One per (eventType, run_id) |

### 3.3 Implication

When NinjaTrader or the Robot process is not running, **no push notifications can be sent** because the notification logic lives inside the Robot. The Watchdog has no notification capability.

---

## Section 4 — Evaluate Ability to Detect Critical Robot Failures

| Scenario | Would Be Detected? | How? | Alert Would Occur? |
|----------|--------------------|------|--------------------|
| **NinjaTrader process crash** | ❌ No | Watchdog only reads log files; no process monitoring | ❌ No (Robot not running) |
| **NinjaTrader process frozen/deadlocked** | ⚠️ Indirect | If Tick() stops, ENGINE_TICK_CALLSITE stops → log stops growing → watchdog sees no new ticks → `engine_alive=False` after 75s. But watchdog reads from file; if NinjaTrader is frozen but still holding file handle, logs may not flush. | ⚠️ Only if logs flush; Robot would also send ENGINE_TICK_STALL |
| **Robot strategy disabled by NinjaTrader** | ❌ No | No strategy-enabled check; no NinjaTrader API | ❌ No |
| **Robot stops processing ticks** | ✅ Yes | ENGINE_TICK_CALLSITE stops → stall detected | ⚠️ Robot sends; Watchdog has no push |
| **Robot disconnects from broker** | ✅ Yes | CONNECTION_LOST events | ⚠️ Robot sends; Watchdog has no push |
| **Robot restarts mid-session** | ✅ Yes (on next run) | MID_SESSION_RESTART_NOTIFICATION from Robot; watchdog would see journal state | ⚠️ Robot sends on startup; Watchdog has no push |
| **Robot exits while holding position** | ⚠️ Partial | Execution journals remain; watchdog hydrates intents. But no "robot exited" event. | ❌ No alert from Watchdog |
| **Robot fails to flatten position** | ⚠️ Partial | PROTECTIVE_ORDERS_FAILED_FLATTENED event if robot emits it | ❌ No push from Watchdog |
| **Robot event loop stalls** | ✅ Yes | Same as "stops processing ticks" | ⚠️ Robot sends; Watchdog has no push |
| **robot_ENGINE.jsonl stops updating** | ✅ Yes | No new ENGINE_TICK_CALLSITE → stall detected after threshold | ❌ No push from Watchdog |

### 4.2 Log Inactivity / Heartbeat / Missing Events

| Detection | Supported? | Notes |
|-----------|------------|-------|
| **Log inactivity** | ✅ Yes | Implicit: no new events → no ENGINE_TICK_CALLSITE → engine_alive=False |
| **Missing heartbeats** | ✅ Yes | ENGINE_TICK_CALLSITE used as heartbeat; stall = missing heartbeats |
| **Missing engine ticks** | ✅ Yes | Same as above |
| **Missing strategy state events** | ⚠️ Partial | Stuck stream detection catches streams not transitioning; no explicit "strategy disabled" |

---

## Section 5 — Heartbeat Monitoring Capability

### 5.1 Robot Heartbeat Events

| Event | Exists? | Frequency | Logged To | Watchdog Use |
|-------|---------|-----------|-----------|--------------|
| **ENGINE_TICK_CALLSITE** | ✅ Yes | Every Tick() call (rate-limited to 5s in feed) | robot_ENGINE.jsonl | Primary liveness signal |
| **ENGINE_HEARTBEAT** | ⚠️ Deprecated | Was from HeartbeatAddOn | robot_ENGINE.jsonl | Removed from LIVE_CRITICAL_EVENT_TYPES; handler kept for backward compat |
| **ENGINE_TICK_HEARTBEAT** | ✅ Yes | Bar-driven (rate-limited) | robot_ENGINE.jsonl | Bar tracking, secondary liveness |
| **ENGINE_ALIVE** | ✅ Yes | Every N bars in Realtime | robot_ENGINE.jsonl | Fallback liveness when ENGINE_TICK_CALLSITE not emitted |
| **HEARTBEAT** | ✅ Yes | StreamStateMachine every 7 min | robot_*.jsonl | Not in LIVE_CRITICAL_EVENT_TYPES |
| **WATCHDOG_HEARTBEAT** | ❌ No | — | — | — |
| **ENGINE_STATUS** | ❌ No | — | — | — |

### 5.2 Summary

- **Primary heartbeat**: ENGINE_TICK_CALLSITE (every Tick(), rate-limited to 5s in feed).
- **Fallback**: ENGINE_ALIVE, ENGINE_TICK_HEARTBEAT.
- **ENGINE_HEARTBEAT**: Deprecated; only works if HeartbeatAddOn/Strategy installed.
- **Frequency**: ENGINE_TICK_CALLSITE fires very frequently; feed rate-limits to one per 5 seconds per run_id.

---

## Section 6 — Process Monitoring Capability

### 6.1 Current State

**The Watchdog does NOT monitor system processes.**

| Check | Implemented? |
|-------|--------------|
| NinjaTrader.exe running | ❌ No |
| Robot strategy active | ❌ No |
| CPU / thread health | ❌ No |
| Robot log file growth | ⚠️ Indirect | Stall detection infers "no new logs" when no ENGINE_TICK_CALLSITE; no explicit file-growth check |

### 6.2 Difficulty to Add

- **NinjaTrader.exe**: Medium — use `psutil` or `subprocess` to list processes; need to handle multiple NinjaTrader instances.
- **Strategy enabled**: Hard — would require NinjaTrader API or parsing NinjaTrader state files; no such integration exists.
- **Log file growth**: Easy — compare `mtime` or file size of `robot_ENGINE.jsonl` between cycles.

---

## Section 7 — Position Safety Monitoring

### 7.1 Current Access

| Data Source | Watchdog Access? | Purpose |
|-------------|------------------|---------|
| Execution journals | ✅ Yes | `EXECUTION_JOURNALS_DIR` — hydrate intent exposures |
| Position snapshots | ❌ No | Not implemented |
| Account position queries | ❌ No | No broker API |
| Robot intent journals | ✅ Yes | `ROBOT_JOURNAL_DIR` — slot journals for stream state |

### 7.2 Orphan Position Detection

**Can the Watchdog detect "position open but robot not running"?**

- **Partially**: Watchdog hydrates from execution journals (EntryFilled && !TradeCompleted). If robot stops, journals remain. Watchdog would still show active intents from journals.
- **Gap**: Watchdog does not explicitly check "robot process not running" vs "robot running." It cannot distinguish "orphan" (position with no live robot) from "active position with robot running."
- **TRADE_RECONCILED**: Handles case where orphaned journal is closed because broker was flat; watchdog transitions stream to DONE.

### 7.3 How to Build Orphan Detection

1. **Process monitor**: If NinjaTrader.exe not running AND execution journals show EntryFilled && !TradeCompleted → orphan.
2. **Heartbeat + journals**: If no ENGINE_TICK_CALLSITE for N seconds AND active intents from journals → potential orphan (robot may have exited).
3. **Broker API**: If available, compare broker positions to robot journals — out-of-sync could indicate orphan. Not currently implemented.

---

## Section 8 — Event Coverage Comparison

| Alert | Status | Notes |
|-------|--------|-------|
| **Robot heartbeat lost** | ✅ Partially supported | Detected via ENGINE_TICK_CALLSITE stall; no push from Watchdog |
| **NinjaTrader process stopped** | ❌ Not supported | No process monitoring |
| **Strategy disabled** | ❌ Not supported | No strategy state monitoring |
| **Connection lost sustained** | ✅ Partially supported | Events processed; no push from Watchdog |
| **Engine tick stall** | ✅ Partially supported | Detected; no push from Watchdog |
| **Mid-session restart detected** | ✅ Partially supported | Robot emits; Watchdog would see journal state; no push from Watchdog |
| **Position orphan detected** | ❌ Not supported | No explicit orphan detection |

---

## Section 9 — Required Architecture Changes

### 9.1 Robot Responsibilities

| Responsibility | Current | Required |
|----------------|--------|----------|
| Emit structured events only | ✅ Yes | Keep |
| Emit heartbeat events every few seconds | ✅ ENGINE_TICK_CALLSITE | Keep; ensure rate limit ≤5s |
| Log critical failures | ✅ Yes | Keep |
| Rely on Watchdog for alerts | ❌ No | Add: Robot can reduce notification logic if Watchdog becomes primary |

### 9.2 Watchdog Responsibilities

| Responsibility | Current | Required |
|----------------|--------|----------|
| Monitor robot heartbeats | ✅ Yes | Keep; add explicit "heartbeat lost" alert |
| Monitor log activity | ✅ Implicit | Add: explicit log inactivity detection |
| Monitor NinjaTrader process | ❌ No | Add: psutil-based process check |
| Monitor strategy enabled state | ❌ No | Add if feasible (NinjaTrader API or file) |
| Monitor orphan positions | ❌ No | Add: process down + active journals = orphan |
| Send push notifications | ❌ No | Add: Pushover (or configurable) integration |

### 9.3 Alert Strategies

| Strategy | Proposal |
|----------|----------|
| **Rate limiting** | Per alert type: e.g., 5 min min interval; configurable per severity |
| **Deduplication** | Per (alert_type, run_id, optional_context) over sliding window |
| **Context enrichment** | Include: instrument, stream, trading_date, timestamp_chicago, last_known_state |

---

## Section 10 — Implementation Roadmap

### High Priority (Critical Safety)

1. **Watchdog push notification integration** — Add Pushover (or similar) to watchdog; send alerts when engine_alive=False, connection lost, etc.
2. **Robot heartbeat event** — Already exists (ENGINE_TICK_CALLSITE); ensure it is reliable and rate-limited appropriately.
3. **Watchdog heartbeat monitor** — Already exists (stall detection); add explicit "heartbeat lost" alert when threshold exceeded.
4. **Process monitor** — Add NinjaTrader.exe check; alert when process not running during market hours.

### Medium Priority

5. **Position orphan detection** — Process not running + active execution journals → orphan alert.
6. **Log inactivity monitor** — Explicit check: robot_ENGINE.jsonl mtime/size not growing.
7. **Alert context enrichment** — Add instrument, stream, timestamp to all alerts.
8. **Alert rate limiting and deduplication** — Implement in watchdog before sending.

### Low Priority

9. **Strategy enabled monitoring** — If NinjaTrader exposes state.
10. **Dashboard visualizations** — Enhanced UI for new alerts.
11. **Hydration log ingestion** — Optionally ingest `hydration_*.jsonl` for richer state.
12. **strategy_lifecycle.log** — Optionally monitor for "stuck in Calculating."

---

## Section 11 — Final Assessment

### 11.1 Current Maturity Level

**Advanced monitoring** — The Watchdog has strong event ingestion, state management, and derived failure detection. It lacks:

- Push notifications
- Process monitoring
- Orphan position detection
- Strategy state monitoring

### 11.2 Path to Institutional-Grade External Supervision

| Requirement | Status | Action |
|-------------|--------|--------|
| External to Robot | ✅ Yes | Watchdog runs separately |
| Works when Robot down | ⚠️ Partial | Can detect via log inactivity; cannot send alerts without push |
| Process monitoring | ❌ No | Add NinjaTrader.exe check |
| Push notifications | ❌ No | Add Pushover to Watchdog |
| Orphan detection | ❌ No | Add process + journal logic |
| Heartbeat monitoring | ✅ Yes | ENGINE_TICK_CALLSITE stall detection |
| Alert deduplication | ❌ No | Add in Watchdog |
| Audit trail | ⚠️ Partial | Logs only; no persistent alert history |

### 11.3 Summary

The Watchdog is a capable **observational** system with robust event processing and state derivation. To become the **primary external monitoring and alerting layer**, it needs:

1. **Push notification integration** — So it can alert when Robot cannot (e.g., process crash).
2. **Process monitoring** — To detect NinjaTrader not running.
3. **Orphan position detection** — To detect positions without a live Robot.
4. **Alert rate limiting and deduplication** — To avoid alert fatigue.

With these additions, the Watchdog can provide institutional-grade external supervision independent of the Robot process.

---

## Appendix A — File Reference

| Component | Path |
|-----------|------|
| Config | `modules/watchdog/config.py` |
| Event feed | `modules/watchdog/event_feed.py` |
| Event processor | `modules/watchdog/event_processor.py` |
| State manager | `modules/watchdog/state_manager.py` |
| Aggregator | `modules/watchdog/aggregator.py` |
| Backend main | `modules/watchdog/backend/main.py` |
| Watchdog router | `modules/watchdog/backend/routers/watchdog.py` |
| WebSocket router | `modules/watchdog/backend/routers/websocket.py` |
| Timetable poller | `modules/watchdog/timetable_poller.py` |

## Appendix B — Key Config Values

| Config | Value |
|--------|-------|
| ENGINE_TICK_STALL_THRESHOLD_SECONDS | 60 |
| ENGINE_TICK_STALL_HYSTERESIS_SECONDS | 15 |
| DATA_STALL_THRESHOLD_SECONDS | 120 |
| STUCK_STREAM_THRESHOLD_SECONDS | 300 |
| UNPROTECTED_TIMEOUT_SECONDS | 10 |
| RECOVERY_TIMEOUT_SECONDS | 600 |
| TAIL_LINE_COUNT | 3000 |
