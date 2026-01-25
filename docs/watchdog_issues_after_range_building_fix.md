# Watchdog Issues After RANGE_BUILDING Fix

## Summary
After fixing the issue where streams could enter `RANGE_BUILDING` when there's no range to build (market closed or no bars), several watchdog-related issues were identified.

## Issues Identified

### 1. **False Positive: ARMED State Stuck Detection**
**Problem**: Streams can legitimately stay in `ARMED` state waiting for bars (new `ARMED_WAITING_FOR_BARS` diagnostic), but the watchdog flags them as stuck after 5 minutes (`STUCK_STREAM_THRESHOLD_SECONDS = 300`).

**Impact**: False alarms for streams that are correctly waiting for market data.

**Location**: `modules/watchdog/state_manager.py:compute_stuck_streams()`

**Current Logic**:
- `PRE_HYDRATION`: Special handling with 30-minute timeout
- All other states (including `ARMED`): 5-minute timeout

**Fix Needed**: Add special handling for `ARMED` state similar to `PRE_HYDRATION`, with a longer timeout (e.g., 1-2 hours) since streams can legitimately wait for bars.

### 2. **Missing Event Handler: NO_TRADE_MARKET_CLOSE**
**Problem**: The watchdog doesn't have a specific handler for `NO_TRADE_MARKET_CLOSE` events. Streams that commit at market close might not be properly marked as committed.

**Impact**: Streams that commit at market close might still be flagged as stuck or show incorrect state.

**Location**: `modules/watchdog/event_processor.py`

**Fix Needed**: Add handler for `NO_TRADE_MARKET_CLOSE` events (or ensure `STREAM_STAND_DOWN` events with `reason="NO_TRADE_MARKET_CLOSE"` are properly handled).

### 3. **RANGE_BUILDING After Market Close**
**Problem**: If a stream is already in `RANGE_BUILDING` when market closes, our fix commits it, but there's a window where the watchdog might flag it as stuck before the commit event is processed.

**Impact**: Brief false positive stuck detection before commit is processed.

**Mitigation**: This is acceptable since the commit will happen quickly, but we should ensure `RANGE_BUILDING` streams are checked against market close time in the watchdog.

### 4. **ARMED_WAITING_FOR_BARS Event Not Tracked**
**Problem**: The new `ARMED_WAITING_FOR_BARS` diagnostic event isn't recognized by the watchdog, so it can't distinguish between streams legitimately waiting for bars vs. actually stuck.

**Impact**: Cannot provide context-aware stuck detection.

**Fix Needed**: Either:
- Add `ARMED_WAITING_FOR_BARS` to event processor to track waiting state
- Or rely on state duration + market status to determine if waiting is legitimate

### 5. **Market-Aware Stuck Detection**
**Problem**: The watchdog doesn't consider market status when determining if streams are stuck. Streams waiting in `ARMED` or `RANGE_BUILDING` after market close should not be flagged as stuck.

**Impact**: False positives after market hours.

**Location**: `modules/watchdog/state_manager.py:compute_stuck_streams()`

**Fix Needed**: Check `is_market_open()` before flagging streams as stuck. Streams in `ARMED` or `RANGE_BUILDING` after market close should be excluded from stuck detection (they should commit, but if they don't, that's a separate issue).

## Recommended Fixes

### Priority 1: ARMED State Timeout Extension
```python
# In compute_stuck_streams()
if info.state == "ARMED":
    # ARMED streams can legitimately wait for bars or market open
    # Use longer timeout: 2 hours
    armed_timeout = 2 * 60 * 60  # 2 hours
    if stuck_duration > armed_timeout:
        # Only flag as stuck if market is open (waiting during market hours is suspicious)
        if market_open:
            stuck_streams.append({...})
```

### Priority 2: Market-Aware Stuck Detection
```python
# In compute_stuck_streams()
from .market_session import is_market_open
chicago_now = datetime.now(CHICAGO_TZ)
market_open = is_market_open(chicago_now)

# Skip stuck detection for ARMED/RANGE_BUILDING if market is closed
if not market_open and info.state in ("ARMED", "RANGE_BUILDING"):
    continue  # Don't flag as stuck if market is closed
```

### Priority 3: NO_TRADE_MARKET_CLOSE Handler
Ensure `STREAM_STAND_DOWN` events with `reason="NO_TRADE_MARKET_CLOSE"` properly mark streams as committed (this should already work, but verify).

## Fixes Implemented

### ✅ Priority 1: ARMED State Timeout Extension
**Status**: IMPLEMENTED
- Added special handling for `ARMED` state with 2-hour timeout
- Only flags as stuck if market is open (waiting during market hours is suspicious)
- Location: `modules/watchdog/state_manager.py:compute_stuck_streams()`

### ✅ Priority 2: Market-Aware Stuck Detection
**Status**: IMPLEMENTED
- Added market status check using `is_market_open()`
- Streams in `ARMED` or `RANGE_BUILDING` after market close are excluded from stuck detection
- Location: `modules/watchdog/state_manager.py:compute_stuck_streams()`

### ✅ Priority 3: MARKET_CLOSE_NO_TRADE Handler
**Status**: IMPLEMENTED
- Added handler for `MARKET_CLOSE_NO_TRADE` events (treated same as `STREAM_STAND_DOWN`)
- Properly marks streams as committed with `commit_reason="NO_TRADE_MARKET_CLOSE"`
- Added to `LIVE_CRITICAL_EVENT_TYPES` in config
- Location: `modules/watchdog/event_processor.py` and `modules/watchdog/config.py`

## Testing Checklist
- [ ] Stream in ARMED waiting for bars during market hours (should not be flagged for at least 2 hours)
- [ ] Stream in ARMED after market close (should not be flagged as stuck)
- [ ] Stream in RANGE_BUILDING after market close (should commit, not be flagged)
- [ ] Stream stuck in ARMED for > 2 hours during market hours (should be flagged)
- [ ] Stream commits with NO_TRADE_MARKET_CLOSE (should be marked committed)
