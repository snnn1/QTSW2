# Watchdog Upgrade Design: Observer → External Supervisor (Phase 1)

**Date**: 2026-03-06  
**Source of Truth**: `docs/watchdog/WATCHDOG_FULL_CAPABILITY_AUDIT_2026_03.md`  
**Scope**: Phase-1 supervision features only. No implementation.

---

## Design Objectives

Enable the Watchdog to **independently detect and alert** on critical robot failures **even when NinjaTrader or the Robot is not running**.

Phase 1 covers exactly four components:

1. Watchdog notification service  
2. NinjaTrader process monitor  
3. Heartbeat-loss alert pipeline  
4. Persistent alert ledger  

---

## Section 1 — Notification Service Design

### 1.1 Module/File Placement

| Item | Path |
|------|------|
| Notification service module | `modules/watchdog/notifications/__init__.py` |
| Notification service class | `modules/watchdog/notifications/notification_service.py` |
| Pushover client | `modules/watchdog/notifications/pushover_client.py` |
| Channel interface (abstract) | `modules/watchdog/notifications/channels.py` |
| Notification config | `configs/watchdog/notifications.json` |
| Secrets (user key, app token) | `configs/watchdog/notifications.secrets.json` (gitignored) |

### 1.2 Service Boundaries

- **NotificationService**: Single entry point for all watchdog alerts. Owns channel selection, retry, and ledger write.
- **Channels**: Pluggable backends (Pushover first). Each channel implements `send(alert: AlertRecord) -> DeliveryResult`.
- **AlertLedger**: Writes to persistent ledger (Section 4). NotificationService calls ledger after each send attempt.
- **Callers**: Aggregator, ProcessMonitor, StateManager (via a thin alert facade). Callers never touch channels directly.

### 1.3 Configuration Model

**`configs/watchdog/notifications.json`** (versioned, committed):

```json
{
  "enabled": true,
  "channels": {
    "pushover": {
      "enabled": true,
      "priority_default": 1,
      "priority_critical": 2,
      "retry_count": 2,
      "retry_delay_seconds": 5,
      "timeout_seconds": 12
    }
  },
  "rate_limits": {
    "min_resend_interval_seconds": 300,
    "max_alerts_per_hour": 20
  }
}
```

**`configs/watchdog/notifications.secrets.json`** (gitignored):

```json
{
  "pushover": {
    "user_key": "<redacted>",
    "app_token": "<redacted>"
  }
}
```

### 1.4 Supported Channels (Phase 1)

| Channel | Priority | Notes |
|---------|----------|-------|
| **Pushover** | First | Same API as Robot's PushoverClient; reuse endpoint `https://api.pushover.net/1/messages.json` |

No Slack, email, or SMS in Phase 1.

### 1.5 Message Formatting

**Title**: `[Watchdog] {alert_type_human}`  
Example: `[Watchdog] NinjaTrader Process Stopped`

**Message body** (plain text, max ~1024 chars for Pushover):

```
{severity}: {alert_type_human}
Context: {context_json_or_summary}
First seen: {first_seen_chicago}
Last seen: {last_seen_utc}
```

Context fields are alert-specific (Section 5).

### 1.6 Severity Mapping

| Severity | Pushover Priority | Bypasses Quiet Hours |
|----------|-------------------|----------------------|
| `critical` | 2 | Yes |
| `warning` | 1 | Yes |
| `info` | 0 | No |

Phase-1 alerts use `critical` or `warning` only.

### 1.7 Retry Behavior

- **Retries**: 2 retries (configurable), 5 seconds apart.
- **Timeout**: 12 seconds per request (match Robot's PushoverClient).
- **On failure**: Log error, write `delivery_status: failed` to ledger, do not block aggregator loop.

### 1.8 Failure Handling

- Notification send is **non-blocking**. Failures are logged and recorded in the ledger.
- No exception propagation to callers. Service catches all channel exceptions.
- If Pushover is unreachable: log, mark delivery failed, continue. No retry loop beyond configured retries.

### 1.9 Synchronous vs Queued

**Queued (recommended)**:

- In-memory queue (e.g., `asyncio.Queue` or `queue.Queue`) with a single worker task.
- Callers enqueue alerts; worker dequeues, sends, writes ledger.
- Prevents notification I/O from blocking the 1-second aggregator loop.
- Queue max size: 100. If full, drop oldest (log warning).

### 1.10 Interface for Callers

```python
# modules/watchdog/notifications/notification_service.py

def raise_alert(
    alert_type: str,
    severity: str,
    context: dict,
    dedupe_key: str,
    min_resend_interval_seconds: int = 300,
) -> None:
    """
    Enqueue an alert for delivery. Non-blocking.
    Dedupe and rate limiting applied before enqueue.
    """
```

Callers use `raise_alert()`. No direct channel access.

---

## Section 2 — Process Monitor Design

### 2.1 Process Discovery on Windows

- Use **psutil** (`psutil.process_iter()` or `psutil.Process`).
- Filter by process name: `NinjaTrader.exe` (case-insensitive).
- No dependency on NinjaTrader install path; discovery is name-based.

### 2.2 Multiple NinjaTrader Instances

| Scenario | Behavior |
|----------|----------|
| 0 instances | Process **missing** → alert if in supervision window |
| 1 instance | Process **up** → normal |
| 2+ instances | Process **up** (at least one running). Log warning: "Multiple NinjaTrader instances detected". Do not alert process-down. Optionally future: alert duplicate-instance. |

Phase 1: "Process up" = at least one `NinjaTrader.exe` running.

### 2.3 Supervision Window Logic

Alerts fire **only when**:

1. **Market open** (`is_market_open(chicago_now)` from `market_session.py`), OR
2. **Active intents** (execution journals with EntryFilled && !TradeCompleted), OR
3. **Recent engine activity** (ENGINE_TICK_CALLSITE or ENGINE_START in last 2 hours)

Otherwise: **suppressed** (supervision window closed). No alert when market closed and no positions.

### 2.4 Polling Interval

- **Interval**: 30 seconds (configurable).
- **Rationale**: Process state changes are rare; 30s balances responsiveness and CPU use.
- **Placement**: Dedicated loop in aggregator (`_process_monitor_loop`), similar to `_poll_timetable_loop`.

### 2.5 State Transitions

| State | Condition | Next States |
|-------|-----------|-------------|
| **PROCESS_UP** | At least one NinjaTrader.exe running | PROCESS_MISSING |
| **PROCESS_MISSING** | No NinjaTrader.exe running | PROCESS_UP (restored) |
| **PROCESS_RESTORED** | Was PROCESS_MISSING, now PROCESS_UP | — (transient; used for resolution) |

### 2.6 Alert Conditions

| Event | Condition | Supervision Window |
|-------|-----------|---------------------|
| **Process down** | Transition to PROCESS_MISSING | Must be in window |
| **Process restored** | Transition to PROCESS_UP after PROCESS_MISSING | Resolves active alert |

### 2.7 Recovery Conditions

- **Resolved**: Process seen again (PROCESS_UP) after PROCESS_MISSING.
- **Recovery grace**: None. First sight of process = resolved.

### 2.8 Module Placement

| Item | Path |
|------|------|
| Process monitor | `modules/watchdog/process_monitor.py` |
| Class | `ProcessMonitor` |

---

## Section 3 — Heartbeat-Loss Alert Design

### 3.1 Source Events

- **Primary**: `ENGINE_TICK_CALLSITE` (rate-limited to 5s in feed)
- **Fallback**: `ENGINE_ALIVE`, `ENGINE_TICK_HEARTBEAT`

Same as current `compute_engine_alive()` in state manager. No new event types.

### 3.2 Freshness Thresholds

Reuse existing config:

- `ENGINE_TICK_STALL_THRESHOLD_SECONDS` = 60
- `ENGINE_TICK_STALL_HYSTERESIS_SECONDS` = 15

**Active (heartbeat lost)**: No tick for `60 + 15 = 75` seconds when currently alive; or no tick for 60s when recovering from dead.

**Resolved**: Tick received within 60s of last tick.

### 3.3 Hysteresis

- **Alive → Dead**: Require 75s of no ticks (60 + 15).
- **Dead → Alive**: Require tick within 60s.
- Prevents flicker from brief gaps.

### 3.4 When Alert First Becomes Active

- State manager already computes `engine_alive=False`.
- **New**: When `engine_alive` transitions from True to False **and** supervision window is open → enqueue `ROBOT_HEARTBEAT_LOST` alert.

### 3.5 When Alert Resolves

- When `engine_alive` transitions from False to True → resolve alert in ledger.
- No new notification on resolve (optional: future "Heartbeat Restored" info notification).

### 3.6 Difference from Process-Down Alert

| Aspect | Heartbeat Lost | Process Down |
|--------|----------------|--------------|
| **Signal** | No ENGINE_TICK_CALLSITE in logs | No NinjaTrader.exe process |
| **Implies** | Robot may be frozen, deadlocked, or not running | NinjaTrader process definitely not running |
| **Can co-occur** | Yes. Process down → no ticks → both fire. Dedupe by not sending heartbeat alert if process-down already active for same incident. |
| **Recovery** | New tick in feed | Process seen again |

### 3.7 Duplicate Avoidance

- **Dedupe key**: `ROBOT_HEARTBEAT_LOST` (no run_id in key; one active heartbeat alert at a time).
- **Resend**: Min 5 min between resends for same alert type.
- **Co-occurrence**: If `NINJATRADER_PROCESS_STOPPED` is active and unresolved, suppress `ROBOT_HEARTBEAT_LOST` (heartbeat loss is implied by process down).

### 3.8 File Inactivity vs Heartbeat Loss

| Signal | Definition | Separate? |
|--------|------------|-----------|
| **Heartbeat loss** | No ENGINE_TICK_CALLSITE/ENGINE_ALIVE in processed events | Primary |
| **File inactivity** | `robot_ENGINE.jsonl` mtime/size unchanged for N seconds | **No** in Phase 1 |

**Rationale**: File inactivity is a proxy for heartbeat loss. Adding it would duplicate alerts. Phase 1 uses heartbeat loss only. File inactivity can be a Phase 2 enhancement if needed.

---

## Section 4 — Persistent Alert Ledger

### 4.1 Storage Format

- **Format**: JSONL (one JSON object per line).
- **Append-only** for new alerts; **update-in-place** for resolution/delivery status (see 4.6).

### 4.2 File Path

- **Path**: `data/watchdog/alert_ledger.jsonl`
- **Directory**: `data/watchdog/` (create if missing).

### 4.3 Schema (per record)

```json
{
  "alert_id": "uuid-v4",
  "alert_type": "NINJATRADER_PROCESS_STOPPED",
  "severity": "critical",
  "first_seen_utc": "2026-03-06T14:30:00.000Z",
  "last_seen_utc": "2026-03-06T14:35:00.000Z",
  "active": true,
  "resolved_utc": null,
  "dedupe_key": "NINJATRADER_PROCESS_STOPPED",
  "context": {
    "market_open": true,
    "active_intent_count": 1
  },
  "delivery_status": "sent",
  "delivery_channel": "pushover",
  "delivery_attempts": 1,
  "last_delivery_utc": "2026-03-06T14:30:05.000Z"
}
```

### 4.4 Lifecycle Fields

| Field | Type | Purpose |
|-------|------|---------|
| `alert_id` | string (UUID) | Unique record id |
| `alert_type` | string | Alert type constant |
| `severity` | string | critical, warning, info |
| `first_seen_utc` | ISO8601 | When condition first detected |
| `last_seen_utc` | ISO8601 | Last time condition was observed |
| `active` | bool | True until resolved |
| `resolved_utc` | ISO8601 or null | When condition cleared |
| `dedupe_key` | string | For deduplication |
| `context` | object | Alert-specific context |
| `delivery_status` | string | pending, sent, failed |
| `delivery_channel` | string | pushover, etc. |
| `delivery_attempts` | int | Number of send attempts |
| `last_delivery_utc` | ISO8601 or null | Last send attempt |

### 4.5 Append/Update Strategy

- **New alert**: Append new line to file. One write per new alert.
- **Update (resolve, delivery status)**: Read file, find line by `alert_id`, rewrite that line with updated fields, write back. To avoid full-file rewrite: maintain an index file `data/watchdog/alert_ledger.idx` mapping `alert_id` → byte offset, or use a simple "last N alerts in memory" cache and append-only file for history.
- **Simplified Phase 1**: Append-only. Resolved alerts get a new "resolution" record: `{"event":"resolved","alert_id":"...","resolved_utc":"..."}`. Active alerts are tracked in-memory; ledger is append-only for audit trail. Resolution record links to original by `alert_id`.

### 4.6 Retention Strategy

- **Retention**: Keep last 7 days of records (configurable).
- **Rotation**: Daily or when file exceeds 10 MB. Rotate to `alert_ledger_YYYYMMDD.jsonl`.
- **Phase 1**: Simple retention: trim lines older than 7 days when writing. No rotation in Phase 1 if file stays small.

### 4.7 Resolved Alerts

- Write resolution record: `{"event":"resolved","alert_id":"...","resolved_utc":"...","alert_type":"..."}`.
- In-memory state: remove from active set.

### 4.8 UI/API Support

- **API**: `GET /api/watchdog/alerts?active=true&since=...` reads ledger, filters by `active` and `first_seen_utc`.
- **UI**: Alert history panel, active alerts banner.

### 4.9 Module Placement

| Item | Path |
|------|------|
| Ledger module | `modules/watchdog/alert_ledger.py` |
| Class | `AlertLedger` |

---

## Section 5 — Alert Model (Phase 1)

### 5.1 Alert Definitions

| Alert Type | Trigger Condition | Clear Condition | Severity | Min Resend | Dedupe Key | Context Fields |
|------------|-------------------|-----------------|----------|------------|-------------|----------------|
| **NINJATRADER_PROCESS_STOPPED** | No NinjaTrader.exe, supervision window open | Process seen again | critical | 300 | `NINJATRADER_PROCESS_STOPPED` | market_open, active_intent_count |
| **ROBOT_HEARTBEAT_LOST** | engine_alive=False for threshold, supervision window open | engine_alive=True | critical | 300 | `ROBOT_HEARTBEAT_LOST` | last_tick_utc, market_open |
| **CONNECTION_LOST_SUSTAINED** | connection_status=ConnectionLost for ≥60s, supervision window open | connection_status=Connected | warning | 300 | `CONNECTION_LOST_SUSTAINED` | connection_name, elapsed_seconds |
| **ENGINE_TICK_STALL** | Same as ROBOT_HEARTBEAT_LOST (alias for event-driven case) | engine_alive=True | critical | 300 | `ENGINE_TICK_STALL` | last_tick_utc, threshold_seconds |
| **POTENTIAL_ORPHAN_POSITION** | Process down + active intents from journals | Process up OR intents closed | critical | 300 | `POTENTIAL_ORPHAN_POSITION` | active_intent_count, intent_ids |

### 5.2 Notes

- **ENGINE_TICK_STALL** vs **ROBOT_HEARTBEAT_LOST**: Same condition (no ticks). Use one: `ROBOT_HEARTBEAT_LOST`. ENGINE_TICK_STALL can be an alias or omitted in Phase 1.
- **CONNECTION_LOST_SUSTAINED**: Robot emits CONNECTION_LOST_SUSTAINED after 60s. Watchdog observes via event. Alert when event received and supervision window open.
- **POTENTIAL_ORPHAN_POSITION**: Process down + `hydrate_intent_exposures_from_journals` shows EntryFilled && !TradeCompleted. Requires process monitor + journal check.

---

## Section 6 — State Machine

### 6.1 States

| State | Meaning |
|-------|---------|
| **inactive** | Condition not present |
| **active** | Condition present, alert raised, may be sent or pending |
| **suppressed** | Condition present but supervision window closed |
| **resolved** | Condition cleared |

### 6.2 Transitions

```
inactive --[condition detected, window open]--> active
inactive --[condition detected, window closed]--> suppressed
active --[condition cleared]--> resolved
active --[window closed]--> suppressed (optional: keep active, don't resend)
suppressed --[window open]--> active
suppressed --[condition cleared]--> resolved
resolved --[condition detected again]--> active (new incident)
```

### 6.3 Dedupe and Resend

- **Dedupe**: One active alert per `dedupe_key`. New event for same key updates `last_seen_utc` and context; does not create new alert.
- **Resend**: If alert remains active and `min_resend_interval_seconds` elapsed since `last_delivery_utc`, send again. Update `last_delivery_utc`.
- **Resolved**: Clear from active set. Next occurrence = new alert.

---

## Section 7 — Integration Points

### 7.1 Aggregator Loop

| Change | Location | Behavior |
|--------|----------|----------|
| New loop | `WatchdogAggregator._process_monitor_loop()` | Poll process every 30s; call `ProcessMonitor.check()`; raise/resolve process alerts |
| Alert check | `WatchdogAggregator._check_alert_conditions()` | After `_process_feed_events_sync`, read state manager; if engine_alive=False and window open, raise ROBOT_HEARTBEAT_LOST |
| Startup | `WatchdogAggregator.start()` | Start `_process_monitor_loop`, ensure NotificationService and AlertLedger initialized |

### 7.2 Event Processor

| Change | Location | Behavior |
|--------|----------|----------|
| **None** | — | EventProcessor remains read-only. It updates state; it does not raise alerts. |

### 7.3 State Manager

| Change | Location | Behavior |
|--------|----------|----------|
| **None** | — | StateManager remains read-only. It computes engine_alive, connection_status, etc. It does not raise alerts. |
| **Expose** | `compute_watchdog_status()` | Already returns engine_alive, connection_status. Alert logic reads this. |

### 7.4 Backend API

| Change | Location | Behavior |
|--------|----------|----------|
| New endpoint | `routers/watchdog.py` | `GET /api/watchdog/alerts` — return active alerts and recent history from ledger |
| Status extension | `get_watchdog_status()` | Optionally add `active_alerts: [...]` to status response |

### 7.5 Config

| Change | Location | Behavior |
|--------|----------|----------|
| New config | `configs/watchdog/notifications.json` | Notification service config |
| New secrets | `configs/watchdog/notifications.secrets.json` | Pushover credentials |
| Config extension | `modules/watchdog/config.py` | Add `ALERT_LEDGER_PATH`, `PROCESS_MONITOR_INTERVAL_SECONDS`, `SUPERVISION_WINDOW_*` |

### 7.6 Read-Only vs Alerting

| Component | Role |
|-----------|------|
| EventProcessor | Read-only: process events, update state |
| StateManager | Read-only: compute derived state |
| EventFeedGenerator | Read-only: ingest logs |
| Aggregator | **Alerting**: calls ProcessMonitor, checks conditions, calls NotificationService.raise_alert |
| ProcessMonitor | **Alerting**: detects process state, raises process/orphan alerts |
| NotificationService | **Alerting**: sends notifications, writes ledger |

---

## Section 8 — Operational Safeguards

### 8.1 Supervision Windows

| Window | Condition | Alerts Affected |
|--------|-----------|-----------------|
| Market open | `is_market_open(chicago_now)` | All |
| Active intents | Journals with EntryFilled && !TradeCompleted | Process, Orphan, Heartbeat |
| Recent activity | ENGINE_TICK_CALLSITE or ENGINE_START in last 2 hours | Heartbeat, Connection |

At least one must be true for alerts to fire.

### 8.2 Rate Limiting

| Limit | Value | Scope |
|-------|-------|-------|
| Min resend interval | 300s (5 min) | Per alert type |
| Max alerts per hour | 20 | Global (configurable) |
| Queue size | 100 | Drop oldest if full |

### 8.3 Dedupe

| Key | Dedupe Key | Notes |
|-----|------------|-------|
| Process stopped | `NINJATRADER_PROCESS_STOPPED` | One active at a time |
| Heartbeat lost | `ROBOT_HEARTBEAT_LOST` | One active; suppressed if process stopped |
| Connection lost | `CONNECTION_LOST_SUSTAINED` | One active |
| Orphan | `POTENTIAL_ORPHAN_POSITION` | One active |

### 8.4 Startup Grace Period

| Period | Value | Purpose |
|--------|-------|---------|
| Watchdog startup | 120 seconds | No alerts in first 2 min after watchdog start (allow state to stabilize) |
| Process monitor | 60 seconds | No process-down alert in first 60s (allow first poll) |

### 8.5 Recovery Grace Period

| Scenario | Grace | Purpose |
|----------|-------|---------|
| Process restored | 0 | Resolve immediately |
| Heartbeat restored | 0 | Resolve immediately |
| Connection restored | 0 | Resolve immediately |

No additional recovery grace in Phase 1.

---

## Section 9 — Final Output

### 9.1 Architecture Summary

```
┌─────────────────────────────────────────────────────────────────┐
│                     Watchdog Aggregator                          │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────────┐  │
│  │ Event Loop   │  │ Process      │  │ Timetable Loop        │  │
│  │ (1s)         │  │ Monitor (30s)│  │ (60s)                 │  │
│  └──────┬───────┘  └──────┬───────┘  └──────────────────────┘  │
│         │                  │                                     │
│         ▼                  ▼                                     │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              Alert Condition Checker                      │   │
│  │  (engine_alive, connection_status, process state,        │   │
│  │   active intents, supervision window)                     │   │
│  └──────────────────────────┬───────────────────────────────┘   │
│                             │                                    │
│                             ▼                                    │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              NotificationService.raise_alert()             │   │
│  │  (dedupe, rate limit, enqueue)                            │   │
│  └──────────────────────────┬───────────────────────────────┘   │
└─────────────────────────────┼────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Notification Worker (async)  │  AlertLedger                     │
│  - Dequeue alert             │  - Append new                    │
│  - PushoverClient.send()      │  - Write resolution             │
│  - Update ledger             │  - Retention                     │
└─────────────────────────────────────────────────────────────────┘
```

### 9.2 File/Module Change Plan

| Action | Path |
|--------|------|
| **Add** | `modules/watchdog/notifications/__init__.py` |
| **Add** | `modules/watchdog/notifications/notification_service.py` |
| **Add** | `modules/watchdog/notifications/pushover_client.py` |
| **Add** | `modules/watchdog/notifications/channels.py` |
| **Add** | `modules/watchdog/process_monitor.py` |
| **Add** | `modules/watchdog/alert_ledger.py` |
| **Add** | `configs/watchdog/notifications.json` |
| **Add** | `configs/watchdog/notifications.secrets.json.example` |
| **Modify** | `modules/watchdog/aggregator.py` — add process monitor loop, alert check, wire NotificationService |
| **Modify** | `modules/watchdog/config.py` — add alert/notification config paths and constants |
| **Modify** | `modules/watchdog/backend/main.py` — ensure NotificationService, AlertLedger initialized in lifespan |
| **Modify** | `modules/watchdog/backend/routers/watchdog.py` — add GET /api/watchdog/alerts |
| **Add** | `data/watchdog/` directory (for alert_ledger.jsonl) |

### 9.3 Phase-1 Implementation Sequence (Dependency Order)

1. **AlertLedger** — No deps. Implement append, resolution, retention.
2. **PushoverClient** — No deps. HTTP client for Pushover API.
3. **NotificationService** — Deps: PushoverClient, AlertLedger, config. Implement queue, worker, raise_alert.
4. **ProcessMonitor** — Deps: psutil, config, market_session. Implement check(), supervision window.
5. **Config** — Add notification and process monitor constants.
6. **Aggregator integration** — Wire ProcessMonitor loop, alert condition check, NotificationService.raise_alert.
7. **API** — Add /alerts endpoint, read from ledger.
8. **Secrets and config files** — Create example, document setup.

### 9.4 Major Risks and Edge Cases

| Risk | Mitigation |
|------|------------|
| **Pushover rate limits** | Respect min resend interval; cap alerts per hour |
| **Multiple NinjaTrader instances** | Treat "any running" as up; log warning |
| **Watchdog restart during active alert** | ✅ Phase 2: Ledger rehydration on startup. `AlertLedger._rehydrate_active_alerts()` restores unresolved alerts from ledger. |
| **False positive: market closed** | Supervision window requires market open OR active intents OR recent activity |
| **Process name change** | Configurable process name (default NinjaTrader.exe) |
| **Ledger file corruption** | Append-only; corruption only affects new writes. Add optional checksum/validation. |
| **Notification worker crash** | Worker restarts on exception; queue may lose items. Log and continue. |
| **Co-occurring process down + heartbeat loss** | Suppress heartbeat alert when process down active |

---

## Appendix A — Alert Type Constants

```python
# Suggested constants
ALERT_NINJATRADER_PROCESS_STOPPED = "NINJATRADER_PROCESS_STOPPED"
ALERT_ROBOT_HEARTBEAT_LOST = "ROBOT_HEARTBEAT_LOST"
ALERT_CONNECTION_LOST_SUSTAINED = "CONNECTION_LOST_SUSTAINED"
ALERT_ENGINE_TICK_STALL = "ENGINE_TICK_STALL"  # Alias for heartbeat lost
ALERT_POTENTIAL_ORPHAN_POSITION = "POTENTIAL_ORPHAN_POSITION"
```

## Appendix B — Supervision Window Helper

```python
def is_supervision_window_open(
    market_open: bool,
    active_intent_count: int,
    last_engine_tick_utc: Optional[datetime],
    now_utc: datetime,
) -> bool:
    """True if any condition suggests robot should be supervised."""
    if market_open:
        return True
    if active_intent_count > 0:
        return True
    if last_engine_tick_utc and (now_utc - last_engine_tick_utc).total_seconds() < 7200:  # 2 hours
        return True
    return False
```

---

## Appendix C — Phase 1 Setup (Post-Implementation)

### C.1 Pushover Setup

1. Copy `configs/watchdog/notifications.secrets.json.example` to `configs/watchdog/notifications.secrets.json`.
2. Add your Pushover `user_key` and `app_token` (from [pushover.net](https://pushover.net)).

### C.2 Optional: Process Monitor

```bash
pip install -r modules/watchdog/requirements.txt
```

Without psutil, process monitoring degrades gracefully (no NinjaTrader.exe detection).

### C.3 Run and Test

- Start the watchdog backend.
- Hit `GET /api/watchdog/alerts` with optional `active_only`, `since_hours`, `limit`.
- Status endpoint now includes `active_alerts` in the response.

### C.4 Post-Phase 1 Additions (Implemented)

| Feature | Description |
|---------|-------------|
| **Phase 2: Rehydrate** | `AlertLedger._rehydrate_active_alerts()` restores unresolved alerts on startup |
| **Restored notifications** | Optional "Heartbeat Restored", "Connection Restored", "NinjaTrader Process Restored" (config: `restored_notifications.enabled`) |
| **Alerts history UI** | `AlertsHistoryCard` shows last 24h from ledger; polls every 30s |
| **Unit tests** | `tests/test_watchdog_phase1.py` — AlertLedger, ProcessMonitor, NotificationService |
