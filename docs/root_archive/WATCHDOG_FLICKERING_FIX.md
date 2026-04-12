# Watchdog Flickering Fix - Engine and Data Status

## Problem

Engine and data status indicators in the watchdog occasionally flicker (turn red/yellow then back to green). This creates false alarms and reduces confidence in the monitoring system.

## Root Cause Analysis

### Engine Status Flickering
- **Status**: ✅ **NOT AN ISSUE** - Engine ticks are fine
- **Analysis**: Recent tick gaps are all < 4s (well below 15s threshold)
- **Conclusion**: Engine status flickering is likely due to frontend polling timing, not actual stalls

### Data Status Flickering  
- **Status**: ⚠️ **ISSUE IDENTIFIED** - Bar event gaps can exceed threshold
- **Analysis**: 
  - Bar events are rate-limited to 60 seconds (`BAR_RECEIVED_NO_STREAMS`, `BAR_ACCEPTED`)
  - Threshold is 90 seconds (1.5x rate limit)
  - Found gaps up to 109.4 seconds (exceeds threshold)
- **Causes**:
  1. Rate limiting (events written every 60s) + processing delays
  2. Market pauses (lunch break, low liquidity periods)
  3. Temporary data feed delays
  4. Frontend polls every 5s, so brief threshold violations cause flickering

## Current Thresholds

```python
ENGINE_TICK_STALL_THRESHOLD_SECONDS = 15  # 3x rate limit (5s)
DATA_STALL_THRESHOLD_SECONDS = 90         # 1.5x rate limit (60s)
```

## Solutions

### Option 1: Increase Thresholds (Quick Fix)
**Pros**: Simple, immediate fix
**Cons**: May miss real stalls

**Changes**:
- Increase `DATA_STALL_THRESHOLD_SECONDS` from 90s to 120s (2x rate limit)
- This gives more buffer for rate limiting and market pauses

### Option 2: Add Smoothing/Debouncing (Better Fix)
**Pros**: Prevents flickering while maintaining sensitivity
**Cons**: More complex implementation

**Implementation**:
- Track status over a sliding window (e.g., last 3 polls)
- Only change status if it's been consistent for 2-3 consecutive polls
- Prevents single-poll flickering

### Option 3: Use Moving Average (Best Fix)
**Pros**: Most stable, handles temporary gaps gracefully
**Cons**: Most complex

**Implementation**:
- Track bar age over last N polls (e.g., 5 polls = 25 seconds)
- Use average bar age instead of single threshold check
- Only flag as stalled if average exceeds threshold

## Recommended Fix: Option 2 (Smoothing)

Add debouncing to status computation to prevent flickering:

1. **Backend**: Track last N status computations
2. **Only change status** if it's been consistent for 2-3 polls
3. **Frontend**: Already has some debouncing via memoization, but can be improved

## Implementation Plan

### Phase 1: Increase Threshold (Immediate)
- Increase `DATA_STALL_THRESHOLD_SECONDS` to 120s
- This provides immediate relief from flickering

### Phase 2: Add Smoothing (Near-term)
- Add status history tracking in `WatchdogStateManager`
- Only change status if consistent for 2 polls (10 seconds)
- Prevents flickering from single-poll threshold violations

### Phase 3: Improve Frontend Debouncing (Optional)
- Add debouncing to status display
- Only update UI if status has been stable for 2-3 polls

## Testing

After implementing fixes:
1. Monitor watchdog for 1 hour during active trading
2. Count flickering events (status changes that revert within 10 seconds)
3. Verify no real stalls are missed
4. Check that flickering is reduced by >80%

## Current Status

- ✅ Engine ticks: Working correctly (gaps < 4s)
- ⚠️ Data status: Flickering due to tight threshold (90s) vs rate limit (60s)
- ✅ Frontend polling: Every 5 seconds (appropriate)
- ✅ Status computation: Working correctly, but needs smoothing
