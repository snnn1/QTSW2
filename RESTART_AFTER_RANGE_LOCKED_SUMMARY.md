# Restart After Range Locked (No Breakout Yet) - Expected Behavior

## Scenario
Robot restarts **after** a range has been locked but **before** a breakout has occurred.

## Expected Flow

### 1. **On Restart (Constructor)**
   - ✅ Detects `isRestart = true` and `existing.LastState == "RANGE_LOCKED"`
   - ✅ Calls `RestoreRangeLockedFromHydrationLog(tradingDay, streamId)`
   
### 2. **Restoration Process**
   - ✅ Reads `hydration_{day}.jsonl` (or falls back to `ranges_{day}.jsonl`)
   - ✅ Finds the **most recent** `RANGE_LOCKED` event for this stream
   - ✅ Restores:
     - `_rangeLocked = true` (authoritative lock flag)
     - `RangeHigh`, `RangeLow`, `FreezeClose` (immutable range values)
     - `_brkLongRounded`, `_brkShortRounded` (breakout levels)
     - `_breakoutLevelsMissing` flag (if breakout computation failed)
     - `_rangeLockCommitted = true` (prevents duplicate lock attempts)
   - ✅ Transitions state to `RANGE_LOCKED`
   - ✅ Logs `RANGE_LOCKED_RESTORED_FROM_HYDRATION` or `RANGE_LOCKED_RESTORED_FROM_RANGES`

### 3. **After Restoration**
   - ✅ Stream is in `RANGE_LOCKED` state
   - ✅ Range values are **immutable** (`_rangeLocked = true` prevents any mutations)
   - ✅ Breakout levels are restored (if they were computed before restart)
   - ✅ `TryLockRange()` will return `true` immediately (early exit check: `if (_rangeLocked) return true;`)

### 4. **Breakout Detection Continues**
   - ✅ `HandleRangeLockedState()` continues to run on each bar (called from `OnTick`/`OnBar`)
   - ✅ Breakout detection logic runs normally using restored `_brkLongRounded` and `_brkShortRounded`
   - ✅ If breakout detected → proceeds with entry logic (same as normal flow)
   - ✅ If breakout levels missing (`_breakoutLevelsMissing = true`):
     - ⚠️ Trading gate blocks entries (no brackets submitted)
     - ⚠️ Logs `RANGE_LOCKED_DERIVATION_FAILED` (if not already logged)
     - ⚠️ Stream waits but cannot trade until breakout levels are available

### 5. **Order Submission (Restart Recovery)**
   - ✅ `HandleRangeLockedState()` checks if orders need to be resubmitted:
     - ✅ If `_stopBracketsSubmittedAtLock = false` (orders weren't submitted before restart)
     - ✅ If `_entryDetected = false` (no entry yet)
     - ✅ If `utcNow < MarketCloseUtc` (before market close)
     - ✅ If breakout levels exist (`_brkLongRounded` and `_brkShortRounded` have values)
   - ✅ **Idempotency Check**: Verifies orders weren't already submitted via execution journal
   - ✅ If all conditions met → calls `SubmitStopEntryBracketsAtLock()` with restored breakout levels
   - ✅ Logs `RESTART_RETRY_STOP_BRACKETS` when retrying order submission
   - ✅ If `_stopBracketsSubmittedAtLock = true` before restart:
     - ✅ Orders were already submitted, won't resubmit (idempotency prevents duplicates)

### 6. **Entry Detection**
   - ✅ `_entryDetected` flag is restored from journal or execution journal
   - ✅ If entry was filled before restart → stream proceeds to position management
   - ✅ If no entry yet → continues watching for breakout

## Key Invariants

1. **Range Immutability**: Once `_rangeLocked = true`, range values (`RangeHigh`, `RangeLow`, `FreezeClose`) **cannot change** for the rest of the trading day
2. **No Re-locking**: `TryLockRange()` will not recompute the range if `_rangeLocked = true`
3. **Breakout Detection Continues**: Breakout detection logic runs normally after restoration
4. **Idempotent Orders**: Order submission checks `_stopBracketsSubmittedAtLock` to prevent duplicates

## Edge Cases

### Case 1: Breakout Levels Missing
- **Before restart**: Range locked but breakout computation failed (`_breakoutLevelsMissing = true`)
- **After restart**: 
  - ✅ Range restored correctly
  - ⚠️ Breakout levels still missing
  - ⚠️ Trading gate blocks entries
  - ⚠️ Stream cannot trade until breakout levels are computed (shouldn't happen in normal flow)

### Case 2: Orders Already Submitted
- **Before restart**: Range locked, breakout levels computed, orders submitted
- **After restart**:
  - ✅ Range restored
  - ✅ Breakout levels restored
  - ✅ `_stopBracketsSubmittedAtLock = true` prevents resubmission
  - ✅ Stream continues watching for order fills

### Case 3: Entry Already Filled
- **Before restart**: Range locked, breakout occurred, entry filled
- **After restart**:
  - ✅ Range restored
  - ✅ `_entryDetected = true` restored from journal
  - ✅ Stream proceeds to position management (stop/target/breakeven)

## Logging

After successful restoration, you should see:
- `RANGE_LOCKED_RESTORED_FROM_HYDRATION` or `RANGE_LOCKED_RESTORED_FROM_RANGES`
- Log includes: `range_high`, `range_low`, `source` (hydration_log or ranges_log)

If restoration fails:
- `RANGE_LOCKED_RESTORE_FAILED` warning
- Falls back to journal `LastState` check (but cannot fully restore without log data)

## Summary

**After restart with range locked but no breakout:**
1. ✅ Range is restored from canonical log (hydration/ranges)
2. ✅ Stream state is `RANGE_LOCKED`
3. ✅ Breakout detection continues normally
4. ✅ Orders are submitted if not already submitted
5. ✅ Breakout can occur and entry can proceed normally
6. ✅ Range values remain immutable (cannot change)

The stream seamlessly continues from where it left off, with all range data and breakout levels restored.
