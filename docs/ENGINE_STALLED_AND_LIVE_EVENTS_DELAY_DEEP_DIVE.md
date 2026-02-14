# ENGINE STALLED Flickering + Live Events 30-Min Delay: Deep Dive

## Executive Summary

**Yes, the two issues are connected.** The Live Events feed delay and ENGINE STALLED flickering share a common root cause: **heavy file I/O on `frontend_feed.jsonl`** (~54MB) causing the pipeline to fall behind. When the pipeline is slow:

1. **ENGINE STALLED flickering**: Liveness uses the tick event's `timestamp_utc`. If we're processing delayed events, that timestamp is old → we show STALLED. When we occasionally catch up, we show ALIVE. Result: flickering.
2. **Live Events ~30 min late**: The REST `/events` API and aggregator both read the entire feed file. With a 54MB file, each read takes seconds. The pipeline can't keep up → events appear 30+ minutes behind real-time.

---

## Data Flow (Quick Reference)

```
Robot (NinjaTrader)
    → robot_ENGINE.jsonl, robot_ES.jsonl, ... (17 files)
    → EventFeedGenerator (reads via persisted byte positions)
    → frontend_feed.jsonl (~54MB, append-only)
    → WatchdogAggregator (reads last 5000 lines 2–3x per second)
    → State Manager (engine_alive, engine_activity_state)
    → REST /events (get_events_since reads ENTIRE file)
    → Live Events UI (polls every 1.5s)
```

---

## Root Cause 1: Full-File Reads Every Second

### Aggregator Processing Loop (every 1 second)

| Operation | What it does | I/O cost |
|-----------|--------------|----------|
| `_read_recent_ticks_from_end` | Reads backwards in 8KB chunks | ~O(tail) – OK |
| `_read_recent_bar_events_from_end` | `f.readlines()` then last 5000 | **Reads entire 54MB** |
| `_read_feed_events_since` | `f.readlines()` then last 5000 | **Reads entire 54MB** |

So each cycle reads ~108MB from disk (54MB × 2). At 50–100 MB/s, that’s 1–2 seconds per cycle. With a 1-second sleep, the loop can take 2–3 seconds per iteration and fall behind.

### REST `/events` API (every 1.5 seconds per client)

`get_events_since(run_id, since_seq)` walks the **entire** `frontend_feed.jsonl` line by line. For a 54MB file (~500k lines), that’s another full read every 1.5 seconds per client.

---

## Root Cause 2: Liveness Uses Event Timestamp, Not Processing Time

```python
# state_manager.py - compute_engine_alive()
elapsed = (now - self._last_engine_tick_utc).total_seconds()
engine_alive = elapsed < stall_threshold
```

`_last_engine_tick_utc` comes from the tick event’s `timestamp_utc` (when the robot wrote it), not when we processed it.

- If the pipeline is 30 minutes behind, we process ticks with timestamps 30 minutes old.
- `elapsed` ≈ 30 minutes → `engine_alive = False` → ENGINE STALLED.
- When we occasionally process a fresher batch, we briefly show ALIVE → flickering.

---

## Root Cause 3: EventFeedGenerator and Robot Log Positions

- `robot_ENGINE.jsonl` read position: 14,058,938 bytes (matches current file size → fully caught up).
- If the robot stops writing (e.g. market closed), no new ticks → no new ENGINE_TICK_CALLSITE → feed stops growing → we keep processing the same old tail → STALLED.

During market hours, the main bottleneck is the aggregator and REST API reading the full feed repeatedly.

---

## Fixes Implemented

### 1. Tail-Only Reads for Aggregator (no full-file reads)

- `_read_feed_events_since`: Use reverse chunked read to get only the last N lines instead of `readlines()` on the whole file.
- `_read_recent_bar_events_from_end`: Same approach – read backwards from EOF.

### 2. Tail-Only Reads for REST `/events`

- `get_events_since`: Read only the last 10,000 lines instead of the entire file. Recent events are at the end, so this keeps the API fast while still returning recent data.

### 3. Liveness: Use Processing Time as Fallback

- When we process a tick, also record `_last_engine_tick_processed_utc`.
- If the event’s `timestamp_utc` is very old (>2 minutes) but we just processed it, treat it as “we are receiving ticks” and use processing time for liveness. This reduces flicker when the feed is delayed but the pipeline is actively consuming ticks.

---

## Verification

1. **Watchdog logs**: Look for `ENGINE_ALIVE_STATUS` and `Updated liveness from end-of-file` – tick age should stay under ~60s during market hours.
2. **Live Events timestamps**: Event `ts_utc` / `timestamp_chicago` should be within a few seconds of real time.
3. **File sizes**: If `frontend_feed.jsonl` grows past 100MB, rotation should kick in; check `_rotate_feed_file_if_needed`.

---

## Related Files

- `modules/watchdog/aggregator.py` – feed reading, processing loop
- `modules/watchdog/state_manager.py` – `compute_engine_alive`, hysteresis
- `modules/watchdog/event_feed.py` – EventFeedGenerator, robot log positions
- `modules/watchdog/config.py` – `ENGINE_TICK_STALL_THRESHOLD_SECONDS`, `ENGINE_TICK_STALL_HYSTERESIS_SECONDS`
