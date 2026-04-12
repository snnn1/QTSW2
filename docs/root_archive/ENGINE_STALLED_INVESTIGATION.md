# ENGINE STALLED Investigation & Summary

## Current Status
- **Issue**: Engine showing as "STALLED" despite ticks being visible in frontend
- **Last Tick Age**: 7514.2 seconds (~2 hours ago) according to logs
- **Threshold**: 15 seconds
- **Frontend**: Receiving 7139 ENGINE_TICK_CALLSITE events

## Complete Flow Analysis

### 1. Event Emission (Robot Engine → Robot Logs)
**Status**: ✅ Working (events exist in logs)
- `ENGINE_TICK_CALLSITE` events are emitted by Robot Engine
- Written to `robot_ENGINE.jsonl` log files
- Events include: `run_id`, `timestamp_utc`, `event_type="ENGINE_TICK_CALLSITE"`

### 2. Event Feed Generation (Robot Logs → frontend_feed.jsonl)
**Status**: ⚠️ Needs Verification
- `EventFeedGenerator.process_new_events()` reads from `robot_*.jsonl` files
- Filters to `LIVE_CRITICAL_EVENT_TYPES` (includes `ENGINE_TICK_CALLSITE`)
- **Rate Limiting**: Only writes `ENGINE_TICK_CALLSITE` every 5 seconds per `run_id`
- Writes to `frontend_feed.jsonl`

**Potential Issues**:
- Rate limiting might be too aggressive
- Events might not be written if timestamp parsing fails
- File might not be flushed immediately

### 3. Backend Event Processing (frontend_feed.jsonl → State Manager)
**Status**: ❌ **LIKELY FAILURE POINT**

**Current Flow**:
1. `WatchdogAggregator._process_feed_events_sync()` runs every 1 second
2. Reads events from `frontend_feed.jsonl` using cursor (`_read_feed_events_since`)
3. Cursor-based reading: Only reads events where `event_seq > cursor[run_id]`
4. Processes events through `EventProcessor.process_event()`
5. Updates state manager: `state_manager.update_engine_tick(timestamp_utc)`

**Problem Identified**:
- **Cursor Logic**: If cursor is at `event_seq=1964` and new ticks have `event_seq <= 1964`, they're skipped
- **Byte Position Tracking**: Uses `_feed_file_positions` which might be stale
- **End-of-File Check**: Recently added `_read_recent_ticks_from_end()` but may not be working correctly

### 4. State Manager Update
**Status**: ⚠️ Depends on Step 3
- `update_engine_tick()` updates `_last_engine_tick_utc`
- Should log `ENGINE_TICK_UPDATED` every 30 seconds (if receiving ticks)

### 5. Stall Detection
**Status**: ✅ Logic is Correct (but depends on Step 3)
- Checks `engine_tick_age = (now - _last_engine_tick_utc).total_seconds()`
- If `engine_tick_age > 15 seconds` → STALLED
- If `engine_tick_age <= 15 seconds` → ACTIVE

## Root Cause Analysis

### Primary Issue: Cursor-Based Reading Misses Events

**Problem**:
- Frontend requests events with `since_seq=0`, getting all historical events
- Backend uses cursor-based incremental reading
- If cursor is ahead of new events, they're never processed
- The `_read_recent_ticks_from_end()` method was added but may have bugs

**Evidence**:
- Log shows: `tick_age_seconds=7514.2` (2 hours old)
- Frontend sees 7139 events (historical)
- Backend not processing new ticks

### Secondary Issues

1. **Rate Limiting**: 5-second rate limit might cause delays
2. **File Position Tracking**: Byte positions might be incorrect
3. **Event Sequence**: If events are out of order, cursor logic fails

## Diagnostic Logs to Check

After restart, look for these in backend logs:

1. ✅ `EventFeedGenerator: Wrote X ENGINE_TICK_CALLSITE event(s) to feed`
   - Confirms events are being written to feed

2. ✅ `Processed X ENGINE_TICK_CALLSITE event(s) from feed`
   - Confirms backend is reading ticks

3. ✅ `ENGINE_TICK_UPDATED: timestamp_utc=...`
   - Confirms state manager is being updated

4. ✅ `Updated liveness from end-of-file: tick_timestamp=...`
   - Confirms end-of-file reading is working

5. ❌ `ENGINE_STALL_DETECTED_TICKS_STOPPED: tick_age_seconds=...`
   - Shows why it's marked as stalled

## Recommended Fixes

### Fix 1: Improve End-of-File Reading (Already Implemented)
- `_read_recent_ticks_from_end()` reads backwards from end of file
- Should find most recent tick regardless of cursor
- **Status**: Implemented but needs verification

### Fix 2: Always Process Most Recent Tick
- Before checking stall status, always read the most recent tick from end of file
- Update state manager if tick is newer
- **Status**: Implemented in `_process_feed_events_sync()`

### Fix 3: Add Fallback to Cursor Reading
- If cursor-based reading finds no ticks, fall back to end-of-file reading
- **Status**: Partially implemented

### Fix 4: Verify EventFeedGenerator is Running
- Ensure `process_new_events()` is being called regularly
- Check if new events are being written to feed

## Next Steps

1. **Check Backend Logs** for diagnostic messages listed above
2. **Verify EventFeedGenerator** is writing new ticks to feed
3. **Test End-of-File Reading** - ensure it's finding recent ticks
4. **Check Cursor State** - verify cursor isn't stuck ahead
5. **Monitor State Manager** - ensure `_last_engine_tick_utc` is being updated

## Configuration

- **ENGINE_TICK_STALL_THRESHOLD_SECONDS**: 15 seconds
- **ENGINE_TICK_CALLSITE_RATE_LIMIT_SECONDS**: 5 seconds (in feed)
- **Processing Frequency**: Every 1 second
- **Grace Period**: 5 minutes (300 seconds)
