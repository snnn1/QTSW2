# Why Ticks Aren't Being Received - Diagnosis

## Current State

### ✅ Ticks ARE in Feed File
- **Total ticks**: 61,252 ENGINE_TICK_CALLSITE events
- **Latest tick**: `2026-01-29T02:54:16` (6.4 minutes ago)
- **Latest tick event_seq**: 2479
- **Latest tick run_id**: `74a584b675c04f93b913097eb72f3256`

### ❌ Ticks NOT Being Processed
- **State manager**: `_last_engine_tick_utc = None`
- **Cursor position**: Run ID `74a584b6...` at event_seq **1700** (779 events behind latest tick)
- **Diagnostic script**: Processed 0 ticks when trying to process recent events

## Root Cause

**The watchdog backend is NOT running** or not processing events correctly.

## Evidence

1. **Cursor is stale**: Cursor shows event_seq 1700, but latest tick is event_seq 2479
   - This means the watchdog hasn't processed events in a while
   - 779 tick events are waiting to be processed

2. **State manager is empty**: `_last_engine_tick_utc = None`
   - No ticks have been processed into the state manager
   - This confirms the watchdog backend isn't running

3. **End-of-file reading should catch this**: The code has logic to read from end of file (`_read_recent_ticks_from_end`), but it's not working because the backend isn't running

## Solution

**Restart the watchdog backend service.**

After restarting:
1. The backend will read from the end of the feed file (`_read_recent_ticks_from_end`)
2. It will find the latest tick (event_seq 2479)
3. It will process it and update `_last_engine_tick_utc`
4. The cursor will be updated to catch up
5. Ticks will start being received and processed

## How to Verify

After restarting, check:
1. **Backend logs** for: `✅ Updated liveness from end-of-file: tick_timestamp=...`
2. **State manager** should show: `_last_engine_tick_utc` with recent timestamp
3. **Cursor** should update to event_seq 2479 or higher
4. **UI** should show engine as "ACTIVE" instead of "STALLED"

## Code Flow

The watchdog backend should:
1. Run `_process_feed_events_sync()` every second
2. Call `_read_recent_ticks_from_end(max_events=1)` to get latest tick
3. Process it via `event_processor.process_event()`
4. Update `state_manager._last_engine_tick_utc`
5. Continue processing incremental events via cursor

Since the state manager shows `None`, this entire flow isn't running.
