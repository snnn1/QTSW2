# Push Notification and Alerting System – Full Architectural Audit

**Date:** 2026-03-15  
**Scope:** QTSW2 codebase (robot + watchdog) – whole-system audit  
**Mode:** Read-only

---

## Executive Summary

The push notification system has **two independent paths**:

1. **Robot HealthMonitor (C#)** – Sends Pushover directly for connection loss, engine stall, and data loss via the C# NotificationService singleton.
2. **Watchdog (Python)** – Reads robot logs, records incidents, and sends incident-based and state-based alerts via its own NotificationService.

The watchdog path is **partially broken**:

- **Working:** CONNECTION_LOST, ENGINE_STALLED, DATA_STALL (via DATA_LOSS_DETECTED), state-based alerts (heartbeat lost, orphan, process down, connection sustained, recovery loop, feed delay, log stalled, etc.).
- **Broken:** FORCED_FLATTEN and RECONCILIATION_QTY_MISMATCH never produce alerts because start/end events are filtered out by `LIVE_CRITICAL_EVENT_TYPES`.
- **Missing:** FORCED_FLATTEN_FAILED, DUPLICATE_INSTANCE_DETECTED, EXECUTION_POLICY_VALIDATION_FAILED, and other critical robot events do not trigger push notifications.

**Verdict:** The system **cannot be fully trusted** for live trading. Critical failures (forced flatten, reconciliation mismatch, flatten failure, duplicate instance, execution policy failure) may not generate alerts.

---

## Full Notification Architecture

### Components and Data Flow

| Component | Location | Role |
|-----------|----------|------|
| **Robot HealthMonitor** | `NT_ADDONS/HealthMonitor.cs`, `modules/robot/core/HealthMonitor.cs` | Detects connection/data/engine stalls; sends Pushover via C# NotificationService |
| **Robot NotificationService** | `modules/robot/core/Notifications/NotificationService.cs` | C# singleton; Pushover delivery; rate limit, dedupe |
| **EventFeedGenerator** | `modules/watchdog/event_feed.py` | Reads `robot_*.jsonl`, filters by `LIVE_CRITICAL_EVENT_TYPES`, writes `frontend_feed.jsonl` |
| **Watchdog Aggregator** | `modules/watchdog/aggregator.py` | Orchestrates loops, wires callbacks, `_check_alert_conditions()` |
| **EventProcessor** | `modules/watchdog/event_processor.py` | Routes events to state manager and incident recorder |
| **IncidentRecorder** | `modules/watchdog/incident_recorder.py` | Tracks incident start/end, writes `incidents.jsonl`, invokes callback on incident end |
| **AlertEngine** | `modules/watchdog/alert_engine.py` | `on_incident()` maps incident records to alerts via `alerts.json` |
| **NotificationService** | `modules/watchdog/notifications/notification_service.py` | Queued alert delivery, Pushover integration |
| **Pushover client** | `modules/watchdog/notifications/pushover_client.py` | HTTP POST to `api.pushover.net/1/messages.json` |
| **AlertLedger** | `modules/watchdog/alert_ledger.py` | Append-only audit trail, active alert tracking |

### End-to-End Flow

```
Robot: LogEvent / LogHealth → robot_ENGINE.jsonl, robot_<instrument>.jsonl
    ↓
EventFeedGenerator.process_new_events() → _process_event()
    ↓ (filter: event_type in LIVE_CRITICAL_EVENT_TYPES)
frontend_feed.jsonl
    ↓
Aggregator._process_feed_events_sync() → _read_last_lines_with_metrics()
    ↓
EventProcessor.process_event(ev)
    ↓
├── incident_recorder.process_event(ev)  [always first]
├── state_manager updates
└── (derived events: drain_pending_derived_events)
    ↓ (on incident END only)
IncidentRecorder._write_incident(record)
    ↓
callback(record) = on_incident(r, ns)
    ↓
alert_engine.on_incident(record, ns)
    ↓ (rule match, cooldown OK, primary=True)
notification_service.raise_alert(...)
    ↓
asyncio.Queue (max 100)
    ↓
_worker_loop() → pushover_send() → Pushover API
```

### Notification Paths

| Path | Source | Trigger | Delivery |
|------|--------|---------|----------|
| **Direct robot** | HealthMonitor (C#) | CONNECTION_LOST sustained, ENGINE_TICK_STALL, DATA_LOSS_DETECTED | C# NotificationService → Pushover |
| **Incident-based** | IncidentRecorder | Incident end (CONNECTION_LOST, ENGINE_STALLED, DATA_STALL, FORCED_FLATTEN, RECONCILIATION_QTY_MISMATCH) | Watchdog NotificationService → Pushover |
| **State-based** | `_check_alert_conditions()` | Heartbeat lost, orphan, process down, connection sustained, recovery loop, feed delay, loop slow, anomaly rate, stuck order, log stalled | Watchdog NotificationService → Pushover |
| **Process monitor** | ProcessMonitor | NinjaTrader.exe not running | Watchdog NotificationService → Pushover |

### Callback Wiring

- **File:** `modules/watchdog/aggregator.py` lines 524–530
- **Code:** `get_incident_recorder().set_on_incident_callback(lambda r: on_incident(r, ns))`
- **Invocation:** `IncidentRecorder._write_incident()` calls `cb(record)` after appending to `incidents.jsonl`

---

## Robot Event Coverage

### Events That Feed Notifications

| Event | Path | Notification |
|-------|------|--------------|
| CONNECTION_LOST / CONNECTION_LOST_SUSTAINED | Robot HealthMonitor + Watchdog incident | Both |
| CONNECTION_RECOVERED | Incident end | Watchdog only |
| ENGINE_TICK_STALL_DETECTED | Robot HealthMonitor + Watchdog incident | Both |
| DATA_LOSS_DETECTED | Robot HealthMonitor + Watchdog incident | Both |
| DATA_STALL_RECOVERED | Incident end | Watchdog only |

### Events That Reach Watchdog but Do Not Notify

| Event | In LIVE_CRITICAL? | Incident Recorder | Alert Rule | Result |
|-------|-------------------|-------------------|------------|--------|
| DUPLICATE_INSTANCE_DETECTED | Yes | No (no incident type) | No | State only, no alert |
| EXECUTION_POLICY_VALIDATION_FAILED | Yes | No | No | State only, no alert |
| RECONCILIATION_QTY_MISMATCH | Yes | Start RECONCILIATION_QTY_MISMATCH | Yes | Incident starts but never ends (end event filtered) |
| EXECUTION_FILLED | Yes | Fill tracking only | No | Informational |
| CRITICAL_EVENT_REPORTED | Yes | No | No | Generic wrapper, no incident |
| EXECUTION_BLOCKED | Yes | No | No | State only |

### Events Filtered Out (Never Reach Incident Recorder)

| Event | In LIVE_CRITICAL? | Emitted By | Result |
|-------|-------------------|------------|--------|
| FORCED_FLATTEN_TRIGGERED | **No** | RobotEngine, JournalStore | Dropped at event feed |
| FORCED_FLATTEN_POSITION_CLOSED | **No** | StreamStateMachine | Dropped |
| SESSION_FORCED_FLATTENED | **No** | ExecutionJournal | Dropped |
| RECONCILIATION_PASS_SUMMARY | **No** | ReconciliationRunner | Dropped |
| FORCED_FLATTEN_FAILED | **No** | StreamStateMachine.LogHealth | Dropped |
| FORCED_FLATTEN_EXPOSURE_REMAINING | **No** | StreamStateMachine.LogHealth | Dropped |
| REENTRY_FAILED | **No** | StreamStateMachine.LogHealth | Dropped |
| REENTRY_PROTECTION_FAILED | **No** | StreamStateMachine.LogHealth | Dropped |
| RANGE_LOCK_TRANSITION_FAILED | **No** | StreamStateMachine.LogHealth | Dropped |
| RANGE_LOCK_VALIDATION_FAILED | **No** | StreamStateMachine.LogHealth | Dropped |
| EXECUTION_JOURNAL_CORRUPTION | **No** | ExecutionJournal | Dropped |
| EXECUTION_JOURNAL_ERROR | **No** | ExecutionJournal | Dropped |

### Critical Robot Events – Summary

| Event | Logged | Reaches Watchdog | Triggers Notification |
|-------|--------|------------------|------------------------|
| Engine lifecycle (ENGINE_START, ENGINE_STOP) | Yes | Yes | No (informational) |
| Session/forced flatten (FORCED_FLATTEN_TRIGGERED, etc.) | Yes | No | No |
| Reconciliation (RECONCILIATION_QTY_MISMATCH, RECONCILIATION_PASS_SUMMARY) | Yes | Start only | No (incident never ends) |
| Execution failures (ORDER_REJECTED, ORDER_SUBMIT_FAIL, etc.) | Yes | Some | No incident rules |
| Disconnect/recovery | Yes | Yes | Yes (CONNECTION_LOST) |
| Risk gate (EXECUTION_BLOCKED) | Yes | Yes | No |
| Duplicate instance | Yes | Yes | No |
| Critical journal failures | Yes | No | No |
| FORCED_FLATTEN_FAILED | Yes | No | No |

---

## Execution Lifecycle Coverage

### SESSION_FORCED_FLATTEN_* and FORCED_FLATTEN_*

| Event | Source | In LIVE_CRITICAL? | Incident | Alert |
|-------|--------|-------------------|----------|-------|
| FORCED_FLATTEN_TRIGGERED | RobotEngine, JournalStore | No | Would start FORCED_FLATTEN | No (filtered) |
| FORCED_FLATTEN_POSITION_CLOSED | StreamStateMachine | No | Would end FORCED_FLATTEN | No (filtered) |
| SESSION_FORCED_FLATTENED | ExecutionJournal | No | Would end FORCED_FLATTEN | No (filtered) |
| SESSION_FORCED_FLATTEN_TRIGGERED | ExecutionEventFamilies | No | Not in incident recorder | No |
| SESSION_FORCED_FLATTEN_SUBMITTED | ExecutionEventFamilies | No | Not in incident recorder | No |
| FORCED_FLATTEN_FAILED | StreamStateMachine.LogHealth | No | No incident type | No |
| FORCED_FLATTEN_EXPOSURE_REMAINING | StreamStateMachine.LogHealth | No | No | No |
| NO_TRADE_FORCED_FLATTEN_PRE_ENTRY | Slot/stream logic | No | No | No |

### EXECUTION_FILLED, RECONCILIATION_QTY_MISMATCH

| Event | In LIVE_CRITICAL? | Usage | Alert |
|-------|-------------------|-------|-------|
| EXECUTION_FILLED | Yes | Fill tracking, latency spike, pending order clear | No (informational) |
| RECONCILIATION_QTY_MISMATCH | Yes | Start RECONCILIATION_QTY_MISMATCH incident | No (incident never ends) |
| RECONCILIATION_PASS_SUMMARY | No | Would end RECONCILIATION_QTY_MISMATCH | No (filtered) |

### Order Rejection / Mapping Failures

| Event | In LIVE_CRITICAL? | Incident | Alert |
|-------|-------------------|----------|-------|
| ORDER_REJECTED | Yes | No | No |
| EXECUTION_FILL_UNMAPPED | Yes | No | No |
| EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL | Yes | No | No |
| BROKER_FLATTEN_FILL_RECOGNIZED | Yes | No | No |

### Lifecycle States (ENTRY_SUBMITTED, PROTECTIVES_ACTIVE, etc.)

- These are stream state machine states, not discrete events in the feed.
- State transitions are logged as STREAM_STATE_TRANSITION; no dedicated incident/alert rules for lifecycle phases.

---

## Watchdog Incident / Alert Coverage

### Incident Types and Rules

| Incident Type | Start Events | End Events | Rule in alerts.json | Min Duration | Severity | Cooldown |
|---------------|--------------|------------|---------------------|--------------|----------|----------|
| CONNECTION_LOST | CONNECTION_LOST, CONNECTION_LOST_SUSTAINED | CONNECTION_RECOVERED, CONNECTION_RECOVERED_NOTIFICATION | Yes | 60s | critical | 900s |
| ENGINE_STALLED | ENGINE_TICK_STALL_DETECTED | ENGINE_ALIVE, ENGINE_TICK_STALL_RECOVERED, ENGINE_TICK_CALLSITE, ENGINE_TIMER_HEARTBEAT | Yes | 30s | critical | 600s |
| DATA_STALL | DATA_LOSS_DETECTED, DATA_STALL_DETECTED | DATA_STALL_RECOVERED | Yes | 0s | high | 600s |
| FORCED_FLATTEN | FORCED_FLATTEN_TRIGGERED | FORCED_FLATTEN_POSITION_CLOSED, SESSION_FORCED_FLATTENED | Yes | 0s | critical | 300s |
| RECONCILIATION_QTY_MISMATCH | RECONCILIATION_QTY_MISMATCH | RECONCILIATION_PASS_SUMMARY | Yes | 60s | high | 900s |

### Path Verification

| Incident | Start Event in LIVE_CRITICAL? | End Event in LIVE_CRITICAL? | Result |
|----------|------------------------------|----------------------------|--------|
| CONNECTION_LOST | Yes | Yes | **Works** |
| ENGINE_STALLED | Yes | Yes | **Works** |
| DATA_STALL | DATA_LOSS_DETECTED yes; DATA_STALL_DETECTED no (derived) | Yes | **Works** via DATA_LOSS_DETECTED |
| FORCED_FLATTEN | No | No | **Broken** – never detected |
| RECONCILIATION_QTY_MISMATCH | Yes | No | **Broken** – incident never ends |

### Incident Recorder Behavior

- **Active persistence:** `active_incidents.json` loaded on startup, saved on start/end.
- **Severity:** `INCIDENT_SEVERITY` used for dashboard; alert engine uses rule severity.
- **Cooldown:** Per incident type in `alerts.json`.
- **Correlation:** `incident_correlator.tag_incident_record()` sets `primary=False` for cascaded incidents; alert engine skips `primary=False`.
- **Callback:** Wired in aggregator `start()`; invoked from `_write_incident()`.

---

## Compatibility With Recent Changes

| Change | Impact on Notifications |
|--------|-------------------------|
| Timer heartbeat | No impact; ENGINE_TIMER_HEARTBEAT in LIVE_CRITICAL, ends ENGINE_STALLED |
| Safe startup snapshot rebuild | No impact |
| Cursor replay protection | No impact; cursor prevents duplicate processing |
| Atomic cursor writes | No impact |
| Startup invalidation | No impact |
| Age checks on tick and bar events | No impact; correct rejection of stale events |
| Deterministic watchdog state rules | No impact |
| Incident recorder hardening | No impact |
| Active incident persistence | No impact; incident end still triggers callback |
| Metrics history snapshots | No impact |
| Incident severity field | No impact; alert engine uses rule severity |
| Tail-based replay | No impact; cursor + age checks |
| New execution logging fields | No impact |
| Canonical forced flatten events | **Broken** – events not in LIVE_CRITICAL |
| Reconciliation improvements | **Broken** – RECONCILIATION_PASS_SUMMARY not in LIVE_CRITICAL |
| Journal read/write share fixes | No impact on notification path |
| Restart recovery logic | No impact |
| Forced flatten path changes | No impact on notification (events still filtered) |
| Slot expiry and session close logic | No impact |
| Execution lifecycle state machine changes | No impact |
| Duplicate instance / safety protections | No impact; DUPLICATE_INSTANCE_DETECTED has no alert rule |
| Alert-engine / watchdog backend/frontend changes | No breaking changes identified |

---

## Delivery Reliability Audit

### NotificationService (Watchdog)

| Aspect | Implementation | Notes |
|--------|----------------|-------|
| Failure logging | `logger.warning` on Pushover failure | Visible |
| Retries | `retry_count=2`, `retry_delay_seconds=5` | 3 attempts total |
| Swallowed exceptions | Callback: `logger.debug` | Not visible at INFO |
| Config/credentials | `_load_config()`, `_secrets`; `_enabled=False` if missing | Graceful disable |
| Observability | Ledger `delivery_status` (sent/failed) | Limited |
| Network failure | Retries; `pushover_send` returns `(ok, status, err)` | Handled |
| Dedupe | `is_alert_active(dedupe_key)`, `update_active_alert_last_seen` | Yes |
| Cooldown | `min_resend_interval_seconds`; `_last_delivery_by_key` | Yes |
| Queue full | `put_nowait` → `QueueFull` → `logger.warning`, drop | Alert lost |
| Rate limit | `max_alerts_per_hour=30` | Can drop alerts |

### Pushover Client

| Aspect | Implementation |
|--------|----------------|
| Timeout | 12s default |
| Priority 2 | `expire=3600`, `retry=60` |
| Exceptions | `logger.exception` on generic failure |
| Empty credentials | Returns `(False, None, "User key or app token is empty")` |

### Robot HealthMonitor (C#)

- Uses C# NotificationService singleton.
- Failure callback emits `NOTIFICATION_SEND_FAILED` to logs.
- Rate limiting and dedupe in C# layer.

---

## Silent Failure Modes

| Mode | Location | Behavior |
|------|----------|----------|
| Callback exception | `incident_recorder.py:214` | `logger.debug(f"Incident callback error: {e}")` – invisible at INFO |
| NotificationService init failure | `aggregator.py` | `_notification_service = None`; `on_incident` returns early |
| Pushover send failure | `notification_service.py` | Retries; ledger `delivery_status=failed`; no operator-facing alert |
| Queue full | `notification_service.py:169-170` | `logger.warning`, alert dropped |
| Config missing | `notification_service.py` | `_enabled=False`, `raise_alert` returns immediately |
| Alerts config missing | `alert_engine.py` | `_load_config` returns `{}`; no rules |
| Rule missing | `alert_engine.py:71` | `rule = rules.get(incident_type)` → `None`; no alert |
| Primary=False | `alert_engine.py:66-68` | Cascaded incident skipped |
| Cooldown | `alert_engine.py:86` | `logger.debug` – invisible at INFO |
| Event not in LIVE_CRITICAL | `event_feed.py:115` | Event dropped before incident recorder |
| Incident never ends | RECONCILIATION_QTY_MISMATCH | No end event; callback never called |
| Robot-only failures | FORCED_FLATTEN_FAILED, etc. | Never reach watchdog |

---

## End-to-End Notification Traces

### 1. Connection Lost (Working)

```
Robot: OnConnectionStatusUpdate(Lost) → HealthMonitor logs CONNECTION_LOST
    → robot_ENGINE.jsonl
EventFeedGenerator: CONNECTION_LOST in LIVE_CRITICAL → frontend_feed.jsonl
Aggregator → EventProcessor → incident_recorder.process_event
    → INCIDENT_START_EVENTS → _active_incidents["CONNECTION_LOST"]
... later ...
Robot: CONNECTION_RECOVERED
    → same path → INCIDENT_END_EVENTS → pop, _write_incident(record)
    → callback → on_incident → rule match → raise_alert(INCIDENT_CONNECTION_LOST)
    → Queue → pushover_send → Pushover API
```

### 2. Engine Stalled (Working)

```
Robot: ENGINE_TICK_STALL_DETECTED (or watchdog infers from no ticks)
    → frontend_feed.jsonl (ENGINE_TICK_STALL_DETECTED in LIVE_CRITICAL)
IncidentRecorder: Start ENGINE_STALLED
... later ...
Robot: ENGINE_TICK_CALLSITE or ENGINE_ALIVE
    → End ENGINE_STALLED → _write_incident → callback → alert
```

### 3. Data Stall (Working)

```
Robot: DATA_LOSS_DETECTED (HealthMonitor) → robot_ENGINE.jsonl
    → LIVE_CRITICAL → frontend_feed
IncidentRecorder: Start DATA_STALL
... later ...
Robot: DATA_STALL_RECOVERED → End DATA_STALL → alert
```

### 4. Forced Flatten (Broken)

```
Robot: FORCED_FLATTEN_TRIGGERED → robot_ENGINE.jsonl
EventFeedGenerator: FORCED_FLATTEN_TRIGGERED NOT in LIVE_CRITICAL
    → return None → event dropped
Incident never starts. No alert.
```

### 5. Reconciliation Mismatch (Broken)

```
Robot: RECONCILIATION_QTY_MISMATCH → robot_ENGINE.jsonl
    → LIVE_CRITICAL → frontend_feed
IncidentRecorder: Start RECONCILIATION_QTY_MISMATCH
... later ...
Robot: RECONCILIATION_PASS_SUMMARY → NOT in LIVE_CRITICAL → dropped
Incident never ends. Callback never called. No alert.
```

### 6. Robot Critical Execution Failure (Broken)

```
Robot: DUPLICATE_INSTANCE_DETECTED or EXECUTION_POLICY_VALIDATION_FAILED
    → robot_*.jsonl
Both in LIVE_CRITICAL → frontend_feed → event_processor (state update only)
No incident type. No alert rule. No notification.
```

### 7. Session Close / Flatten Failure (Broken)

```
Robot: FORCED_FLATTEN_FAILED (StreamStateMachine.LogHealth)
    → robot_<instrument>.jsonl via RobotLogger
FORCED_FLATTEN_FAILED NOT in LIVE_CRITICAL → dropped
No notification.
```

---

## Coverage Gaps

| Situation | Should Notify? | Current Behavior |
|-----------|----------------|------------------|
| Phantom-flat risk (broker ahead) | **Must** | RECONCILIATION_QTY_MISMATCH incident starts but never ends; no alert |
| Broker ahead / journal behind | **Must** | Same as above |
| Forced flatten failure | **Must** | Event filtered; no alert |
| Forced flatten exposure remaining | **Must** | Event filtered; no alert |
| Repeated order rejections | **Should** | No incident/alert rule |
| Session close failure | **Should** | No event/rule |
| Disconnect without recovery | **Must** | CONNECTION_LOST works if end event eventually arrives; long disconnect may not end incident |
| Engine alive but no execution progress | **Should** | No dedicated alert |
| Stuck orders | **Should** | ORDER_STUCK_DETECTED exists; state-based |
| Journal corruption | **Must** | EXECUTION_JOURNAL_CORRUPTION not in LIVE_CRITICAL |
| Notification outage | **Should** | No self-monitoring; failures only in logs |
| Duplicate instance | **Must** | DUPLICATE_INSTANCE_DETECTED reaches watchdog but no alert |
| Execution policy validation failed | **Must** | Same as above |
| REENTRY_FAILED | **Should** | Not in LIVE_CRITICAL |
| REENTRY_PROTECTION_FAILED | **Should** | Not in LIVE_CRITICAL |

---

## Recommended Fixes

### Critical (Fail-Closed, No Silent Loss)

1. **Add missing event types to LIVE_CRITICAL_EVENT_TYPES** (`modules/watchdog/config.py`):
   - `FORCED_FLATTEN_TRIGGERED`, `FORCED_FLATTEN_POSITION_CLOSED`, `SESSION_FORCED_FLATTENED`
   - `RECONCILIATION_PASS_SUMMARY`
   - `FORCED_FLATTEN_FAILED`, `FORCED_FLATTEN_EXPOSURE_REMAINING`, `REENTRY_FAILED`, `REENTRY_PROTECTION_FAILED`
   - `EXECUTION_JOURNAL_CORRUPTION`, `EXECUTION_JOURNAL_ERROR` (if desired)

2. **Add incident type and rule for FORCED_FLATTEN_FAILED** (`incident_recorder.py`, `alerts.json`):
   - Instant incident (start only, or start+end same event)
   - Rule: `min_duration_seconds: 0`, `severity: critical`

3. **Add alert rules for DUPLICATE_INSTANCE_DETECTED and EXECUTION_POLICY_VALIDATION_FAILED**:
   - Option A: New incident types (instant) + rules in `alerts.json`
   - Option B: Direct `raise_alert` in event_processor when these events are processed

4. **Improve callback error visibility** (`incident_recorder.py:214`):
   - Change `logger.debug` to `logger.warning` for callback errors

### High Priority

5. **Add RECONCILIATION_PASS_SUMMARY to LIVE_CRITICAL** so RECONCILIATION_QTY_MISMATCH incidents can end and alert.

6. **Add ALERT_TYPE_LABELS** for state-based alerts that may lack labels (e.g. RECOVERY_LOOP_DETECTED, FEED_INGESTION_DELAY, ORDER_STUCK_DETECTED).

### Medium Priority

7. **Emit DATA_STALL_DETECTED as derived event** when state manager detects stall, and ensure it reaches incident recorder (or add equivalent path).

8. **Notification self-monitoring:** Emit a detectable event when Pushover fails after retries (e.g. `PUSHOVER_DELIVERY_FAILED`).

---

## Final Verdict

**Can the current push notification system be trusted to alert us for the major robot and watchdog failures that matter in live trading?**

**No.**

Reasons:

1. **FORCED_FLATTEN** – Start/end events are filtered out; no alerts for session forced flatten.
2. **RECONCILIATION_QTY_MISMATCH** – Incidents start but never end; no alerts for position drift.
3. **FORCED_FLATTEN_FAILED** – Event filtered; no alerts when flatten fails.
4. **DUPLICATE_INSTANCE_DETECTED, EXECUTION_POLICY_VALIDATION_FAILED** – Reach watchdog but have no alert rules.
5. **Callback errors** – Logged at DEBUG; not visible at default INFO.
6. **Queue full** – Alerts dropped with only a warning.

**Working paths:** CONNECTION_LOST, ENGINE_STALLED, DATA_STALL, heartbeat lost, orphan, process down, and most state-based alerts.

**Recommendation:** Apply the critical fixes above before relying on push notifications for live trading. Until then, use log tailing (e.g. on CRITICAL_ENGINE_EVENT, FORCED_FLATTEN_FAILED, RECONCILIATION_QTY_MISMATCH) as a backup.
