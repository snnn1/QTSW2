# Stuck Stream Analysis: RTY2 from Yesterday

## Issue

**Stream**: RTY2 (RTY, S2, 09:30 slot)
**State**: RANGE_LOCKED (from yesterday's session)
**Trading Date**: 2026-02-03 (yesterday)
**Current Date**: 2026-02-04 (today)
**Problem**: Stream is stuck in RANGE_LOCKED state and showing in watchdog UI

## Root Cause

### 1. Stream Never Transitioned to DONE
- **Found**: 5 RANGE_LOCKED events for RTY2
- **Missing**: 0 DONE/MARKET_CLOSE events
- **Latest RANGE_LOCKED**: 2026-02-04T02:52:06 (but trading_date is 2026-02-03)

**This means**: The stream never completed properly - it should have transitioned to DONE when:
- Trade completed (target/stop hit)
- Market closed
- Stream was committed

### 2. Watchdog Filtering Issue
The watchdog has defensive code to filter streams by trading date (lines 1072-1080 in `aggregator.py`):

```python
if watchdog_trading_date and watchdog_trading_date != current_trading_date:
    logger.warning(...)
    watchdog_info = None  # Skip this stream
```

However, the stream is still showing, which suggests:
- The trading_date check isn't working correctly
- The stream state wasn't cleaned up when trading date changed
- The UI is showing stale data

## Why This Happens

### Possible Scenarios:

1. **Robot Restarted Before Stream Completed**
   - Stream was in RANGE_LOCKED state
   - Robot restarted before trade completed
   - Stream state was restored but never transitioned to DONE
   - Trading date changed, but old state wasn't cleaned up

2. **Market Close Event Not Processed**
   - Stream should have received MARKET_CLOSE_NO_TRADE or similar event
   - Event wasn't logged or wasn't processed by watchdog
   - Stream remained in RANGE_LOCKED state

3. **Watchdog State Not Cleaned Up**
   - When trading date changes, old stream states should be cleared
   - Cleanup may not have run or failed
   - Old streams persist in watchdog state

## Solution

### Immediate Fix

The watchdog should filter out streams from previous trading dates. The code exists but may not be working correctly.

**Check**: Verify watchdog is using current trading date when filtering streams.

### Long-term Fix

1. **Ensure Streams Transition to DONE**
   - Verify MARKET_CLOSE events are being logged
   - Verify watchdog processes these events
   - Add defensive cleanup for streams that don't complete

2. **Improve Trading Date Change Handling**
   - When trading date changes, explicitly clear old stream states
   - Add logging when old streams are detected
   - Add cleanup routine that runs on trading date change

3. **Add Stream State Validation**
   - Validate stream states match current trading date
   - Log warnings when mismatches are detected
   - Auto-cleanup stale states

## Code Locations

### Watchdog Filtering Logic
- **File**: `modules/watchdog/aggregator.py`
- **Lines**: 1068-1080
- **Function**: `get_stream_states()`

### Stream State Updates
- **File**: `modules/watchdog/event_processor.py`
- **Lines**: 470-486 (MARKET_CLOSE_NO_TRADE)
- **Lines**: 520-531 (RANGE_INVALIDATED)

### Trading Date Change Handling
- **File**: `modules/watchdog/aggregator.py`
- **Lines**: 282-286
- **Event**: `WATCHDOG_TRADING_DATE_CHANGED`

## Verification

To verify the fix:

1. **Check Current Trading Date**:
   ```python
   # Should be 2026-02-04
   ```

2. **Check Stream States**:
   ```python
   # Should only show streams for 2026-02-04
   # RTY2 from 2026-02-03 should be filtered out
   ```

3. **Check Watchdog Logs**:
   ```bash
   # Look for warnings about wrong trading_date
   grep "wrong trading_date" watchdog logs
   ```

## Expected Behavior

- **Watchdog UI**: Should only show streams for current trading date
- **Old Streams**: Should be filtered out automatically
- **Stuck Streams**: Should be cleaned up when trading date changes

## Current Status

- ✅ **Diagnosis Complete**: Stream stuck in RANGE_LOCKED from yesterday
- ⚠️ **Issue Identified**: Watchdog not filtering correctly
- 🔧 **Fix Needed**: Improve trading date filtering and cleanup
