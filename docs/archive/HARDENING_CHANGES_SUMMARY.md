# Hardening Changes Summary

## Overview
Implemented four critical hardening fixes to prevent MGC/M2K strategies from getting stuck in loading and improve overall robustness.

## Fix 1: Stop calling Instrument.GetInstrument() in DataLoaded ✅

**Problem**: Calling `Instrument.GetInstrument()` during `DataLoaded` initialization can block/hang if the instrument doesn't exist (e.g., M2K), preventing the strategy from reaching `Realtime` state.

**Solution**: 
- Removed all `Instrument.GetInstrument()` calls from `OnStateChange(State.DataLoaded)` block
- Use strategy's `Instrument.MasterInstrument.Name` directly as the source of truth
- Instrument resolution deferred to order submission time (in `Realtime` state) where it's safe

**Files Modified**:
- `modules/robot/ninjatrader/RobotSimStrategy.cs` (lines 118-125)

**Impact**: Eliminates the most likely blocking call from the critical startup phase.

---

## Fix 2: Make BarsRequest Non-Blocking and Skippable ✅

**Problem**: `BarsRequest` failures (exceptions, null returns, invalid time ranges) were throwing exceptions, preventing the strategy from reaching `Realtime` state. Pre-hydration should never prevent trading.

**Solution**:
- Changed all `throw new InvalidOperationException()` to `return` (log and continue)
- Changed error messages from `CRITICAL` to `WARNING` 
- Added `BARSREQUEST_SKIPPED` or `BARSREQUEST_FAILED` events with note: "continuing without pre-hydration bars (non-blocking)"
- Strategy now reaches `Realtime` even if BarsRequest fails completely

**Files Modified**:
- `modules/robot/ninjatrader/RobotSimStrategy.cs`:
  - Lines 370-377: Trading date not locked → skip instead of throw
  - Lines 372-384: Time range cannot be determined → skip instead of throw  
  - Lines 427-445: Invalid time range → skip instead of throw
  - Lines 582-606: BarsRequest exception → log and return instead of throw
  - Lines 609-625: BarsRequest returned null → log and return instead of throw

**Impact**: Strategy will always reach `Realtime` state, even if pre-hydration fails. Trading continues with live bars only.

---

## Fix 3: Fail Closed on Init Exceptions ✅

**Problem**: If initialization failed, the strategy would continue in a half-built state, causing confusing behavior and potential crashes.

**Solution**:
- Added `_initFailed` flag (boolean field)
- Set `_initFailed = true` in the catch block of `OnStateChange(State.DataLoaded)`
- Added `_initFailed` checks at the start of all execution paths:
  - `OnBarUpdate()` (line 776)
  - `OnMarketData()` (line 1024)
  - `CheckBreakEvenTriggersTickBased()` (line 1067)
  - `OnOrderUpdate()` (line 1241)
  - `OnExecutionUpdate()` (line 1290)
  - `SendTestNotification()` (line 1404)

**Files Modified**:
- `modules/robot/ninjatrader/RobotSimStrategy.cs`:
  - Line 36: Added `_initFailed` field
  - Line 283: Set `_initFailed = true` on init exception
  - Lines 776, 1024, 1067, 1241, 1290, 1404: Added `if (_initFailed) return;` guards

**Impact**: Strategy fails closed - if initialization fails, it logs the error and refuses to execute any trading logic, preventing half-built state issues.

---

## Fix 4: Instrument Resolution Fallback Single-Shot ✅

**Problem**: Contract month fallback logic (e.g., "M2K" → "M2K 03-26") was being attempted repeatedly, potentially causing stalls and log spam.

**Solution**:
- Removed contract month fallback from `SubmitEntryOrderReal()` in `NinjaTraderSimAdapter.NT.cs`
- Instrument resolution is now single-shot: try exact match, if fails → immediately fallback to strategy instrument
- No repeated resolution attempts

**Files Modified**:
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (lines 210-243)
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` (lines 210-243)

**Impact**: Prevents repeated resolution attempts that could cause stalls. Instrument resolution happens once per order submission.

---

## Expected Behavior After Fixes

### M2K Strategy:
1. ✅ No longer gets "stuck in loading"
2. ✅ Uses strategy's instrument name directly during initialization
3. ✅ If BarsRequest fails, logs warning and continues to `Realtime`
4. ✅ If initialization fails, sets `_initFailed` flag and refuses to execute

### MGC Strategy:
1. ✅ No longer gets "stuck in loading"
2. ✅ Uses strategy's instrument name directly during initialization
3. ✅ If BarsRequest fails, logs warning and continues to `Realtime`
4. ✅ If initialization fails, sets `_initFailed` flag and refuses to execute

### Overall Stability:
- ✅ Strategies always reach `Realtime` state (unless initialization completely fails)
- ✅ Pre-hydration failures don't prevent trading
- ✅ Failed initialization results in clear error logging and fail-closed behavior
- ✅ No repeated instrument resolution attempts

---

## Testing Checklist

1. **Load M2K Strategy**:
   - [ ] Strategy transitions from "Loading" to "Running" state
   - [ ] Check NinjaTrader Output window for any "CRITICAL: Exception during DataLoaded initialization" messages
   - [ ] Check logs for `BARSREQUEST_SKIPPED` or `BARSREQUEST_FAILED` events (if pre-hydration fails)
   - [ ] Verify strategy processes live bars in `Realtime` state

2. **Load MGC Strategy**:
   - [ ] Strategy transitions from "Loading" to "Running" state
   - [ ] Check NinjaTrader Output window for any "CRITICAL: Exception during DataLoaded initialization" messages
   - [ ] Check logs for `BARSREQUEST_SKIPPED` or `BARSREQUEST_FAILED` events (if pre-hydration fails)
   - [ ] Verify strategy processes live bars in `Realtime` state

3. **Verify Fail-Closed Behavior**:
   - [ ] If initialization fails, check that `_initFailed` flag is set
   - [ ] Verify no trading logic executes (no `OnBarUpdate`, `OnMarketData`, order updates)
   - [ ] Check logs for clear "INIT_FAILED" error messages

4. **Verify Non-Blocking BarsRequest**:
   - [ ] If BarsRequest fails, strategy still reaches `Realtime`
   - [ ] Check logs for "continuing without pre-hydration bars (non-blocking)" notes
   - [ ] Verify strategy continues with live bars only

---

## Files Modified Summary

### Core Changes:
- `modules/robot/ninjatrader/RobotSimStrategy.cs`:
  - Removed `Instrument.GetInstrument()` calls from DataLoaded
  - Made all BarsRequest failures non-blocking (return instead of throw)
  - Added `_initFailed` flag and fail-closed checks
  - Added `_initFailed` guards to all execution paths

- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`:
  - Removed contract month fallback from instrument resolution

- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`:
  - Synced: Removed contract month fallback from instrument resolution

---

## Notes

- **BarsRequest Timeout**: The existing 30-second timeout in `NinjaTraderBarRequest.cs` (line 161) remains unchanged. If BarsRequest hangs, it will timeout after 30 seconds and throw, but our non-blocking fix catches this and continues.

- **Instrument Resolution**: Instrument resolution now happens only when orders are submitted (in `Realtime` state), not during initialization. This is safer because:
  - Strategy is already in `Realtime` state
  - Order submission can handle resolution failures gracefully
  - No risk of blocking initialization

- **Fail-Closed Philosophy**: If initialization fails, the strategy logs the error clearly and refuses to execute. This prevents confusing "half-working" states where some features work and others don't.
