# Push Notification Architecture Audit

**Date**: 2026-03-14  
**Scope**: Read-only architectural audit of the push notification system across QTSW2, verifying compatibility with Phase 9 watchdog hardening changes.

---

## 1. Push Notification Architecture

### 1.1 Components

| Component | Location | Role |
|-----------|----------|------|
| **NotificationService** | `modules/watchdog/notifications/notification_service.py` | Queued alert delivery, Pushover integration, dedupe, rate limiting |
| **Pushover client** | `modules/watchdog/notifications/pushover_client.py` | HTTP POST to `api.pushover.net/1/messages.json` |
| **Alert engine** | `modules/watchdog/alert_engine.py` | `on_incident()` — maps incident records to alerts via rules |
| **Alert ledger** | `modules/watchdog/alert_ledger.py` | Append-only audit trail, active alert tracking |
| **Incident recorder** | `modules/watchdog/incident_recorder.py` | Tracks incident start/end, writes to `incidents.jsonl`, invokes callback |
| **Event processor** | `modules/watchdog/event_processor.py` | Routes events to state manager and incident recorder |
| **Event feed** | `modules/watchdog/event_feed.py` | Reads robot logs → `frontend_feed.jsonl` |
| **Watchdog aggregator** | `modules/watchdog/aggregator.py` | Orchestrates loops, wires callback, `_check_alert_conditions()` |
| **Dashboard backend** | `modules/watchdog/backend/routers/watchdog.py` | REST API; does not send notifications |

### 1.2 Data Flow: Event → Notification

```
Robot logs (robot_ENGINE.jsonl, robot_<instrument>.jsonl)
    ↓
EventFeedGenerator.process_new_events()
    ↓ (filters by LIVE_CRITICAL_EVENT_TYPES)
frontend_feed.jsonl
    ↓
Aggregator._process_feed_events_sync() → _read_last_lines_with_metrics()
    ↓
EventProcessor.process_event(ev)
    ↓
incident_recorder.process_event(ev)  [always called first, never throws]
    ↓ (on incident END only)
IncidentRecorder._write_incident(record)
    ↓
callback(record) = on_incident(record, notification_service)
    ↓
alert_engine.on_incident(record, ns)
    ↓ (if rule matches, cooldown OK, primary incident)
notification_service.raise_alert(...)
    ↓
asyncio.Queue (max 100)
    ↓
_worker_loop() → pushover_send() → Pushover API
```

### 1.3 Callback Wiring

- **File**: `modules/watchdog/aggregator.py` (lines 524–530)
- **When**: During `start()`, after `NotificationService` and `ProcessMonitor` are created
- **Code**:
  ```python
  get_incident_recorder().set_on_incident_callback(lambda r: on_incident(r, ns))
  ```
- **Invocation**: `IncidentRecorder._write_incident()` calls `cb(record)` after appending to `incidents.jsonl`

---

## 2. Alert Trigger Sources

### 2.1 Incident-Based Alerts (via `alerts.json`)

| Incident type | Rule in alerts.json | Min duration | Severity | Cooldown |
|---------------|---------------------|--------------|----------|----------|
| CONNECTION_LOST | Yes | 60s | critical | 900s |
| ENGINE_STALLED | Yes | 30s | critical | 600s |
| DATA_STALL | Yes | 0s | high | 600s |
| FORCED_FLATTEN | Yes | 0s | critical | 300s |
| RECONCILIATION_QTY_MISMATCH | Yes | 60s | high | 900s |

### 2.2 State-Based Alerts (`_check_alert_conditions`)

| Alert type | Trigger |
|------------|---------|
| ROBOT_HEARTBEAT_LOST | `engine_alive=False`, supervision window open |
| CONNECTION_LOST_SUSTAINED | `connection_status=ConnectionLost` ≥ 60s |
| POTENTIAL_ORPHAN_POSITION | Heartbeat lost or process missing + active intents |
| CONFIRMED_ORPHAN_POSITION | Heartbeat lost ≥ 120s + active intents |
| RECOVERY_LOOP_DETECTED | `DISCONNECT_RECOVERY_STARTED` count in window |
| FEED_INGESTION_DELAY | Ingestion lag > threshold |
| WATCHDOG_LOOP_SLOW | Loop duration > threshold |
| ANOMALY_RATE_EXCEEDED | Anomaly count in window |
| ORDER_STUCK_DETECTED | Order submit without fill/cancel |
| LOG_FILE_STALLED | Feed file size unchanged 60s |

### 2.3 Process Monitor Alerts

| Alert type | Trigger |
|------------|---------|
| NINJATRADER_PROCESS_STOPPED | `NinjaTrader.exe` not running |
| POTENTIAL_ORPHAN_POSITION | Process down + active intents |

### 2.4 Incident Severity

- **Phase 9**: `incident_recorder.py` adds `severity` to incident records (`INCIDENT_SEVERITY` map).
- **Alert engine**: Uses `rule.get("severity", "high")` from `alerts.json`, **not** the incident `severity` field.
- **NotificationService**: Maps severity to Pushover priority: `critical=2`, `high/warning=1`, `info=0`.
- **Conclusion**: Incident `severity` is for dashboard display and filtering only; alert routing uses rule severity.

### 2.5 Correlation Suppression

- **Primary vs secondary**: `incident_correlator.tag_incident_record()` sets `primary=False` for cascaded incidents (e.g. DATA_STALL when CONNECTION_LOST is active).
- **Alert engine**: Skips `primary=False` incidents (line 66–68 in `alert_engine.py`).
- **Cooldown**: Per-alert-type cooldown via `_last_delivery_by_type` and `alerts.json` rules.

---

## 3. Integration With Recent Watchdog Changes

### 3.1 `active_incidents.json` Persistence

- **Impact**: None on alert flow.
- **Behavior**: Active incidents are loaded on startup and saved on start/end. Incident **end** still triggers `_write_incident` → callback → alert.
- **Verification**: ✅ Alert logic unchanged.

### 3.2 Metrics History Snapshots

- **Impact**: None on alert flow.
- **Behavior**: `append_weekly_snapshot()` runs every 6 hours in aggregator loop. No interaction with notification or incident recorder.
- **Verification**: ✅ No conflicts.

### 3.3 Incident Severity Field

- **Impact**: None on alert routing.
- **Behavior**: `severity` added to incident record; alert engine uses rule severity.
- **Verification**: ✅ Alert engine unchanged.

### 3.4 Tail-Based Incident Replay

- **Impact**: None on alert flow.
- **Behavior**: `load_incident_events()` reads last 20MB of feed for post-mortem. Replay is for debugging and API; not part of alert pipeline.
- **Verification**: ✅ No conflicts.

---

## 4. Execution Event Integration

### 4.1 Critical Gap: `LIVE_CRITICAL_EVENT_TYPES` Filter

The event feed **filters** events by `LIVE_CRITICAL_EVENT_TYPES` in `config.py`. Only events in this set reach `frontend_feed.jsonl` and thus the incident recorder.

| Event type | In LIVE_CRITICAL? | Incident recorder usage |
|------------|-------------------|-------------------------|
| CONNECTION_LOST | Yes | Start CONNECTION_LOST |
| CONNECTION_LOST_SUSTAINED | Yes | Start CONNECTION_LOST |
| CONNECTION_RECOVERED | Yes | End CONNECTION_LOST |
| CONNECTION_RECOVERED_NOTIFICATION | Yes | End CONNECTION_LOST |
| ENGINE_TICK_STALL_DETECTED | Yes | Start ENGINE_STALLED |
| ENGINE_ALIVE, ENGINE_TICK_CALLSITE, ENGINE_TIMER_HEARTBEAT | Yes | End ENGINE_STALLED |
| ENGINE_TICK_STALL_RECOVERED | Yes | End ENGINE_STALLED |
| DATA_LOSS_DETECTED | Yes | Start DATA_STALL |
| DATA_STALL_DETECTED | **No** | Start DATA_STALL (derived) |
| DATA_STALL_RECOVERED | Yes | End DATA_STALL |
| FORCED_FLATTEN_TRIGGERED | **No** | Start FORCED_FLATTEN |
| FORCED_FLATTEN_POSITION_CLOSED | **No** | End FORCED_FLATTEN |
| SESSION_FORCED_FLATTENED | **No** | End FORCED_FLATTEN |
| RECONCILIATION_QTY_MISMATCH | Yes | Start RECONCILIATION_QTY_MISMATCH |
| RECONCILIATION_PASS_SUMMARY | **No** | End RECONCILIATION_QTY_MISMATCH |

### 4.2 Impact

- **FORCED_FLATTEN**: Never detected. Start and end events are filtered out.
- **RECONCILIATION_QTY_MISMATCH**: Incidents start but never end (RECONCILIATION_PASS_SUMMARY filtered out).
- **DATA_STALL**: Works via DATA_LOSS_DETECTED (start) and DATA_STALL_RECOVERED (end). DATA_STALL_DETECTED is derived by state manager; if emitted as a derived event, it would need to be injected into the feed path (not from robot logs).

### 4.3 Execution Events

- **SESSION_FORCED_FLATTEN_TRIGGERED**, **SESSION_FORCED_FLATTEN_SUBMITTED**: Not in incident recorder; FORCED_FLATTEN uses FORCED_FLATTEN_TRIGGERED, which is not in LIVE_CRITICAL.
- **EXECUTION_FILLED**: In LIVE_CRITICAL; used for fill tracking, not incident alerts.
- **RECONCILIATION_QTY_MISMATCH**: In LIVE_CRITICAL; incident start works; end (RECONCILIATION_PASS_SUMMARY) does not.

---

## 5. Failure Modes

### 5.1 Silent Failure Paths

| Path | Risk | Mitigation |
|------|------|------------|
| **Callback exception** | `logger.debug(f"Incident callback error: {e}")` — swallowed at DEBUG level | If root logging is INFO, no visibility |
| **NotificationService init failure** | `self._notification_service = None`; callback receives `ns=None` | `on_incident` returns early if `None` |
| **Pushover send failure** | Retries (2), logs `logger.warning` | Ledger marks `delivery_status=failed` |
| **Queue full** | `put_nowait` → `QueueFull` → `logger.warning` | Alert dropped |
| **Config missing** | `_enabled=False` | `raise_alert` returns immediately |
| **Alerts config missing** | `_load_config` returns `{}` | No rules → no alerts |
| **Rule missing for incident type** | `rule = rules.get(incident_type)` → `None` | No alert |
| **Primary=False** | Cascaded incident skipped | By design |
| **Cooldown** | `logger.debug` | No visibility at INFO level |

### 5.2 Event Feed Filter

- **Event type not in LIVE_CRITICAL**: Event never reaches incident recorder; no alert.
- **No retry** for filtered events; design assumes correct event set.

### 5.3 Logging Levels

- **DEBUG**: Callback errors, cooldown skips, rate limits.
- **INFO**: Notification service start/stop, config disabled.
- **WARNING**: Pushover failures, queue full, rate limit.

---

## 6. End-to-End Notification Flow

### 6.1 FORCED_FLATTEN Example (Current State: Broken)

```
Robot: LogEvent(FORCED_FLATTEN_TRIGGERED)
    ↓
robot_ENGINE.jsonl
    ↓
EventFeedGenerator._process_event()
    ↓
event_type in LIVE_CRITICAL_EVENT_TYPES? → NO (FORCED_FLATTEN_TRIGGERED not in set)
    ↓
return None → event dropped
```

**Result**: FORCED_FLATTEN incidents are never created; no alerts.

### 6.2 CONNECTION_LOST Example (Working)

```
Robot: LogEvent(CONNECTION_LOST)
    ↓
robot_ENGINE.jsonl
    ↓
EventFeedGenerator → frontend_feed.jsonl (CONNECTION_LOST in LIVE_CRITICAL)
    ↓
Aggregator._process_feed_events_sync → EventProcessor.process_event
    ↓
incident_recorder.process_event → INCIDENT_START_EVENTS → _active_incidents["CONNECTION_LOST"]
    ↓
... later ...
Robot: LogEvent(CONNECTION_RECOVERED)
    ↓
... same path ...
    ↓
incident_recorder.process_event → INCIDENT_END_EVENTS → pop, _write_incident(record)
    ↓
callback(record) → on_incident(record, ns)
    ↓
alert_engine: rule exists, primary=True, cooldown OK
    ↓
notification_service.raise_alert(INCIDENT_CONNECTION_LOST, ...)
    ↓
Queue → _worker_loop → pushover_send → Pushover API
```

**Result**: ✅ End-to-end path exists and works for CONNECTION_LOST.

### 6.3 RECONCILIATION_QTY_MISMATCH Example (Partial)

- Start: RECONCILIATION_QTY_MISMATCH in LIVE_CRITICAL → incident starts.
- End: RECONCILIATION_PASS_SUMMARY not in LIVE_CRITICAL → incident never ends.

**Result**: Incidents start but never complete; no alerts (alerts fire only on incident end).

---

## 7. Recommended Fixes

### 7.1 Critical: Add Missing Event Types to LIVE_CRITICAL_EVENT_TYPES

**File**: `modules/watchdog/config.py`

Add to `LIVE_CRITICAL_EVENT_TYPES`:

```python
# Forced flatten
"FORCED_FLATTEN_TRIGGERED",
"FORCED_FLATTEN_POSITION_CLOSED",
"SESSION_FORCED_FLATTENED",
# Reconciliation
"RECONCILIATION_PASS_SUMMARY",
# Data stall (if state manager emits DATA_STALL_DETECTED as derived event)
"DATA_STALL_DETECTED",
```

**Impact**: FORCED_FLATTEN and RECONCILIATION_QTY_MISMATCH incidents can be detected and completed; alerts will fire.

### 7.2 Improve Callback Error Visibility

**File**: `modules/watchdog/incident_recorder.py`

Change line 214 from:

```python
logger.debug(f"Incident callback error: {e}")
```

to:

```python
logger.warning(f"Incident callback error (alert may not have been sent): {e}")
```

**Impact**: Callback failures visible at default INFO level.

### 7.3 Optional: Use Incident Severity for Alert Routing

**File**: `modules/watchdog/alert_engine.py`

Add fallback to incident severity when rule severity is missing:

```python
severity = rule.get("severity") or record.get("severity", "high")
# Normalize CRITICAL/WARNING to lowercase
severity = str(severity).lower() if severity else "high"
```

**Impact**: CRITICAL incidents can drive higher Pushover priority when rule severity is omitted.

### 7.4 Add Missing ALERT_TYPE_LABELS

**File**: `modules/watchdog/notifications/notification_service.py`

Add labels for state-based alerts that may not have entries:

```python
"RECOVERY_LOOP_DETECTED": "Recovery Loop Detected",
"FEED_INGESTION_DELAY": "Feed Ingestion Delay",
"WATCHDOG_LOOP_SLOW": "Watchdog Loop Slow",
"ANOMALY_RATE_EXCEEDED": "Anomaly Rate Exceeded",
"ORDER_STUCK_DETECTED": "Order Stuck Detected",
```

**Impact**: Clearer notification titles for these alert types.

---

## 8. Summary

| Question | Answer |
|----------|--------|
| Does the full pipeline exist? | Yes, for incident-based alerts. |
| Do CONNECTION_LOST, ENGINE_STALLED, DATA_STALL, FORCED_FLATTEN, RECONCILIATION_QTY_MISMATCH flow to alerts? | CONNECTION_LOST, ENGINE_STALLED, DATA_STALL: Yes. FORCED_FLATTEN, RECONCILIATION_QTY_MISMATCH: No (events filtered out). |
| Is incident severity used for routing? | No; rule severity is used. |
| Can the system silently fail? | Yes: callback errors at DEBUG, queue full, config missing. |
| Are Phase 9 changes compatible? | Yes; no conflicts. |
| Critical fix required? | Yes: add FORCED_FLATTEN_*, RECONCILIATION_PASS_SUMMARY, DATA_STALL_DETECTED to LIVE_CRITICAL_EVENT_TYPES. |
