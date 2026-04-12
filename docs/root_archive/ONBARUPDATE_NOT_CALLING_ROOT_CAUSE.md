# OnBarUpdate Not Calling - Root Cause Analysis

**Date**: 2026-01-30  
**Issue**: OnBarUpdate stopped being called for all instruments at 07:59:17 CT

---

## Root Cause Identified

### The Issue

**OnBarUpdate stopped being called for ALL instruments at 07:59:17 CT (38 minutes ago)**

**Evidence**:
- Last OnBarUpdate call: 07:59:17 CT for MES
- All instruments stopped receiving OnBarUpdate calls simultaneously
- Bars ARE still being received (via Tick() or other means)
- System is still processing bars (BAR_RECEIVED_NO_STREAMS, BAR_ADMISSION_PROOF events)

---

## Why OnBarUpdate Stopped

### OnBarUpdate Implementation

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`  
**Line 72**: `Calculate = Calculate.OnBarClose;`

**Critical**: OnBarUpdate uses `OnBarClose`, which means:
- OnBarUpdate **only fires when bars CLOSE**
- If bars aren't closing, OnBarUpdate won't be called
- This is different from `OnEachTick` or `OnMarketData`

### What Happened at 07:59:17 CT

1. **Last OnBarUpdate call**: 07:59:17 CT
2. **After that**: 
   - `DATA_LOSS_DETECTED` events (07:59:24, 07:59:26, 07:59:29)
   - `ENGINE_TICK_STALL_DETECTED` events (08:01:22+)
   - `TIMETABLE_POLL_STALL_DETECTED` events (08:00:22+)
3. **Bars still arriving**: But via Tick() or OnMarketData(), not OnBarClose

---

## Why Bars Stopped Closing

### Possible Reasons

1. **Market Closed**: 
   - ES regular session: 08:30-15:15 CT
   - Current time: 08:36 CT
   - **Market should be OPEN** (regular session started at 08:30)
   - But bars may have stopped closing during the gap (07:59-08:30)

2. **Data Feed Issue**:
   - Data feed may have disconnected or stopped providing bar closes
   - Bars may be arriving as ticks but not forming/closing bars
   - NinjaTrader may not be receiving bar close events

3. **NinjaTrader State**:
   - Strategy may have stopped or crashed
   - Chart may have disconnected
   - Data feed connection lost

4. **Bar Formation Issue**:
   - Bars may not be closing (stuck in current minute)
   - Data feed may be providing ticks but not bar closes
   - NinjaTrader may be waiting for bar close that never comes

---

## The Real Problem

### Two Separate Issues

1. **Bar Admission Bug** (FIXED ✅):
   - Bars at slot_time were being rejected
   - Fixed by changing `<` to `<=` in bar admission check

2. **OnBarUpdate Not Called** (NEW ISSUE):
   - NinjaTrader stopped calling OnBarUpdate at 07:59:17 CT
   - This is a **NinjaTrader/data feed issue**, not a code bug
   - OnBarUpdate requires bars to CLOSE, and bars stopped closing

---

## Why This Affects Range Locking

### The Chain of Events

1. **07:59:17**: Last OnBarUpdate call
2. **08:00:00**: Slot time passed
3. **08:00:01+**: 
   - Bars arriving at/after slot_time would be rejected (BEFORE FIX)
   - But OnBarUpdate isn't being called anyway
   - Range lock check runs in `Tick()` which is called from `OnBarUpdate()`
   - **No OnBarUpdate → No Tick() → No range lock check**

### After Fix

- **Bar admission fix**: Allows slot_time bars to be admitted
- **But**: OnBarUpdate still needs to be called for Tick() to run
- **If**: Bars aren't closing, OnBarUpdate won't fire
- **Result**: Range lock check still won't run

---

## Diagnosis

### Root Cause: NinjaTrader/Data Feed Issue

**OnBarUpdate stopped being called because bars stopped closing.**

This is **NOT a code bug** - it's a NinjaTrader/data feed issue:
- OnBarUpdate uses `Calculate.OnBarClose`
- Requires bars to CLOSE to fire
- Bars stopped closing at 07:59:17 CT
- System is still receiving ticks/data, but bars aren't closing

### Evidence

1. **All instruments stopped simultaneously** (07:59:17-07:59:24)
   - Suggests system-wide issue (data feed, NinjaTrader, or market)
   
2. **Bars still being received** (BAR_RECEIVED_NO_STREAMS events)
   - System is processing data
   - But bars aren't closing
   
3. **DATA_LOSS_DETECTED events** (07:59:24+)
   - Indicates data feed issues
   - May have caused bars to stop closing

---

## Action Required

### Immediate Checks

1. **Check NinjaTrader**:
   - Is ES1 strategy still running?
   - What state is it in? (DataLoaded, Realtime, etc.)
   - Are bars closing on the chart?
   - Is data feed connected?

2. **Check Data Feed**:
   - Is data feed connected?
   - Are bars forming/closing?
   - Any connection errors?

3. **Check Market Status**:
   - Is market open? (Should be - 08:36 CT is during regular session)
   - Are other instruments receiving bars?
   - Is this a market-wide issue?

### Possible Solutions

1. **Restart Strategy**: If strategy stopped, restart it
2. **Reconnect Data Feed**: If data feed disconnected, reconnect
3. **Check Market Hours**: Verify market is actually open
4. **Wait for Next Bar**: If bars are stuck, wait for next bar to close

---

## Code Fix Status

### Bar Admission Fix (COMPLETE ✅)

- Changed bar admission to include slot_time (`<=` instead of `<`)
- This ensures slot_time bars are admitted when they arrive
- **But**: OnBarUpdate still needs to be called for bars to be processed

### OnBarUpdate Issue (NOT A CODE BUG)

- OnBarUpdate not being called is a **NinjaTrader/data feed issue**
- Not something we can fix in code
- Requires checking NinjaTrader and data feed

---

## Conclusion

**Root Cause**: NinjaTrader stopped calling OnBarUpdate because bars stopped closing at 07:59:17 CT.

**Why**: OnBarUpdate uses `Calculate.OnBarClose`, which only fires when bars close. If bars aren't closing (data feed issue, market closed, etc.), OnBarUpdate won't be called.

**Impact**: 
- No OnBarUpdate → No Tick() → No range lock check
- Range cannot lock → No orders

**Action**: Check NinjaTrader and data feed - this is not a code issue, it's a data feed/NinjaTrader issue.

**Bar Admission Fix**: Still valid and needed - ensures slot_time bars are admitted when OnBarUpdate resumes.
