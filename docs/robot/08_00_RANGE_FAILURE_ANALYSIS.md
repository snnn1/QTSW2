# 08:00 Range Failure Analysis

## Problem Summary

ES1 and GC1 streams (08:00 Chicago slot time) did not form ranges despite the strategy starting before the range start time.

## Timeline

- **11:08 UTC (05:08 Chicago)**: Strategy started
- **12:30 UTC (06:30 Chicago)**: Range start time passed (no transition occurred)
- **14:00 UTC (08:00 Chicago)**: Slot time passed (no range computation)
- **14:05 UTC (08:05 Chicago)**: Current time - ranges should be locked but aren't

## Root Cause Analysis

### 1. BarsRequest Did Not Run for ES/GC

**Evidence:**
- No `BARSREQUEST_*` events found in logs for ES/GC instruments
- Only BarsRequest events found were for CL and RTY (which were skipped)

**Impact:**
- No historical bars loaded for ES/GC
- Streams started with empty buffers

### 2. All Live Bars Rejected as Partial

**Evidence:**
- 0 bars ACCEPTED for ES/GC
- 1,253 bars REJECTED (all as `BAR_PARTIAL_REJECTED`)
- Bar age: ~0.016-0.036 minutes (all < 1 minute old)

**Root Cause:**
- `RobotEngine.OnBar()` line 485: `MIN_BAR_AGE_MINUTES = 1.0`
- All bars < 1 minute old are rejected as partial/in-progress
- This is correct behavior for live bars, but prevents any bars from reaching streams

**Impact:**
- No bars ever added to stream buffers
- `barCount = 0` for ES/GC streams

### 3. Pre-Hydration Transition Condition Should Have Worked

**Code Location:** `StreamStateMachine.cs` line 563

**Condition:**
```csharp
if (barCount > 0 || nowChicago >= RangeStartChicagoTime)
{
    Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM");
}
```

**Expected Behavior:**
- When range start time passed (12:30 UTC = 06:30 Chicago):
  - `barCount = 0` (no bars)
  - `nowChicago >= RangeStartChicagoTime` = TRUE
  - Condition should be TRUE → transition should occur

**Actual Behavior:**
- Transition did NOT occur
- Streams remained stuck in `PRE_HYDRATION` state

### 4. Why Transition Didn't Happen

**Possible Causes:**

1. **Tick() Not Called on Streams**
   - `RobotEngine.Tick()` calls `s.Tick(utcNow)` for each stream (line 433)
   - But if streams aren't in the `_streams` dictionary, they won't be ticked
   - **Status:** Streams WERE created (confirmed by `STREAMS_CREATED` event)

2. **Condition Check Failing Silently**
   - The condition check happens in `HandlePreHydrationState()`
   - If this method isn't being called, or if there's a bug in the condition, transition won't happen
   - **Status:** Added diagnostic logging to verify

3. **State Machine Not Processing**
   - Streams may be in wrong state
   - Or state machine may have a bug preventing transitions
   - **Status:** Need to verify stream states

## Diagnostic Logging Added

Added `PRE_HYDRATION_CONDITION_CHECK` event to log:
- `bar_count`: Current buffer count
- `now_chicago`: Current Chicago time
- `range_start_chicago`: Range start Chicago time
- `condition_bar_count_gt_zero`: Whether bar count condition is met
- `condition_now_ge_range_start`: Whether time condition is met
- `condition_met`: Whether overall condition is met
- `will_transition`: Whether transition will occur

This will help diagnose why the transition isn't happening.

## Immediate Actions Needed

1. **Verify BarsRequest Execution**
   - Check why BarsRequest didn't run for ES/GC
   - Verify NinjaTrader BarsRequest initialization
   - Check if instruments are properly configured

2. **Verify Stream Tick Processing**
   - Confirm `Tick()` is being called on ES/GC streams
   - Check if streams are in `PRE_HYDRATION` state
   - Verify `HandlePreHydrationState()` is being called

3. **Check Transition Logic**
   - Review diagnostic logs after restart
   - Verify condition evaluation is working correctly
   - Check for any silent failures or exceptions

## Long-Term Solutions

1. **BarsRequest Reliability**
   - Ensure BarsRequest runs for all enabled instruments
   - Add retry logic for failed BarsRequest calls
   - Log BarsRequest failures prominently

2. **Bar Acceptance Strategy**
   - Historical bars from BarsRequest should bypass age check (they're already closed)
   - Consider accepting bars that are "close enough" to 1 minute old (e.g., 0.9 minutes)
   - Add fallback mechanism when no historical bars are available

3. **Transition Monitoring**
   - Add alerts for streams stuck in `PRE_HYDRATION` past range start time
   - Monitor transition success rate
   - Add health checks for stream state progression

## Related Files

- `modules/robot/core/StreamStateMachine.cs`: Pre-hydration transition logic
- `modules/robot/core/RobotEngine.cs`: Bar acceptance and rejection logic
- `modules/robot/ninjatrader/RobotSimStrategy.cs`: BarsRequest initialization
- `modules/robot/ninjatrader/NinjaTraderBarRequest.cs`: BarsRequest implementation
