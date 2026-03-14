# Watchdog ENGINE ALIVE Evidence Trace — Exact Root Cause

## Section 1 — Trace All Writes to Engine Liveness State

### `_last_engine_heartbeat`

| File | Function | Line | Condition | Event Types |
|------|----------|------|-----------|-------------|
| `state_manager.py` | `update_engine_tick` | 195 | Always: `self._last_engine_heartbeat = now` | Called only by ENGINE_TICK_CALLSITE, ENGINE_ALIVE handlers |
| `state_manager.py` | `invalidate_engine_liveness` | 181 | When NinjaTrader process not running | None (process check) |

### `_last_engine_tick_utc`

| File | Function | Line | Condition | Event Types |
|------|----------|------|-----------|-------------|
| `state_manager.py` | `update_engine_tick` | 194 | Always: `self._last_engine_tick_utc = timestamp_utc` | ENGINE_TICK_CALLSITE, ENGINE_ALIVE |
| `state_manager.py` | `invalidate_engine_liveness` | 180 | When NinjaTrader process not running | None |

### `_last_engine_alive_value`

| File | Function | Line | Condition | Event Types |
|------|----------|------|-----------|-------------|
| `state_manager.py` | `__init__` | 91 | Default: `True` | N/A |
| `state_manager.py` | `compute_engine_alive` | 1153, 1169 | Read heartbeat; set to False if None; set to computed value | N/A |
| `state_manager.py` | `invalidate_engine_liveness` | 182 | Set to False | None |

### Direct `engine_alive` assignment/override

| File | Function | Line | Condition |
|------|----------|------|-----------|
| `aggregator.py` | `get_watchdog_status` | 1826 | When `not ninja_running`: `status["engine_alive"] = False` |

**Write graph:** The only way `_last_engine_heartbeat` gets set is `update_engine_tick()`, which is called only from `process_event()` for ENGINE_TICK_CALLSITE and ENGINE_ALIVE.

---

## Section 2 — Trace All Event Types That Can Trigger Liveness

| Event Type | Where Processed | Refreshes Heartbeat? | Startup | Runtime |
|------------|-----------------|----------------------|---------|---------|
| ENGINE_TICK_CALLSITE | event_processor.py:171 | Yes (update_engine_tick) | Yes (aggregator startup init) | Yes (tail update) |
| ENGINE_ALIVE | event_processor.py:177 | Yes (update_engine_tick) | Yes (aggregator startup init) | Yes (tail update) |
| ENGINE_START | event_processor.py:101 | No (removed) | Yes | Yes |
| ENGINE_HEARTBEAT | event_processor.py:122 | No | Yes | Yes |
| ENGINE_TICK_HEARTBEAT | event_processor.py:126 | No | Yes | Yes |
| ONBARUPDATE_CALLED | event_processor.py:144 | No | Yes | Yes |
| ENGINE_TICK_STALL_RECOVERED | event_processor.py:284 | No | No | Yes |

**Proven:** Only ENGINE_TICK_CALLSITE and ENGINE_ALIVE call `update_engine_tick()`.

---

## Section 3 — Startup/Bootstrap Behavior (Exact Path)

### Scenario: Watchdog starts, NinjaTrader already open at login

**Sequence:**

1. **aggregator.start()** (line 355)
   - `_last_invalidate_utc` is **None** (never set; only set when `ninja_running=False` and we invalidate)
   - No process check at startup

2. **Tail read** (lines 367–376)
   - `_read_last_lines_with_metrics(FRONTEND_FEED_FILE, TAIL_LINE_COUNT)` → 3000 lines
   - `startup_snapshot` = parsed events from tail (includes old events from previous strategy run)

3. **Init engine tick** (lines 385–401)
   ```python
   ticks = [e for e in reversed(startup_snapshot) if e.get("event_type") in ("ENGINE_TICK_CALLSITE", "ENGINE_ALIVE")]
   if ticks:
       most_recent_tick = ticks[0]
       age_sec = (datetime.now(timezone.utc) - tick_ts).total_seconds()
       if age_sec <= ENGINE_TICK_MAX_AGE_FOR_INIT_SECONDS:  # 15
           self._event_processor.process_event(most_recent_tick)  # ← ACCEPTS
   ```
   - **No `_last_invalidate_utc` check**
   - Only check: `age_sec <= 15`
   - If tick is 10 seconds old (from previous strategy run) → **ACCEPTED**
   - `process_event(most_recent_tick)` → `update_engine_tick()` → `_last_engine_heartbeat = now`

4. **Result:** ENGINE ALIVE turns green immediately.

### Answers

| Question | Answer |
|----------|--------|
| Is a fresh event from the feed tail being accepted? | **No.** An **old** event (up to 15 seconds old) is accepted. |
| What event type is it? | ENGINE_TICK_CALLSITE or ENGINE_ALIVE |
| Is `_last_invalidate_utc` bypassed? | **Yes.** Startup init does not check it; it is None at startup. |
| Is ENGINE_ALIVE emitted when NinjaTrader opens? | **No.** Strategy does not run at login; no new events. Tail has old events only. |
| Is process-running state indirectly causing alive/connected? | **No.** Process check only overrides when process is **down**. |

---

## Section 4 — Status Computation

### get_watchdog_status() flow

1. `status = self._state_manager.compute_watchdog_status()` (line 1816)
   - Calls `compute_engine_alive()` → `heartbeat = _last_engine_heartbeat or _last_engine_tick_utc`
   - If heartbeat set and `elapsed < threshold` → `engine_alive = True`
   - `connection_status`: if `engine_alive` and status was Unknown → `"Connected"`

2. Process check (lines 1821–1832)
   - If `not ninja_running`: override `engine_alive = False`, call `invalidate_engine_liveness()`
   - When `ninja_running`: **no override**; computed status is used as-is

### Where engine_alive can be forced true or false

| Location | Condition | Effect |
|----------|-----------|--------|
| aggregator.py:1826 | `not ninja_running` | Force False |
| state_manager.compute_engine_alive | heartbeat age | Compute True/False |

### Where connection_status becomes Connected

| Location | Condition |
|----------|-----------|
| state_manager.py:1776 | `connection_status == "Unknown" and engine_alive` → `"Connected"` |
| event_processor (CONNECTION_RECOVERED etc.) | Explicit connection events |

---

## Section 5 — Frontend Caching/Staleness

| Aspect | Finding |
|--------|---------|
| Poll cadence | 5 seconds (`usePollingInterval(poll, 5000)`) |
| Memoization | `setStatus(prev => ...)` only keeps prev if key fields (including `engine_alive`) are equal |
| Stale state | If `data` differs, state is updated. No logic that keeps previous green when new status is missing. |
| API | `fetchWatchdogStatus()` → `GET /status` → returns `engine_alive` from backend |

**Conclusion:** The frontend shows what the backend returns. The bug is in the backend.

**Inspection:** Inspect the `/status` API response field `engine_alive`. If it is `true` when NinjaTrader is open but strategy disabled, the backend is wrong.

---

## Section 6 — Instrumentation Plan

Root cause is identified from static inspection. Minimal instrumentation to confirm:

Add to `state_manager.update_engine_tick()`:
```python
logger.info(
    f"ENGINE_TICK_UPDATED: event_type={event_type}, "
    f"tick_ts={timestamp_utc.isoformat()}, heartbeat_now={now.isoformat()}"
)
```
(Pass `event_type` as parameter or infer from caller.)

Add to `aggregator.start()` before tick init:
```python
logger.info(f"STARTUP_TICK_INIT: ticks_in_tail={len(ticks)}, most_recent_age_sec={age_sec:.1f}, will_accept={age_sec <= 15}")
```

---

## Section 7 — Final Conclusion

### What exact code path makes ENGINE ALIVE turn green when NinjaTrader is opened?

**File:** `modules/watchdog/aggregator.py`  
**Function:** `start()`  
**Lines:** 385–401

**Path:**
1. Watchdog starts (or was already running; same init applies on first load).
2. `startup_snapshot` is built from the tail of `frontend_feed.jsonl`.
3. Ticks are filtered: `ENGINE_TICK_CALLSITE`, `ENGINE_ALIVE`.
4. The most recent tick is taken.
5. **Condition:** `age_sec <= 15` (no `_last_invalidate_utc` check).
6. `process_event(most_recent_tick)` is called.
7. `event_processor.process_event` → `update_engine_tick(timestamp_utc)`.
8. `_last_engine_heartbeat = now`.
9. `compute_engine_alive()` returns True.
10. UI shows ENGINE ALIVE green.

### Is the source:

- **a) Feed event** — Yes. An old ENGINE_TICK_CALLSITE or ENGINE_ALIVE from the tail.
- **b) Startup/bootstrap rule** — Yes. Startup init accepts ticks with `age <= 15` and no invalidation check.
- **c) Process-state inference** — No.
- **d) Frontend stale-state** — No.
- **e) Something else** — No.

### Smallest correct fix

At startup, do not trust ticks from the tail. Either:

**Option A (recommended):** Call `invalidate_engine_liveness()` at the start of `start()` (before any init), and add to the startup tick init:

```python
last_inv = getattr(self._state_manager, "_last_invalidate_utc", None)
if last_inv and tick_ts <= last_inv:
    # Reject ticks from before we started
    logger.info(f"Skipped tick on init: tick predates startup (tick_ts <= _last_invalidate_utc)")
    # skip process_event
```

**Option B:** Remove tick init from startup entirely. Rely on the first ingestion cycle to set heartbeat from new ticks only. (Tail update already has age and invalidation checks, but would need to ensure no init path can set heartbeat from tail.)

### Scenarios to re-test after fix

1. NinjaTrader closed → ENGINE STALLED, BROKER UNKNOWN  
2. NinjaTrader open, strategy disabled → ENGINE STALLED, BROKER UNKNOWN  
3. NinjaTrader open, strategy enabled → ENGINE ALIVE, BROKER CONNECTED  
4. Watchdog restart while NinjaTrader open, strategy disabled → ENGINE STALLED, BROKER UNKNOWN  
5. Watchdog restart while strategy enabled → Brief STALLED, then ENGINE ALIVE after first tick  
