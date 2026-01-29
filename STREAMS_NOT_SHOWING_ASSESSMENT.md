# Streams Not Showing - Full Assessment

## Problem Statement
Streams are not appearing in the watchdog UI despite events being processed.

## Complete Flow Analysis

### 1. Stream Creation (STREAM_STATE_TRANSITION Events)
**Location**: `event_processor.py` lines 191-340

**How Streams Are Created**:
- `STREAM_STATE_TRANSITION` events are processed
- Events must contain: `trading_date`, `stream`, `new_state`
- Streams are stored in `state_manager._stream_states` with key `(trading_date, stream)`

**Requirements**:
- ✅ Event type: `STREAM_STATE_TRANSITION`
- ✅ Must have `trading_date` field
- ✅ Must have `stream` field  
- ✅ Must have `new_state` in `data` dict
- ✅ Event must be in `LIVE_CRITICAL_EVENT_TYPES`

**Potential Issues**:
- Events missing required fields → streams not created
- Events not in `LIVE_CRITICAL_EVENT_TYPES` → filtered out before processing
- Events not reaching `event_processor.process_event()` → not processed

### 2. Stream Storage (State Manager)
**Location**: `state_manager.py` lines 143-164

**How Streams Are Stored**:
- Key: `(trading_date, stream)` tuple
- Value: `StreamStateInfo` object with state, instrument, session, etc.

**Potential Issues**:
- Streams created but immediately cleaned up (wrong trading_date)
- Streams overwritten by duplicate events
- Streams cleared by `cleanup_stale_streams()`

### 3. Stream Filtering (get_stream_states)
**Location**: `aggregator.py` lines 740-830

**Filtering Logic**:
1. **Trading Date Filter** (Line 768):
   ```python
   if trading_date != current_trading_date:
       continue  # Skip streams from different trading dates
   ```
   - Only streams matching `current_trading_date` are included
   - `current_trading_date` comes from timetable (authoritative) or CME rollover fallback

2. **Enabled Streams Filter** (Line 774):
   ```python
   if enabled_streams is not None:
       if stream not in enabled_streams:
           continue  # Skip disabled streams
   ```
   - Only streams in `enabled_streams` set are included
   - If `enabled_streams` is `None` (timetable unavailable), all streams are shown (fail-open)

**Potential Issues**:
- **Trading Date Mismatch**: Streams have different `trading_date` than current
- **Stream Not Enabled**: Stream not in timetable's `enabled_streams` list
- **Timetable Unavailable**: If timetable is unavailable, streams should still show (fail-open)

### 4. Stream Cleanup
**Location**: `state_manager.py` lines 258-305

**When Streams Are Removed**:
1. **Different Trading Date** (Line 271):
   - Streams from different `trading_date` are always removed
   - Happens during periodic cleanup and on ENGINE_START

2. **ENGINE_START Cleanup** (Line 279):
   - On ENGINE_START, streams not updated in last 30 seconds are removed
   - Prevents stale streams from previous runs

3. **Stuck in PRE_HYDRATION** (Line 295):
   - Streams stuck in PRE_HYDRATION for >2 hours are removed

**Potential Issues**:
- Streams cleaned up too aggressively
- Trading date mismatch causing premature cleanup
- Streams not being updated frequently enough

### 5. Frontend Display
**Location**: `StreamStatusTable.tsx` lines 31-50

**Empty State Messages**:
- Market Closed: "No active streams - Market is closed — this is expected"
- Market Open: "No active streams - Streams will begin forming ranges when they enter their range windows"
- Market Unknown: "No active streams - Waiting for market status"

**Potential Issues**:
- Frontend not receiving stream data from API
- API returning empty array
- Frontend filtering streams out

## Diagnostic Checklist

### Step 1: Verify Events Are Being Emitted
**Check**: Are `STREAM_STATE_TRANSITION` events in robot logs?
- Look for `STREAM_STATE_TRANSITION` events in `robot_*.jsonl` files
- Verify events have: `trading_date`, `stream`, `data.new_state`

### Step 2: Verify Events Are Written to Feed
**Check**: Are events in `frontend_feed.jsonl`?
- Search for `STREAM_STATE_TRANSITION` in feed file
- Verify `STREAM_STATE_TRANSITION` is in `LIVE_CRITICAL_EVENT_TYPES`

### Step 3: Verify Events Are Processed
**Check**: Are events being processed by backend?
- Look for log: `"Stream state transition: {stream} ({trading_date}) {old_state} -> {new_state}"`
- Check if `state_manager._stream_states` contains streams

### Step 4: Verify Trading Date Match
**Check**: Do stream trading dates match current trading date?
- Check `state_manager.get_trading_date()` value
- Compare with `trading_date` in `_stream_states` keys
- Look for: `"Removing stale stream from different trading date"` logs

### Step 5: Verify Enabled Streams Filter
**Check**: Are streams in enabled_streams list?
- Check `timetable_current.json` for `enabled_streams` array
- Verify stream IDs match exactly (case-sensitive)
- Check if timetable is available (`enabled_streams` is `None` = fail-open)

### Step 6: Verify Streams Not Cleaned Up
**Check**: Are streams being removed prematurely?
- Look for cleanup logs: `"Removing stale stream"`
- Check if `ENGINE_START` cleared streams
- Verify streams are being updated (not stuck)

### Step 7: Verify API Response
**Check**: Is API returning streams?
- Check `/api/watchdog/stream-states` response
- Verify `streams` array is not empty
- Check `timetable_unavailable` flag

### Step 8: Verify Frontend Receives Data
**Check**: Is frontend receiving stream data?
- Check browser console for API responses
- Verify `useStreamStates` hook receives data
- Check if streams array is empty in component

## Common Failure Points

### Failure Point 1: Trading Date Mismatch ⚠️ **MOST LIKELY**
**Symptom**: Streams exist but filtered out by trading_date check
**Cause**: 
- Watchdog using computed trading_date (CME rollover)
- Robot using timetable trading_date (different value)
- Date rollover timing differences

**Fix**: Already implemented - watchdog now uses timetable trading_date as authoritative

### Failure Point 2: Stream Not Enabled
**Symptom**: Stream exists but not in `enabled_streams` list
**Cause**:
- Stream ID mismatch (case sensitivity, formatting)
- Stream not listed in timetable
- Timetable not loaded correctly

**Fix**: Check timetable file, verify stream IDs match exactly

### Failure Point 3: Events Not Processed
**Symptom**: No streams created in state manager
**Cause**:
- `STREAM_STATE_TRANSITION` not in `LIVE_CRITICAL_EVENT_TYPES`
- Events filtered out before processing
- Cursor skipping events

**Fix**: Verify events are in feed and being processed

### Failure Point 4: Streams Cleaned Up
**Symptom**: Streams created but immediately removed
**Cause**:
- Wrong trading_date → cleaned up as stale
- ENGINE_START clearing active streams
- Stuck in PRE_HYDRATION >2 hours

**Fix**: Check cleanup logs, verify trading_date matches

### Failure Point 5: Frontend Not Receiving
**Symptom**: Backend has streams but frontend shows empty
**Cause**:
- API not returning streams
- Frontend filtering streams out
- Network/API errors

**Fix**: Check API response, verify frontend receives data

## Diagnostic Logging Added

The following logs will help identify the issue:

1. **`Stream state transition: {stream} ({trading_date}) {old_state} -> {new_state}`**
   - Confirms events are being processed
   - Shows trading_date and stream ID

2. **`get_stream_states: trading_date not set in state manager, using computed fallback`**
   - Indicates trading_date mismatch

3. **`Removing stale stream from different trading date`**
   - Shows streams being cleaned up due to date mismatch

4. **`get_stream_states: {count} streams found, {filtered_count} after filtering`**
   - Shows how many streams exist vs. are returned

## Next Steps

1. **Check Backend Logs** for diagnostic messages above
2. **Verify Trading Date** - ensure watchdog and robot use same date
3. **Check Timetable** - verify `enabled_streams` includes your streams
4. **Check API Response** - verify `/api/watchdog/stream-states` returns streams
5. **Check Frontend** - verify streams are received and displayed
