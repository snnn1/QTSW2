# Fresh Start BarsRequest Fix

**Date:** 2026-01-29  
**Status:** ✅ Fixed and synced

## Problem Identified

**Issue:** Range locking BEFORE BarsRequest completes for fresh starts (`is_restart: false`)

**Root Cause:**
1. Streams initialized immediately after `_engine.Start()` completes
2. `MarkBarsRequestPending()` was called INSIDE the loop, AFTER queuing BarsRequest in background thread
3. Race condition: Stream could check `IsBarsRequestPending()` BEFORE it was marked pending
4. Result: Stream proceeded without waiting, locked range with insufficient bars (3 instead of 180+)

**Timeline (NQ2 example):**
- 18:27:48 - Stream initialized (`is_restart: false`)
- 18:27:48 - `PRE_HYDRATION_COMPLETE` with `bar_count: 0`
- 18:27:52 - `RANGE_LOCKED` with only 3 bars ❌
- 18:40:37 - BarsRequest requested (13 minutes AFTER range locked!) ❌

## Fixes Implemented

### ✅ Fix 1: Mark BarsRequest Pending BEFORE Queuing
**File:** `modules/robot/ninjatrader/RobotSimStrategy.cs` (lines 243-258)

**Change:**
- Mark ALL instruments as pending **synchronously** BEFORE queuing any BarsRequest
- This ensures streams wait even if they process ticks before BarsRequest completes

**Code:**
```csharp
// CRITICAL: Mark ALL instruments as pending BEFORE queuing BarsRequest
foreach (var instrument in executionInstruments)
{
    // Mark as pending immediately (synchronously) before queuing BarsRequest
    _engine.MarkBarsRequestPending(instrument, DateTimeOffset.UtcNow);
    Log($"BarsRequest marked as pending for {instrument} (before queuing)", LogLevel.Information);
}

// THEN queue BarsRequest in background thread
foreach (var executionInstrument in executionInstruments)
{
    ThreadPool.QueueUserWorkItem(_ => {
        RequestHistoricalBarsForPreHydration(instrument);
    });
}
```

### ✅ Fix 2: Check Both Canonical and Execution Instruments
**Files:**
- `modules/robot/core/StreamStateMachine.cs` (lines 1117-1120, 4228-4229)
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` (synced)

**Change:**
- `IsBarsRequestPending()` now checks BOTH `CanonicalInstrument` and `ExecutionInstrument`
- BarsRequest might be marked pending with either one (due to canonical mapping)

**Code:**
```csharp
// CRITICAL: Check both CanonicalInstrument and ExecutionInstrument
// BarsRequest might be marked pending with either one
var isPending = _engine != null && (
    _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
    _engine.IsBarsRequestPending(ExecutionInstrument, utcNow)
);

if (isPending)
{
    // Wait for BarsRequest...
}
```

### ✅ Fix 3: Enhanced Diagnostic Logging
**Files:** Both StreamStateMachine.cs files

**Changes:**
- Added `execution_instrument` to `PRE_HYDRATION_WAITING_FOR_BARSREQUEST` events
- Added `canonical_pending` and `execution_pending` flags to `RANGE_LOCK_BLOCKED_BARSREQUEST_PENDING` events
- Enhanced `PRE_HYDRATION_COMPLETE_SET` to check both instruments

## Protection Layers

### Layer 1: Pre-Hydration Wait
- `HandlePreHydrationState()` checks `IsBarsRequestPending()` before marking `_preHydrationComplete = true`
- Stream stays in `PRE_HYDRATION` state until BarsRequest completes

### Layer 2: TryLockRange Guard
- `TryLockRange()` double-checks `IsBarsRequestPending()` before locking range
- Prevents premature lock even if pre-hydration check somehow passes

### Layer 3: Timeout Protection
- 5-minute timeout prevents indefinite blocking
- If BarsRequest times out, stream can proceed (with warning)

## Expected Behavior After Fix

### Fresh Start Flow:
1. ✅ `_engine.Start()` creates streams
2. ✅ `GetAllExecutionInstrumentsForBarsRequest()` gets instruments
3. ✅ **Mark ALL as pending synchronously** (NEW)
4. ✅ Queue BarsRequest in background thread
5. ✅ Stream checks `IsBarsRequestPending()` → returns `true` → waits
6. ✅ BarsRequest completes → `MarkBarsRequestCompleted()` called
7. ✅ Stream checks again → returns `false` → proceeds
8. ✅ Range locks with sufficient bars ✅

### Restart Flow (unchanged):
1. ✅ Stream detects restart → logs `RESTART_BARSREQUEST_NEEDED`
2. ✅ Strategy checks `GetInstrumentsNeedingRestartBarsRequest()` on Realtime transition
3. ✅ Marks pending → queues BarsRequest
4. ✅ Stream waits → BarsRequest completes → Range locks ✅

## Files Modified

1. ✅ `modules/robot/ninjatrader/RobotSimStrategy.cs`
   - Mark pending BEFORE queuing BarsRequest
   - Removed duplicate marking loop

2. ✅ `modules/robot/core/StreamStateMachine.cs`
   - Check both CanonicalInstrument and ExecutionInstrument
   - Enhanced diagnostic logging

3. ✅ `RobotCore_For_NinjaTrader/StreamStateMachine.cs`
   - Synced all changes from core version

## Testing Checklist

- [ ] Fresh start: Stream waits for BarsRequest before range lock
- [ ] Restart: Stream waits for BarsRequest before range lock
- [ ] Diagnostic logs show `PRE_HYDRATION_WAITING_FOR_BARSREQUEST` events
- [ ] Range locks with sufficient bars (180+ for 3-hour window)
- [ ] No premature range locks with insufficient bars

## Conclusion

✅ **All fixes implemented and synced**

The system now:
1. Marks BarsRequest pending BEFORE queuing (prevents race condition) ✅
2. Checks both canonical and execution instruments ✅
3. Has enhanced diagnostic logging ✅
4. Has multiple protection layers ✅

**Ready for testing!**
