# Streams Not Showing - Deep Dive Analysis

## Root Cause Identified ✅

**Trading Date Mismatch**: All streams are being filtered out due to trading date mismatch.

### Current State

1. **Streams in State Manager**: 5 streams, all with `trading_date: "2026-01-28"`
   - NQ2 (2026-01-28): state=ARMED
   - NQ1 (2026-01-28): state=ARMED
   - RTY2 (2026-01-28): state=ARMED
   - RTY1 (2026-01-28): state=ARMED
   - YM2 (2026-01-28): state=ARMED

2. **Current Timetable**: `trading_date: "2026-01-29"`

3. **Filtering Result**: 
   - Total streams: 5
   - **Filtered by trading_date: 5** ⚠️ **ALL STREAMS FILTERED OUT**
   - Filtered by enabled_streams: 0
   - **Passed filters: 0** ❌

### The Problem

The `get_stream_states()` function filters streams by `current_trading_date`:

```python
if trading_date != current_trading_date:
    filtered_by_date += 1
    continue  # Skip streams from different trading dates
```

Since all streams have `trading_date: "2026-01-28"` but `current_trading_date: "2026-01-29"`, **all streams are filtered out**.

### Additional Issue

The state manager shows:
- `trading_date: None` (should be `2026-01-29` from timetable)
- `enabled_streams: None` (should have 6 streams)

This indicates the watchdog backend may not be running, or the timetable poller isn't updating the state manager.

## Why This Happened

1. **Day Rollover**: The trading date rolled over from `2026-01-28` to `2026-01-29` (likely at 17:00 CT)
2. **Old Streams**: Streams from `2026-01-28` are still in the state manager
3. **Cleanup Not Running**: The periodic cleanup (`cleanup_stale_streams`) should remove streams from different trading dates, but it may not be running or may have failed

## Solutions

### Solution 1: Restart Watchdog Backend (Recommended)

The watchdog backend needs to be running to:
1. Poll the timetable and update `current_trading_date` to `2026-01-29`
2. Run periodic cleanup to remove stale streams from `2026-01-28`
3. Process new `STREAM_STATE_TRANSITION` events with `trading_date: "2026-01-29"`

**Action**: Restart the watchdog backend service.

### Solution 2: Manual Cleanup

If the backend is running but cleanup isn't working, we can add more aggressive cleanup logic or manual cleanup triggers.

### Solution 3: Check Timetable Poller

Verify that the timetable poller is:
1. Running every 60 seconds
2. Successfully reading `timetable_current.json`
3. Updating the state manager with `update_timetable_streams()`

## Diagnostic Commands

Run these to verify:

```powershell
# Check if watchdog backend is running
Get-Process python | Where-Object {$_.CommandLine -like "*watchdog*"}

# Check latest timetable
Get-Content data/timetable/timetable_current.json | ConvertFrom-Json | Select-Object trading_date

# Check latest stream events
Select-String -Path logs/robot/frontend_feed.jsonl -Pattern "STREAM_STATE_TRANSITION" | Select-Object -Last 5 | ForEach-Object { $_.Line | ConvertFrom-Json | Select-Object trading_date, stream, event_seq }
```

## Expected Behavior After Fix

1. Watchdog backend polls timetable → `current_trading_date = "2026-01-29"`
2. Cleanup removes streams with `trading_date: "2026-01-28"`
3. New `STREAM_STATE_TRANSITION` events with `trading_date: "2026-01-29"` are processed
4. Streams appear in UI with correct trading date

## Next Steps

1. **Verify watchdog backend is running**
2. **Check watchdog logs** for timetable polling and cleanup messages
3. **Restart watchdog backend** if needed
4. **Monitor logs** for:
   - `TIMETABLE_POLL_OK` messages
   - `Removing stale stream from different trading date` messages
   - `✅ Stream state transition` messages with `trading_date: "2026-01-29"`
