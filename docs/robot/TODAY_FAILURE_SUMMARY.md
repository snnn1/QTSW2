# Today's Failure Summary - January 20, 2026

## Executive Summary

The robot failed to compute ranges for ES1 and GC1 streams (08:00 Chicago slot time) due to a cascade of issues:
1. **BarsRequest never executed for ES/GC** - No historical bars loaded
2. **100% live bar rejection** - All bars rejected as partial (< 1 minute old)
3. **Streams stuck in PRE_HYDRATION** - Never transitioned to ARMED despite time threshold

## Timeline of Events

### 11:08 UTC (05:08 Chicago) - Strategy Startup
- Engine started successfully
- Trading date locked: 2026-01-20
- Streams created (10 streams total)
- **BarsRequest executed for CL and RTY only** (both skipped)
- **NO BarsRequest events for ES/GC**

### 11:08 UTC - Initial Issues
- Multiple `BAR_TIME_INTERPRETATION_MISMATCH` warnings
- Affected instruments: GC, NQ, and others
- Timezone interpretation issues at startup

### 11:10 UTC - Data Loss Detected
- `DATA_LOSS_DETECTED` events for multiple streams
- Indicates missing data feed

### 12:30 UTC (06:30 Chicago) - Range Start Time Passed
- Range start time for ES1/GC1 (08:00 slot) should have triggered transition
- **No transition occurred** - streams remained in PRE_HYDRATION

### 14:00 UTC (08:00 Chicago) - Slot Time Passed
- Slot time for ES1/GC1 passed
- **No range computation occurred**
- Streams still stuck in PRE_HYDRATION

### Throughout Day - Continuous Errors
- `GAP_VIOLATIONS_SUMMARY` errors every 5 minutes
- `BAR_REJECTION_RATE_HIGH` warnings
- 100% bar rejection rate for ES/GC

## Root Cause Analysis

### 1. BarsRequest Did NOT Run for ES/GC

**Evidence:**
- Only 8 BarsRequest events total
- CL and RTY had BarsRequest events (but were skipped)
- **Zero BarsRequest events for ES/GC**
- No `BARSREQUEST_INITIALIZATION` for ES/GC
- No `BARSREQUEST_RAW_RESULT` for ES/GC
- No `PRE_HYDRATION_BARS_LOADED` for ES/GC

**Why This Happened:**
- Need to check NinjaTrader strategy code
- May be instrument-specific BarsRequest logic
- May be conditional execution based on time/instrument

**Impact:**
- No historical bars loaded for ES/GC
- Streams started with empty buffers
- No pre-hydration data available

### 2. All Live Bars Rejected as Partial

**Evidence:**
- 0 bars ACCEPTED for ES/GC
- 1,253+ bars REJECTED (all as `BAR_PARTIAL_REJECTED`)
- Bar ages: 0.016-0.036 minutes (all < 1 minute old)
- `BAR_REJECTION_RATE_HIGH` warnings

**Why This Happened:**
- `RobotEngine.OnBar()` line 485: `MIN_BAR_AGE_MINUTES = 1.0`
- All bars < 1 minute old are rejected as partial/in-progress
- This is correct behavior for live bars (prevents partial bar contamination)
- But prevents any bars from reaching streams when no historical bars exist

**Impact:**
- No bars ever added to stream buffers
- `barCount = 0` for ES/GC streams
- Streams cannot compute ranges without bars

### 3. Streams Never Transitioned from PRE_HYDRATION

**Evidence:**
- No `STATE_TRANSITION` events found
- No `HYDRATION_SUMMARY` events for ES/GC
- No `PRE_HYDRATION_TIMEOUT_NO_BARS` events
- No `PRE_HYDRATION_CONDITION_CHECK` events (code not deployed yet)

**Expected Behavior:**
- Code at `StreamStateMachine.cs` line 563:
  ```csharp
  if (barCount > 0 || nowChicago >= RangeStartChicagoTime)
  {
      Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM");
  }
  ```
- When range start time passed (12:30 UTC = 06:30 Chicago):
  - `barCount = 0` (no bars)
  - `nowChicago >= RangeStartChicagoTime` = TRUE
  - Condition should be TRUE → transition should occur

**Why Transition Didn't Happen:**
- Possible causes:
  1. `Tick()` not being called on streams (unlikely - engine ticks all streams)
  2. `HandlePreHydrationState()` not being called (state check issue?)
  3. Condition check failing silently (bug in condition evaluation?)
  4. Streams in wrong state (not PRE_HYDRATION?)

**Impact:**
- Streams stuck in PRE_HYDRATION forever
- Never transitioned to ARMED → RANGE_BUILDING → RANGE_LOCKED
- No ranges computed for ES/GC

### 4. Gap Violations (Secondary Issue)

**Evidence:**
- `GAP_VIOLATIONS_SUMMARY` errors every 5 minutes
- Started at 11:08 UTC and continued throughout day

**Why This Happened:**
- Streams invalidated due to gap tolerance violations
- Likely caused by missing data (no bars = infinite gaps)

**Impact:**
- Trading blocked for invalidated streams
- Health monitoring alerts

### 5. Timezone Interpretation Mismatch (Secondary Issue)

**Evidence:**
- Multiple `BAR_TIME_INTERPRETATION_MISMATCH` warnings at 11:08 UTC
- Affected instruments: GC, NQ, and others

**Why This Happened:**
- Timezone detection logic may have issues
- Or bars arriving with unexpected timestamps

**Impact:**
- Potential bar time interpretation errors
- May contribute to bar rejection

## Diagnostic Logging Added

Added `PRE_HYDRATION_CONDITION_CHECK` event to `StreamStateMachine.cs`:
- Logs bar count, time comparison, condition evaluation
- Will help diagnose why transition isn't happening
- **Note:** Code changes not yet deployed (no events found in logs)

## Immediate Actions Needed

1. **Investigate BarsRequest Logic**
   - Check why BarsRequest didn't run for ES/GC
   - Review `RobotSimStrategy.cs` BarsRequest initialization
   - Verify instrument-specific BarsRequest execution
   - Check if there are conditions that skip ES/GC

2. **Verify Stream Tick Processing**
   - Confirm `Tick()` is being called on ES/GC streams
   - Check stream states in logs
   - Verify `HandlePreHydrationState()` is being called

3. **Deploy Diagnostic Logging**
   - Deploy updated `StreamStateMachine.cs` with condition check logging
   - Restart strategy and monitor `PRE_HYDRATION_CONDITION_CHECK` events
   - Analyze why transition condition isn't being met

4. **Review Bar Acceptance Strategy**
   - Consider accepting historical bars from BarsRequest even if < 1 minute old
   - Or adjust age check for historical bars
   - Add fallback mechanism when no historical bars available

## Long-Term Solutions

1. **BarsRequest Reliability**
   - Ensure BarsRequest runs for ALL enabled instruments
   - Add retry logic for failed BarsRequest calls
   - Log BarsRequest failures prominently
   - Add alerts for missing BarsRequest events

2. **Bar Acceptance Strategy**
   - Historical bars from BarsRequest should bypass age check (they're already closed)
   - Consider accepting bars that are "close enough" to 1 minute old (e.g., 0.9 minutes)
   - Add fallback mechanism when no historical bars are available

3. **Transition Monitoring**
   - Add alerts for streams stuck in PRE_HYDRATION past range start time
   - Monitor transition success rate
   - Add health checks for stream state progression
   - Log transition attempts even when condition fails

4. **Data Feed Monitoring**
   - Improve data loss detection
   - Add alerts for missing instruments
   - Monitor bar acceptance rates per instrument

## Related Files

- `modules/robot/core/StreamStateMachine.cs`: Pre-hydration transition logic
- `modules/robot/core/RobotEngine.cs`: Bar acceptance and rejection logic
- `modules/robot/ninjatrader/RobotSimStrategy.cs`: BarsRequest initialization
- `modules/robot/ninjatrader/NinjaTraderBarRequest.cs`: BarsRequest implementation
- `docs/robot/08_00_RANGE_FAILURE_ANALYSIS.md`: Detailed technical analysis
