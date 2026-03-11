# Hydration Problem - Full Summary

## Executive Summary

**Problem:** NQ2's range was calculated incorrectly as `[26170.25, 26197]` instead of `[25536, 26197]` because only **3 bars** were used instead of the expected **180 bars** for the 08:00-11:00 Chicago time window.

**Root Cause:** BarsRequest (which loads historical bars in SIM mode) was **never called on restart**. The system relied on live bars only, resulting in insufficient data for range calculation.

## The Problem: Incorrect Range Calculation

### What Happened

**NQ2 Range Calculation Error:**
- **Expected range:** `[25536, 26197]` (correct)
- **Actual range:** `[26170.25, 26197]` (incorrect)
- **Error:** Range low is wrong (26170.25 instead of 25536)

**Root Cause:**
- Range window: `08:00:00` to `11:00:00` Chicago time (180 minutes)
- Expected bar count: **180 bars** (1-minute bars)
- Actual bar count used: **3 bars**
- Bars used: `08:00:00`, `08:01:00`, `08:02:00` (only the first 3 minutes)

### Timeline of Events

1. **17:00:37 UTC (11:00:37 Chicago)** - Stream restarted
   - `MID_SESSION_RESTART_DETECTED` logged
   - Stream reinitialized internally
   - Previous state: `RANGE_BUILDING`

2. **17:00:38 UTC** - Pre-hydration completed
   - `PRE_HYDRATION_COMPLETE_SIM` logged with `bar_count: 0`
   - `HYDRATION_SNAPSHOT` showed:
     - `barsrequest_raw_count: 0`
     - `barsrequest_accepted_count: 0`
     - `historical_bar_count: 0`
     - `live_bar_count: 0`
   - Transition reason: `TIME_THRESHOLD` (forced transition after timeout)

3. **17:00:43 UTC (11:00:43 Chicago)** - Range calculation executed
   - Only **3 bars** were in the buffer
   - Bars were: `08:00:00`, `08:01:00`, `08:02:00`
   - All bars marked as `Source=CSV` (not from NinjaTrader BarsRequest)
   - Range calculated: `[26170.25, 26197]`

## Root Cause: BarsRequest Not Called on Restart

### The Critical Issue

**BarsRequest is only called during `OnStateChange(State.DataLoaded)`, which happens ONCE when the strategy first loads.**

**What Should Happen:**
- On initial load: BarsRequest is called → historical bars loaded ✅
- On restart: BarsRequest should be called again → historical bars loaded ✅
- **What Actually Happened:** BarsRequest was NOT called on restart ❌

### Why BarsRequest Wasn't Called

**BarsRequest Call Path:**
1. `RobotSimStrategy.OnStateChange(State.DataLoaded)` is called when strategy first loads
2. In `DataLoaded` handler, `RequestHistoricalBarsForPreHydration()` is queued in a background thread
3. BarsRequest is executed asynchronously

**On Restart:**
1. Stream is reinitialized internally in `StreamStateMachine` constructor
2. `MID_SESSION_RESTART_DETECTED` is logged
3. **BUT:** `OnStateChange(State.DataLoaded)` is NOT called again
4. **Result:** BarsRequest is never triggered
5. **Result:** No historical bars are loaded

### Evidence from Logs

**Initial Load (16:54:15):**
- BarsRequest was called and bars were processed
- Some bars were rejected as duplicates (LIVE bars already existed)
- This shows BarsRequest was working correctly

**On Restart (17:00:37):**
- `MID_SESSION_RESTART_DETECTED` logged
- Stream reinitialized
- **No `DATALOADED_INITIALIZATION_COMPLETE` event**
- **No `BARSREQUEST_QUEUED` event**
- **No `BARSREQUEST_CALLBACK_RECEIVED` events**
- **No `BARSREQUEST_RAW_RESULT` events**

**HYDRATION_SNAPSHOT (17:00:38):**
- `barsrequest_raw_count: 0` ← **BarsRequest never executed**
- `barsrequest_accepted_count: 0`
- `live_bar_count: 0`
- `historical_bar_count: 0`

## How Pre-Hydration Works (SIM Mode)

### Expected Behavior

**SIM Mode Pre-Hydration Flow:**
1. **BarsRequest** (primary source):
   - Called during `OnStateChange(State.DataLoaded)`
   - Requests historical bars from NinjaTrader API
   - Time range: `range_start` to `min(slot_time, now)`
   - Bars arrive asynchronously via callback
   - Bars are buffered with `BarSource.BARSREQUEST`

2. **CSV Fallback** (if BarsRequest fails):
   - Reads from `data/raw/{instrument}/1m/{yyyy}/{MM}/{yyyy-MM-dd}.csv`
   - Filters bars to hydration window
   - Bars are buffered with `BarSource.CSV`

3. **Live Bars** (ongoing):
   - Arrive via `OnBarUpdate()` from NinjaTrader
   - Bars are buffered with `BarSource.LIVE`
   - Highest precedence (LIVE > BARSREQUEST > CSV)

4. **Transition to ARMED:**
   - When bars are loaded OR when past range start time
   - Hard timeout: `range_start + 1 minute`

### What Actually Happened

1. **BarsRequest:** Never called on restart → 0 bars
2. **CSV Fallback:** Only 3 bars loaded (08:00, 08:01, 08:02)
3. **Live Bars:** Only 3 bars arrived before range calculation
4. **Result:** Pre-hydration completed with 0 bars (forced transition)

## Why Only 3 Bars?

### BarsRequest Issue

**BarsRequest was never called:**
- No `BARSREQUEST_REQUESTED` event
- No `BARSREQUEST_INITIALIZATION` event
- No callback events
- `barsrequest_raw_count: 0` in snapshot

### CSV Fallback Issue

**CSV pre-hydration only loaded 3 bars:**
- Bars were: `08:00:00`, `08:01:00`, `08:02:00`
- All marked as `Source=CSV`
- Possible reasons:
  - CSV file only contains 3 bars
  - CSV loading filtered out bars incorrectly
  - CSV file doesn't exist or is empty

### Live Bars Issue

**Only 3 live bars arrived:**
- Range calculation at 11:00 Chicago time
- Live bars should have been arriving since 08:00
- Why only 3 bars accumulated over 3 hours?
  - Stream may have been restarted just before range calculation
  - Live bars may not have been arriving continuously
  - Bars may have been filtered out

## Current BarsRequest Logic

### Time Range Calculation

**Current code (lines 565-567 in RobotSimStrategy.cs):**
```csharp
var endTimeChicago = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
    ? nowChicago.ToString("HH:mm")
    : slotTimeChicago;
```

**Problem:**
- On restart after slot_time, `endTimeChicago` is limited to `slotTimeChicago`
- Should request bars up to `nowChicago` on restart
- Current logic prevents loading bars beyond slot_time

**Example:**
- Slot time: `11:00`
- Restart time: `11:00:37`
- Current: Requests bars up to `11:00` (slot_time)
- Should: Request bars up to `11:00:37` (now)

### Restart Detection

**Current code doesn't detect restart:**
- No explicit restart detection in BarsRequest logic
- Relies on `OnStateChange(State.DataLoaded)` being called
- On restart, `OnStateChange` is NOT called again

## Impact

### Immediate Impact

1. **Incorrect Range Calculation:**
   - Range low: `26170.25` (wrong)
   - Should be: `25536` (correct)
   - Difference: `634.25` points

2. **Trading Impact:**
   - Breakout levels calculated incorrectly
   - Entry orders placed at wrong prices
   - Stop orders may be too tight or too wide

3. **Data Integrity:**
   - Range locked with incorrect data
   - Restoration correctly restores wrong range
   - Cannot fix without manual intervention

### Systemic Impact

1. **Restart Reliability:**
   - Any restart after slot_time results in insufficient bars
   - Range calculations are unreliable on restart
   - System cannot recover gracefully

2. **Visibility:**
   - No clear indication that bars are missing
   - Pre-hydration completes silently with 0 bars
   - Range calculation proceeds with insufficient data

3. **Safety:**
   - No validation that range was computed correctly
   - No checks for minimum bar count
   - System proceeds with incorrect data

## Solution Requirements

### Part 1: Restart-Aware BarsRequest

**Required Changes:**
1. Detect restart condition (when `nowChicago >= slotTimeChicagoTime` on same trading date)
2. Request bars up to `utcNow` on restart (not just `slotTimeChicago`)
3. Ensure BarsRequest is called on restart (not just on initial DataLoaded)
4. Add restart detection to BarsRequest logic

**Implementation:**
- Modify `RequestHistoricalBarsForPreHydration()` to detect restart
- Change `endTimeChicago` calculation to use `nowChicago` on restart
- Trigger BarsRequest from engine when restart detected
- Add logging to indicate restart-aware behavior

### Part 2: Range Validation Before Lock

**Required Changes:**
1. Validate range values are present (not null)
2. Validate range high > range low (sanity check)
3. Validate bar count > 0 (range computed from actual data)
4. Log validation details for auditability

**Implementation:**
- Add validation checks in `TryLockRange()` before Phase A commit
- If validation fails, log CRITICAL and return false
- Do NOT lock if validation fails

### Part 3: Fail-Closed Behavior

**Required Changes:**
1. Detect when `previous_state == RANGE_LOCKED` but restore fails
2. Check if bars are insufficient
3. Suspend stream instead of recomputing
4. Log CRITICAL event

**Implementation:**
- After `RestoreRangeLockedFromHydrationLog()` call
- Check if restore failed AND bars are insufficient
- Transition to `SUSPENDED_DATA_INSUFFICIENT` state
- Do NOT allow recomputation

## Key Takeaways

1. **BarsRequest is only called once** - on initial `DataLoaded`, not on restart
2. **Restart doesn't trigger BarsRequest** - `OnStateChange` is not called again
3. **System relies on live bars only** - insufficient for range calculation
4. **No validation before locking** - range can be locked with incorrect data
5. **Restoration works correctly** - but restores what was locked (even if wrong)

## Files Involved

**BarsRequest Logic:**
- `modules/robot/ninjatrader/RobotSimStrategy.cs` - Lines 280-640
- `modules/robot/ninjatrader/NinjaTraderBarRequest.cs` - BarsRequest implementation

**Pre-Hydration Logic:**
- `modules/robot/core/StreamStateMachine.cs` - Lines 1039-1072 (SIM mode handling)
- `modules/robot/core/StreamStateMachine.cs` - Lines 3494-3700 (CSV pre-hydration)

**Range Calculation:**
- `modules/robot/core/StreamStateMachine.cs` - Lines 3737-4100 (`ComputeRangeRetrospectively`)
- `modules/robot/core/StreamStateMachine.cs` - Lines 4097-4238 (`TryLockRange`)

**Restart Recovery:**
- `modules/robot/core/StreamStateMachine.cs` - Lines 385-407 (constructor restart recovery)
- `modules/robot/core/StreamStateMachine.cs` - Lines 4336-4600 (`RestoreRangeLockedFromHydrationLog`)

## Next Steps

1. **Fix BarsRequest to be called on restart**
2. **Request bars up to current time on restart**
3. **Add range validation before locking**
4. **Add fail-closed behavior on restart inconsistency**
5. **Add diagnostic logging for BarsRequest lifecycle**
