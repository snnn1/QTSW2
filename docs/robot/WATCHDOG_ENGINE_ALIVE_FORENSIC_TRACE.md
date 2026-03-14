# Watchdog ENGINE ALIVE / BROKER CONNECTED Forensic Trace

**Task:** Find every code path that can cause `engine_alive = True` and `connection_status = "Connected"` when NinjaTrader is open but the strategy is NOT enabled.

**Scope:** Current implementation after all recent liveness, invalidation, and connection-state fixes.

---

## Section 1 — Engine Alive Truth Path

### All ways `_last_engine_heartbeat` can be set

The only setter is `update_engine_tick(timestamp_utc)` in `state_manager.py`, which does:
```python
self._last_engine_heartbeat = now  # datetime.now(timezone.utc)
```

### All callers of `update_engine_tick()`

| File | Function | Event Type | Can happen when strategy disabled? |
|------|----------|------------|------------------------------------|
| `event_processor.py` | `process_event` | ENGINE_START | No – strategy only |
| `event_processor.py` | `process_event` | ENGINE_HEARTBEAT | No – strategy only |
| `event_processor.py` | `process_event` | ENGINE_TICK_HEARTBEAT | No – strategy only |
| `event_processor.py` | `process_event` | **ONBARUPDATE_CALLED** | **YES – see Section 7** |
| `event_processor.py` | `process_event` | ENGINE_TICK_CALLSITE | No – strategy only |
| `event_processor.py` | `process_event` | ENGINE_ALIVE | No – strategy only |
| `event_processor.py` | `process_event` | ENGINE_TICK_STALL_RECOVERED | No – strategy only |

### Bootstrap/init logic that sets engine alive indirectly

| File | Function | Trigger | Age/invalidation check? |
|------|----------|---------|--------------------------|
| `aggregator.py` | `start()` | Startup: ticks from snapshot | Yes: `age_sec <= 15` |
| `aggregator.py` | `start()` | Startup: **bar events** (ONBARUPDATE_CALLED, BAR_ACCEPTED, BAR_RECEIVED_NO_STREAMS) | **NO** |
| `aggregator.py` | `_process_feed_events_sync()` | Every cycle: ticks from tail | Yes: age ≤15/60, `_last_invalidate_utc` |
| `aggregator.py` | `_process_feed_events_sync()` | Every cycle: **bar events from tail** | **NO** |

### Event types that can currently mark the engine alive

1. **ENGINE_TICK_CALLSITE** – guarded by age + invalidation in tail path
2. **ENGINE_ALIVE** – guarded by age + invalidation in tail path
3. **ONBARUPDATE_CALLED** – **no guards** – processed every cycle from tail
4. **ENGINE_TICK_HEARTBEAT** – no guards in bar-extraction path
5. **BAR_ACCEPTED** – **no guards** – processed every cycle from tail
6. **BAR_RECEIVED_NO_STREAMS** – **no guards** – processed every cycle from tail
7. ENGINE_START, ENGINE_HEARTBEAT, ENGINE_TICK_STALL_RECOVERED – strategy-only, no tail path

---

## Section 2 — Connection Connected Truth Path

### All ways `_connection_status` can become "Connected"

| File | Function | Condition | Depends on |
|------|----------|-----------|------------|
| `state_manager.py` | `update_connection_status` | Explicit CONNECTION_RECOVERED, CONNECTION_RECOVERED_NOTIFICATION, CONNECTION_CONFIRMED | Connection events |
| `state_manager.py` | `compute_watchdog_status` | `connection_status == "Unknown" and engine_alive` | **engine_alive** |
| `state_manager.py` | `compute_watchdog_status` | `connection_status == "ConnectionLost" and engine_alive` → auto-clear to Connected | engine_alive |
| `aggregator.py` | `get_watchdog_status` | Override when `ninja_running` and status was Connected | Process state (only when process DOWN) |

### Override logic

- When `engine_alive` is False: `connection_status` is forced to `"Unknown"` (state_manager line 1774–1775).
- When NinjaTrader process is not running: status overridden to False/Unknown (aggregator line 1825–1832).

### Conclusion for connection_status

BROKER CONNECTED goes green when `engine_alive` is True. It is derived from engine liveness. The root cause of both indicators is `engine_alive`.

---

## Section 3 — Startup/Bootstrap Path

### Watchdog startup behavior

1. **`aggregator.start()`** (lines 355–444):
   - Reads last `TAIL_LINE_COUNT` (3000) lines from `frontend_feed.jsonl`
   - Builds `startup_snapshot` (all parsed events)
   - `_rebuild_connection_status_from_snapshot(startup_snapshot)` – connection from CONNECTION_* events
   - **Init engine tick:** filters `ENGINE_TICK_CALLSITE`, `ENGINE_ALIVE` only; applies `age_sec <= 15`; rejects stale
   - **Init bar tracking:** filters `BAR_RECEIVED_NO_STREAMS`, `BAR_ACCEPTED`, `ONBARUPDATE_CALLED`; processes up to 50 bars with **no age or invalidation check**

2. **`_last_invalidate_utc`**:
   - Set only in `invalidate_engine_liveness()` when NinjaTrader process is not running
   - On fresh watchdog start, `_last_invalidate_utc` is unset (None)
   - Startup init does not use `_last_invalidate_utc`

3. **Feed tail on startup**:
   - Contains events from previous strategy runs
   - Old bar events (ONBARUPDATE_CALLED, BAR_ACCEPTED, BAR_RECEIVED_NO_STREAMS) are processed
   - Each calls `update_engine_tick` → `_last_engine_heartbeat = now`

### Step-by-step: watchdog running → NinjaTrader opens → UI turns green

1. Watchdog is running; `_process_feed_events_sync` runs every cycle.
2. User had strategy running earlier; tail has old bar events.
3. User closes NinjaTrader (or disables strategy). Next `get_watchdog_status` sees `ninja_running=False` → `invalidate_engine_liveness()` → clears heartbeat, sets `_last_invalidate_utc`.
4. User opens NinjaTrader at login. Next poll: `ninja_running=True` → no override.
5. **Same cycle or next:** `_process_feed_events_sync` runs:
   - Reads tail (same old events)
   - **Bar extraction** (lines 1259–1265):
     ```python
     bar_types = ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED")
     bars = [e for e in reversed(parsed_events) if e.get("event_type") in bar_types][:10]
     for bar_ev in bars:
         self._event_processor.process_event(bar_ev)
     ```
   - No `tick_age_sec`, no `_last_invalidate_utc`, no `ENGINE_TICK_MAX_AGE` check
   - Each bar event → `process_event` → `update_engine_tick(timestamp_utc)` → `_last_engine_heartbeat = now`
6. `compute_engine_alive()` sees fresh heartbeat → `engine_alive = True`.
7. `connection_status` inferred as `"Connected"` when `engine_alive` and status was `"Unknown"`.
8. UI shows ENGINE ALIVE and BROKER CONNECTED green.

### Events written during NinjaTrader startup (without strategy)

- No new events. Strategy does not run; robot does not write.
- Tail still has old events from the last strategy run.

### Bypass of invalidation

- Tick path (ENGINE_TICK_CALLSITE, ENGINE_ALIVE): respects `_last_invalidate_utc` and age.
- **Bar path: does not check `_last_invalidate_utc` or event age.** Stale bar events refresh the heartbeat every cycle.

---

## Section 4 — Event Type Audit

### Events that influence engine liveness

| Event Type | Source | Can appear at NinjaTrader startup without strategy? | Should affect engine_alive? |
|------------|--------|------------------------------------------------------|-----------------------------|
| ENGINE_TICK_CALLSITE | Strategy (RobotEngine Tick()) | No | Yes (when strategy running) |
| ENGINE_ALIVE | Strategy (RobotSimStrategy) | No | Yes (when strategy running) |
| ONBARUPDATE_CALLED | Strategy (OnBarUpdate) | No | Yes (when strategy running) |
| BAR_ACCEPTED | RobotEngine | No | Yes (when strategy running) |
| BAR_RECEIVED_NO_STREAMS | RobotEngine | No | Yes (when strategy running) |
| ENGINE_TICK_HEARTBEAT | Strategy | No | Yes (when strategy running) |
| ENGINE_START | Strategy | No | Yes (when strategy starts) |

All of these come from the robot/strategy. None are emitted when the strategy is disabled.

### Events that influence connection_status

| Event Type | Source | Can appear without strategy? | Should affect connection_status? |
|------------|--------|------------------------------|----------------------------------|
| CONNECTION_RECOVERED | Strategy/broker | No | Yes |
| CONNECTION_RECOVERED_NOTIFICATION | Strategy | No | Yes |
| CONNECTION_CONFIRMED | Strategy | No | Yes |
| CONNECTION_LOST | Strategy | No | Yes |

### Wrapper/adapter startup events

- No separate adapter startup events found that mark engine alive.
- Feed is populated by EventFeedGenerator from robot logs; no strategy ⇒ no new log entries.

---

## Section 5 — Process-State Coupling Audit

### Does any path do "NinjaTrader process running → engine_alive true"?

**No direct coupling.** There is no code that sets `engine_alive = True` solely because the process is running.

**Indirect coupling:** When the process is running, we do not override. We use `compute_watchdog_status()`, which derives `engine_alive` from `_last_engine_heartbeat`. The heartbeat is refreshed by processing events from the tail. The bar-event path has no guards, so **stale bar events refresh the heartbeat every cycle** while the process is running.

### Does any path do "NinjaTrader process running → connection_status connected"?

**Indirectly.** `connection_status` is inferred as `"Connected"` when `engine_alive` is True. So the same bar-event path that keeps `engine_alive` True also drives BROKER CONNECTED.

### Proof

- `get_watchdog_status` (aggregator 1812–1832): overrides only when `not ninja_running`.
- When `ninja_running` is True, status comes from `compute_watchdog_status()`.
- `engine_alive` comes from `compute_engine_alive()` → `heartbeat = _last_engine_heartbeat or _last_engine_tick_utc`.
- `_last_engine_heartbeat` is set only by `update_engine_tick()`, which is called from `process_event()` for the event types listed in Section 1.

---

## Section 6 — Feed Contamination Audit

### Can frontend_feed.jsonl receive new events during NinjaTrader startup without strategy?

**No.** EventFeedGenerator reads robot log files. The robot writes only when the strategy is running. At login, no strategy ⇒ no new events.

### EventFeedGenerator filtering

- Uses `LIVE_CRITICAL_EVENT_TYPES`; bar events are included.
- Requires `run_id`; events without it are dropped.
- Rate-limits ENGINE_TICK_CALLSITE (5s) and BAR_RECEIVED_NO_STREAMS (60s).

### run_id / event_seq

- Old events in the tail keep their original `run_id` and `event_seq`.
- Cursor logic uses `event_seq > last_seq` for cursor_events; bar events are always included via `is_bar(ev)`.
- Bar extraction (lines 1259–1265) does not use cursor; it processes the 10 most recent bar events every cycle regardless of run_id or event_seq.

### Events from another component/strategy

- Feed is from robot logs only. No other component writes to it.
- Multiple strategies would use different run_ids; bar events are not filtered by run_id in the bar-extraction path.

---

## Section 7 — Final Root Cause

### Exact code path causing ENGINE ALIVE to go green

**File:** `modules/watchdog/aggregator.py`  
**Function:** `_process_feed_events_sync`  
**Lines:** 1259–1265

```python
bar_types = ("BAR_RECEIVED_NO_STREAMS", "BAR_ACCEPTED", "ONBARUPDATE_CALLED")
bars = [e for e in reversed(parsed_events) if e.get("event_type") in bar_types][:10]
for bar_ev in bars:
    self._event_processor.process_event(bar_ev)
```

**Flow:**
1. Every ingestion cycle reads the tail of `frontend_feed.jsonl`.
2. The 10 most recent bar events are extracted (no age or invalidation check).
3. Each is passed to `process_event()`.
4. For ONBARUPDATE_CALLED, BAR_ACCEPTED, BAR_RECEIVED_NO_STREAMS, `event_processor.py` calls `update_engine_tick(timestamp_utc)`.
5. `update_engine_tick` sets `_last_engine_heartbeat = now`.
6. `compute_engine_alive()` sees a fresh heartbeat → `engine_alive = True`.

**Why it happens when strategy is disabled:** The tail still contains bar events from the last strategy run. They are re-processed every cycle. Each processing sets `_last_engine_heartbeat = now`, so the heartbeat never ages and ENGINE ALIVE stays green.

### Exact code path causing BROKER CONNECTED to go green

**File:** `modules/watchdog/state_manager.py`  
**Function:** `compute_watchdog_status`  
**Lines:** 1771–1777

```python
connection_status = self._connection_status
if not engine_alive and connection_status == "Connected":
    connection_status = "Unknown"
elif connection_status == "Unknown" and engine_alive:
    connection_status = "Connected"
```

When `engine_alive` is True (from the bar-event path above), `connection_status` is set to `"Connected"` if it was `"Unknown"`. So BROKER CONNECTED is driven by the same root cause as ENGINE ALIVE.

### Summary

| Indicator | Root cause |
|-----------|------------|
| ENGINE ALIVE | Bar events (ONBARUPDATE_CALLED, BAR_ACCEPTED, BAR_RECEIVED_NO_STREAMS) processed every cycle from the tail with no age or invalidation check; each call to `update_engine_tick` sets `_last_engine_heartbeat = now`. |
| BROKER CONNECTED | Derived from `engine_alive`; when `engine_alive` is True and status is `"Unknown"`, it is inferred as `"Connected"`. |

**The fix:** Apply the same age and invalidation checks to bar events as to tick events (ENGINE_TICK_CALLSITE, ENGINE_ALIVE), or stop using bar events for engine liveness when they are older than a threshold or predate `_last_invalidate_utc`.
