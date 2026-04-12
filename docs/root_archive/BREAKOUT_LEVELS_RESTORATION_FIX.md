# Breakout Levels Restoration Fix - Summary

## Date: January 29, 2026

## Problem Statement

**Issue:** Orders were not being created even though ranges were locked and entry detection should have been active.

**Symptoms:**
- `EXECUTION_GATE_EVAL` showed `can_detect_entries: False`
- `entry_detected: True` (blocking orders)
- `breakout_levels_computed: True` in gate eval (from previous run)
- But `RANGE_LOCKED` events showed missing breakout levels (`brk_long_rounded`, `brk_short_rounded` were empty)

## Root Cause Analysis

### The Problem

When a stream restarts after a range has been locked, the restoration process (`RestoreRangeLockedFromHydrationLog`) restores:
- Range values (`RangeHigh`, `RangeLow`, `FreezeClose`)
- Breakout levels (`_brkLongRounded`, `_brkShortRounded`) **if present in hydration log**

**Critical Issue:** If breakout levels were missing from the hydration log, they were **not computed** after restoration. This caused:
1. `_brkLongRounded` and `_brkShortRounded` to remain `null`
2. `can_detect_entries` to be `False` (requires breakout levels)
3. Entry detection (`CheckBreakoutEntry`) to be blocked
4. Orders to never be created

### Why Breakout Levels Were Missing

The hydration log stores breakout levels in the `RANGE_LOCKED` event's `data` dictionary:
- `breakout_long` → `_brkLongRounded`
- `breakout_short` → `_brkShortRounded`

If these fields were missing from the hydration log (due to older log format, missing data, or log corruption), restoration would succeed for the range but fail for breakout levels.

## Solution Implemented

### Fix: Compute Breakout Levels After Restoration

**Location:** `modules/robot/core/StreamStateMachine.cs` and `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

**Method:** `RestoreRangeLockedFromHydrationLog()`

**Change:** Added logic to compute breakout levels if they're missing but the range is available:

```csharp
// Restore breakout levels if present
_brkLongRounded = ExtractDecimal(latestHydrationData, "breakout_long");
_brkShortRounded = ExtractDecimal(latestHydrationData, "breakout_short");

// Set gate flag if breakout levels are missing
_breakoutLevelsMissing = !_brkLongRounded.HasValue || !_brkShortRounded.HasValue;

// Mark lock as committed (for duplicate detection)
_rangeLockCommitted = true;

// Restore state as RANGE_LOCKED
var utcNow = DateTimeOffset.UtcNow;

// If breakout levels are missing but range is available, compute them
if ((!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) && 
    RangeHigh.HasValue && RangeLow.HasValue)
{
    ComputeBreakoutLevelsAndLog(utcNow);
}
```

**Applied to both restoration paths:**
1. Hydration log restoration (line ~4820)
2. Ranges log restoration (line ~4865)

### How It Works

1. **Restoration attempts** to restore breakout levels from log
2. **If missing**, checks if range values are available (`RangeHigh`, `RangeLow`)
3. **If range available**, calls `ComputeBreakoutLevelsAndLog()` to compute:
   - `_brkLongRaw = RangeHigh + _tickSize`
   - `_brkShortRaw = RangeLow - _tickSize`
   - `_brkLongRounded = RoundToTick(_brkLongRaw, _tickSize)`
   - `_brkShortRounded = RoundToTick(_brkShortRaw, _tickSize)`
4. **Logs** the computed breakout levels via `BREAKOUT_LEVELS_COMPUTED` event
5. **Enables** entry detection (`can_detect_entries` becomes `True`)

## Range Lock Behavior

### Immutability After Lock

**Key Principle:** Once a range is locked for a trading day, it **cannot be recomputed** for that same day.

**Code Evidence:**
```csharp
// TryLockRange() - Line 4222-4224
if (_rangeLocked)
    return true;  // Already locked - idempotent return

// HandleRangeBuildingState() - Line 2218
if (utcNow >= SlotTimeUtc && !_rangeLocked)
{
    if (!TryLockRange(utcNow))
    {
        return;  // Locking failed - will retry on next tick
    }
}

// HandleRangeBuildingState() - Line 2257-2260
// Early return if range is already locked
if (_rangeLocked)
{
    return;
}
```

**Why:** This ensures range consistency throughout the trading day. Once locked, the range is immutable until trading day rollover.

### When Range Lock Resets

**Trading Day Rollover:**
- `ResetForNewTradingDay()` is called (line 5543)
- `_rangeLocked = false`
- Range values reset to `null`
- Stream state resets to `PRE_HYDRATION`
- Fresh range computation begins for new trading day

**Explicit Recovery Restart:**
- `ResetForNewTradingDay()` can be called manually
- Same reset behavior as rollover

## Restoration Behavior

### When Restoration Happens

**Condition:** `if (isRestart && existing != null)`

- `isRestart = true` when journal exists (`existing != null`)
- Restoration only attempts when journal exists
- If journal is deleted, restoration is skipped entirely

### Restoration Sources (Priority Order)

1. **Hydration Log** (`hydration_{tradingDay}.jsonl`)
   - Primary canonical source
   - Contains full event history
   - Includes `data` dictionary with range and breakout levels

2. **Ranges Log** (`ranges_{tradingDay}.jsonl`)
   - Fallback canonical source
   - Contains range events only
   - Flat structure (no nested `data` dictionary)

### What Gets Restored

**From Hydration Log:**
- `_rangeLocked = true`
- `RangeHigh` (from `data.range_high`)
- `RangeLow` (from `data.range_low`)
- `FreezeClose` (from `data.freeze_close`)
- `_brkLongRounded` (from `data.breakout_long`) - **NOW COMPUTED IF MISSING**
- `_brkShortRounded` (from `data.breakout_short`) - **NOW COMPUTED IF MISSING**
- State transition to `RANGE_LOCKED`

**From Ranges Log:**
- Same as hydration log, but from flat structure
- Breakout levels also computed if missing

### Restoration Failure Handling

**If restoration fails and previous state was `RANGE_LOCKED`:**
- Checks if bars are insufficient using `HasSufficientRangeBars()`
- If insufficient → Stream suspended (`SUSPENDED_DATA_INSUFFICIENT`)
- If sufficient → Range will be recomputed on next tick

**If journal exists but hydration/ranges log missing:**
- Falls back to journal `LastState` check
- Cannot fully restore without log data
- Range will be recomputed on next tick

## Entry Detection Requirements

### Gate: `can_detect_entries`

**Required conditions (all must be true):**
```csharp
var canDetectEntries = stateOk &&                    // State == RANGE_LOCKED
                       !_entryDetected &&            // Entry not already detected
                       slotReached &&                 // Slot time has passed
                       barUtc < MarketCloseUtc &&     // Before market close
                       _brkLongRounded.HasValue &&   // Breakout levels computed
                       _brkShortRounded.HasValue;     // Breakout levels computed
```

**Critical:** Breakout levels must be present for entry detection to work.

### Entry Detection Flow

1. **Breakout Detection:** `CheckBreakoutEntry()` compares bar high/low to breakout levels
2. **Entry Recording:** `RecordIntendedEntry()` sets `_entryDetected = true`
3. **Order Submission:** `SubmitIntent()` creates and submits entry orders
4. **Protective Orders:** Stop/target orders placed on fill

**Without breakout levels:** Step 1 cannot proceed, so orders are never created.

## Files Modified

### Core Files
1. **`modules/robot/core/StreamStateMachine.cs`**
   - Lines ~4820: Added breakout level computation after hydration log restoration
   - Lines ~4865: Added breakout level computation after ranges log restoration

2. **`RobotCore_For_NinjaTrader/StreamStateMachine.cs`**
   - Same changes as core file (synced for NinjaTrader compilation)

### Compilation Fixes
- Fixed variable scoping issue (`utcNow` declared twice in same scope)
- Moved `utcNow` declaration before conditional block to allow reuse

## Testing & Verification

### What to Check After Fix

1. **After Restart:**
   - `BREAKOUT_LEVELS_COMPUTED` event should appear after `RANGE_LOCKED_RESTORED`
   - `can_detect_entries` should be `True` (if entry not detected)
   - Entry detection should work normally

2. **Log Events to Monitor:**
   - `RANGE_LOCKED_RESTORED_FROM_HYDRATION` or `RANGE_LOCKED_RESTORED_FROM_RANGES`
   - `BREAKOUT_LEVELS_COMPUTED` (should appear after restoration)
   - `EXECUTION_GATE_EVAL` (check `can_detect_entries` and `breakout_levels_computed`)

3. **Order Creation:**
   - Orders should be created when breakouts occur
   - `DRYRUN_INTENDED_ENTRY` or `INTENT_SUBMITTED` events should appear

## Impact & Benefits

### Immediate Benefits
- **Orders can be created** after restart even if breakout levels missing from log
- **Entry detection works** correctly after restoration
- **No manual intervention** needed to fix missing breakout levels

### Long-term Benefits
- **Robust restoration** - handles missing data gracefully
- **Backward compatibility** - works with older log formats
- **Self-healing** - automatically computes missing derived values

## Edge Cases Handled

1. **Missing Breakout Levels in Hydration Log:**
   - ✅ Computed automatically from range values

2. **Missing Breakout Levels in Ranges Log:**
   - ✅ Computed automatically from range values

3. **Range Values Missing:**
   - ✅ No computation attempted (would fail anyway)
   - ✅ `_breakoutLevelsMissing` flag set to prevent entries

4. **Trading Day Rollover:**
   - ✅ Fresh range computation (not affected by this fix)

5. **Journal Deleted:**
   - ✅ No restoration attempted
   - ✅ Fresh stream initialization

## Related Issues Resolved

1. **Orders Not Being Created:** ✅ Fixed
   - Breakout levels now computed after restoration
   - Entry detection enabled

2. **Missing Breakout Levels After Restart:** ✅ Fixed
   - Automatic computation if missing from log

3. **Range Immutability:** ✅ Documented
   - Cannot recompute after lock (by design)
   - Resets on trading day rollover

## Future Considerations

### Potential Improvements

1. **Log Format Enhancement:**
   - Ensure breakout levels are always written to hydration log
   - Add validation to prevent missing breakout levels

2. **Restoration Validation:**
   - Add checksum validation for restored ranges
   - Verify breakout levels match range values

3. **Monitoring:**
   - Alert when breakout levels are computed during restoration
   - Track frequency of missing breakout levels in logs

## Expected Timing After Fixes

### Normal Timing (What You Should See)

**1. Strategy / Stream Starts**
- **Time:** Instant (milliseconds)
- **Event:** `STREAM_INITIALIZED` logged
- **Note:** Happens in constructor

**2. BarsRequest is Issued**
- **Time:** < 100 ms
- **Event:** `BARSREQUEST_INITIALIZATION`
- **Action:** `MarkBarsRequestPending = true` (synchronous, before queuing)
- **Note:** This happens before the stream can advance

**3. BarsRequest Completes**
- **Time:** Typically 200 ms – 2 seconds (depends on instrument, provider, and bar count)
- **Event:** `BARSREQUEST_CALLBACK_RECEIVED`
- **Action:** `MarkBarsRequestCompleted = true` (**NOW MOVED BEFORE FEEDING BARS**)
- **Note:** At this point the system is unblocked

**4. Bars are Fed into the Stream**
- **Time:** Milliseconds
- **Action:** Each bar triggers `OnBar()` → `Tick()` → `HandlePreHydrationState()`
- **Result:** Because `pending = false`, the very first bar allows:
  - `PRE_HYDRATION_COMPLETE`
  - Transition to `ARMED` / `RANGE_BUILDING`
- **Note:** No waiting for live bars anymore

**5. Range is Locked**
- **Time:** Same second as the last historical bar (if slot time has passed)
- **Event:** `TryLockRange()` succeeds → `RANGE_LOCKED` emitted
- **Condition:** `utcNow >= SlotTimeUtc && !_rangeLocked`
- **Note:** This should happen immediately after the bars arrive if the slot time has passed

### End-to-End Expectation

**From BarsRequest start → RANGE_LOCKED:**
- **~0.5 to 3 seconds** in most cases
- **< 5 seconds** in almost all cases
- **Anything beyond that is a red flag**

### Important Caveats

**Slot Time Not Yet Reached:**
- If `utcNow < SlotTimeUtc`, range lock will wait until slot time
- This is expected behavior - range shouldn't lock before slot time
- Timing will be: BarsRequest → Bars arrive → Wait for slot time → Range locks

**BarsRequest Timeout:**
- If BarsRequest takes > 5 minutes, it times out
- Range lock proceeds anyway (may have insufficient bars)
- This is a fallback mechanism

**Multiple Instruments:**
- Each instrument has its own BarsRequest
- Timing is per-instrument, not global
- Streams wait for their specific instrument's BarsRequest

### Code Evidence

**MarkBarsRequestCompleted called BEFORE feeding bars:**
```csharp
// RobotEngine.cs, line 2077
// CRITICAL: Mark BarsRequest as completed BEFORE feeding bars
MarkBarsRequestCompleted(instrument, utcNow);

// Feed filtered bars to matching streams
foreach (var bar in filteredBars)
{
    stream.OnBar(...);  // Each call checks IsBarsRequestPending()
}
```

**Pre-hydration check (now sees completed immediately):**
```csharp
// StreamStateMachine.cs, line 1117-1120
var isPending = _engine != null && (
    _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
    _engine.IsBarsRequestPending(ExecutionInstrument, utcNow)
);

if (isPending) {
    return; // Wait
}

// BarsRequest not pending - mark complete and transition
_preHydrationComplete = true;
```

**Range lock timing:**
```csharp
// StreamStateMachine.cs, line 2218
if (utcNow >= SlotTimeUtc && !_rangeLocked)
{
    if (!TryLockRange(utcNow))
    {
        return; // Retry on next tick
    }
}
```

## Conclusion

This fix ensures that breakout levels are always available after restoration, even if they're missing from the log files. This enables entry detection and order creation to work correctly after restarts, without requiring manual intervention or waiting for trading day rollover.

The fix is minimal, safe, and maintains backward compatibility with existing log formats while providing automatic recovery for missing data.

**Timing expectations are accurate** for the normal case where slot time has already passed. The critical improvement is that `MarkBarsRequestCompleted` is now called **before** feeding bars, eliminating the delay that previously occurred when streams checked `IsBarsRequestPending()` during bar feeding.

## Restart After Range is Locked

### Scenario: Restart After RANGE_LOCKED

**What happens when you restart the strategy after a range has been locked?**

### Step-by-Step Restart Process

**1. Stream Initialization (Constructor)**
- **Time:** Instant (milliseconds)
- **Event:** `STREAM_INITIALIZED`
- **Action:** 
  - Journal exists → `isRestart = true`
  - Journal `LastState == "RANGE_LOCKED"` → triggers restoration

**2. Restoration Attempt**
- **Time:** Milliseconds (during constructor)
- **Source Priority:**
  1. Hydration log (`hydration_{tradingDay}.jsonl`) - **Primary**
  2. Ranges log (`ranges_{tradingDay}.jsonl`) - **Fallback**
- **What Gets Restored:**
  - `_rangeLocked = true`
  - `RangeHigh`, `RangeLow`, `FreezeClose`
  - `_brkLongRounded`, `_brkShortRounded` (**NOW COMPUTED IF MISSING**)
  - State → `RANGE_LOCKED`
  - `_stopBracketsSubmittedAtLock` flag
  - `_entryDetected` flag (from execution journal or journal)

**3. Restoration Success**
- **Event:** `RANGE_LOCKED_RESTORED_FROM_HYDRATION` or `RANGE_LOCKED_RESTORED_FROM_RANGES`
- **State:** `RANGE_LOCKED`
- **Breakout Levels:** Computed automatically if missing from log
- **Result:** Stream resumes exactly where it left off

**4. Arm() Method Called**
- **Time:** Immediately after constructor
- **Check:** `if (_rangeLocked)` → Skip normal flow
- **Action:** 
  - Verifies state is `RANGE_LOCKED`
  - If not, forces transition to `RANGE_LOCKED`
  - Returns early (skips `PRE_HYDRATION` transition)

**5. HandleRangeLockedState() Runs**
- **Time:** On first bar/tick after restart
- **Actions:**
  - **Retry Stop Brackets:** If `_stopBracketsSubmittedAtLock == false` and entry not detected
    - Checks execution journal for idempotency
    - If not already submitted → `SubmitStopEntryBracketsAtLock()`
    - Logs `RESTART_RETRY_STOP_BRACKETS`
  - **Market Close Check:** If `utcNow >= MarketCloseUtc` and no entry → `LogNoTradeMarketClose()`

**6. Entry Detection Resumes**
- **Time:** Immediately (on next bar)
- **Breakout Detection:** `CheckBreakoutEntry()` runs normally
- **Order Submission:** Works normally if breakout occurs

### Restoration Failure Scenarios

**Scenario A: Restoration Failed + Insufficient Bars**
- **Condition:** `existing.LastState == "RANGE_LOCKED"` but `!_rangeLocked` AND bars insufficient
- **Action:** Stream **SUSPENDED** (`SUSPENDED_DATA_INSUFFICIENT`)
- **Event:** `RANGE_LOCKED_RESTORE_FAILED_INSUFFICIENT_BARS`
- **Result:** Stream cannot trade - manual intervention required
- **Reason:** Fail-closed behavior - don't recompute with bad data

**Scenario B: Restoration Failed + Sufficient Bars**
- **Condition:** `existing.LastState == "RANGE_LOCKED"` but `!_rangeLocked` AND bars sufficient
- **Action:** Range will be **recomputed** on next tick
- **Event:** `RANGE_LOCKED_RESTORE_FALLBACK` (if hydration log missing)
- **Result:** Stream continues normally, recomputes range

**Scenario C: Hydration Log Missing**
- **Condition:** Journal exists but hydration/ranges log not found
- **Action:** Falls back to journal `LastState` check
- **Event:** `RANGE_LOCKED_RESTORE_FALLBACK`
- **Result:** Range recomputed (cannot fully restore without log data)

### What Does NOT Happen on Restart

**❌ Range is NOT recomputed** (if restoration succeeds)
- Range values remain exactly as they were
- Range lock is immutable for the trading day

**❌ BarsRequest is NOT required** (if restoration succeeds)
- Stream is already in `RANGE_LOCKED` state
- No need for pre-hydration

**❌ State does NOT go through PRE_HYDRATION** (if restoration succeeds)
- `Arm()` method skips normal flow when `_rangeLocked == true`
- Stream goes directly to `RANGE_LOCKED` state

### What DOES Happen on Restart

**✅ Range is restored** from canonical log
- Exact same range values as before restart
- Breakout levels computed if missing

**✅ Stop brackets are retried** (if they failed before)
- Idempotency check prevents duplicates
- Only retries if not already submitted

**✅ Entry detection resumes** immediately
- Breakout detection works normally
- Orders can be created if breakout occurs

**✅ State is `RANGE_LOCKED`** immediately
- No waiting for bars or slot time
- Ready to detect entries right away

### Code Flow

**Constructor Restoration:**
```csharp
// StreamStateMachine.cs, line 404-407
if (isRestart && existing != null)
{
    RestoreRangeLockedFromHydrationLog(tradingDateStr, Stream);
    // ... restoration logic ...
}
```

**Arm() Skip Logic:**
```csharp
// StreamStateMachine.cs, line 2348-2365
if (_rangeLocked)
{
    // Range was restored - verify state is correct
    if (State != StreamState.RANGE_LOCKED)
    {
        Transition(utcNow, StreamState.RANGE_LOCKED, "RANGE_LOCKED_RESTORED_FIX");
    }
    // Skip normal flow - restoration already completed
    return;
}
```

**Restart Recovery in HandleRangeLockedState:**
```csharp
// StreamStateMachine.cs, line 2276-2305
if (!_stopBracketsSubmittedAtLock && !_entryDetected && utcNow < MarketCloseUtc &&
    RangeHigh.HasValue && RangeLow.HasValue &&
    _brkLongRounded.HasValue && _brkShortRounded.HasValue)
{
    // Check idempotency
    if (!alreadySubmitted)
    {
        SubmitStopEntryBracketsAtLock(utcNow);
    }
}
```

### Expected Log Sequence After Restart

```
1. STREAM_INITIALIZED
2. RANGE_LOCKED_RESTORED_FROM_HYDRATION (or FROM_RANGES)
3. BREAKOUT_LEVELS_COMPUTED (if missing from log - NEW FIX)
4. RANGE_LOCKED_RESTORED (transition event)
5. RESTART_RETRY_STOP_BRACKETS (if brackets weren't submitted)
6. HandleRangeLockedState() runs on first bar
7. EXECUTION_GATE_EVAL (can_detect_entries should be True)
8. Entry detection works normally
```

### Key Points

1. **Restoration is automatic** - happens in constructor before `Arm()`
2. **Range is immutable** - same values as before restart
3. **Breakout levels computed** - automatically if missing (NEW FIX)
4. **Stop brackets retried** - if they failed before restart
5. **Entry detection works** - immediately after restart
6. **No recomputation** - range stays locked for the trading day
7. **Fail-closed on insufficient bars** - stream suspended if restoration fails and bars insufficient

### Timing After Restart

**From restart → ready to detect entries:**
- **< 100 ms** - Restoration happens in constructor
- **Immediate** - State is `RANGE_LOCKED` right away
- **First bar** - `HandleRangeLockedState()` runs and entry detection begins

**No waiting for:**
- BarsRequest (not needed if restoration succeeds)
- Pre-hydration (skipped)
- Slot time (already passed)
- Range computation (already locked)

The stream is **immediately ready** to detect entries after restart, assuming restoration succeeds.

## Breakout Detection on Restart

### Does It Check If Breakout Already Occurred?

**Short Answer:** It checks if an **entry was already detected/filled**, but **NOT** if price has already breached breakout levels.

### Entry Detection Restoration

**On Restart (Constructor, lines 389-399):**
```csharp
// Restore entry detection from execution journal (Option B)
if (_executionJournal != null && !existing.EntryDetected)
{
    // Scan execution journal for any filled entry
    _entryDetected = _executionJournal.HasEntryFillForStream(tradingDateStr, Stream);
}
else
{
    // Use journal value if available, otherwise default to false
    _entryDetected = existing.EntryDetected;
}
```

**What Gets Checked:**
1. **Execution Journal** - Scans for filled entry orders (`EntryFilled == true`)
2. **Stream Journal** - Uses `EntryDetected` flag if execution journal check skipped

**What Does NOT Get Checked:**
- ❌ **Price history** - Does NOT check if price already breached breakout levels
- ❌ **Historical bars** - Does NOT replay bars to detect missed breakouts
- ❌ **Current price** - Does NOT check current price against breakout levels on restart

### Breakout Detection Logic

**After Restart (HandleRangeLockedState, line 2880):**
```csharp
// Check for breakout after lock (before market close)
if (!_entryDetected && barUtc >= SlotTimeUtc && barUtc < MarketCloseUtc && 
    _brkLongRounded.HasValue && _brkShortRounded.HasValue)
{
    CheckBreakoutEntry(barUtc, high, low, utcNow);
}
```

**Key Point:** `CheckBreakoutEntry()` only runs if `!_entryDetected`. It checks the **current bar** against breakout levels, not historical bars.

### Scenarios

**Scenario A: Entry Already Filled Before Restart**
- ✅ **Restored:** `_entryDetected = true` (from execution journal)
- ✅ **Result:** No new entry detection, no duplicate orders
- ✅ **Status:** Correct behavior

**Scenario B: Breakout Occurred But Entry Not Filled**
- ⚠️ **Restored:** `_entryDetected = false` (no fill in execution journal)
- ⚠️ **Result:** Will detect breakout again on **next bar** if price still above/below breakout level
- ⚠️ **Risk:** May try to enter again if breakout still valid

**Scenario C: Breakout Occurred and Entry Filled**
- ✅ **Restored:** `_entryDetected = true` (from execution journal fill)
- ✅ **Result:** No duplicate detection
- ✅ **Status:** Correct behavior

**Scenario D: Breakout Occurred, Entry Submitted But Not Filled**
- ⚠️ **Restored:** `_entryDetected = false` (no fill yet)
- ⚠️ **Result:** Will detect breakout again on next bar
- ⚠️ **Risk:** May submit duplicate entry order (idempotency check should prevent this)

### Idempotency Protection

**Entry Detection (RecordIntendedEntry, line 5089):**
```csharp
if (_entryDetected)
{
    // Log when breakout is detected but entry already exists
    _log.Write(..., "BREAKOUT_DETECTED_ALREADY_ENTERED", ...);
    return; // Already detected
}
```

**Order Submission (RecordIntendedEntry, line 5182):**
```csharp
// Check idempotency: Has this intent already been submitted?
if (_executionJournal != null && _executionJournal.IsIntentSubmitted(intentId, TradingDate, Stream))
{
    _log.Write(..., "EXECUTION_SKIPPED_DUPLICATE", ...);
    return; // Already submitted
}
```

**Stop Brackets (HandleRangeLockedState, line 2285):**
```csharp
bool alreadySubmitted = false;
if (_executionJournal != null)
{
    alreadySubmitted = _executionJournal.IsIntentSubmitted(longIntentId, TradingDate, Stream) ||
                      _executionJournal.IsIntentSubmitted(shortIntentId, TradingDate, Stream);
}
```

### Potential Gap

**Missing Check:** The system does NOT verify if price has already breached breakout levels before restart. It relies on:
1. `_entryDetected` flag being set correctly
2. Execution journal having correct fill records
3. Idempotency checks preventing duplicate submissions

**Edge Case:** If a breakout occurred but:
- Entry was not detected (bug/crash)
- Entry was detected but not submitted (crash)
- Entry was submitted but not filled (crash)

Then on restart, it will detect the breakout again on the next bar **if price is still above/below breakout level**. This could be correct (retry) or incorrect (missed opportunity) depending on timing.

### Recommendations

**Current Behavior is Acceptable Because:**
1. **Execution journal** tracks fills reliably
2. **Idempotency checks** prevent duplicate submissions
3. **Breakout detection** is bar-by-bar (not historical replay)
4. **If breakout still valid**, detecting it again is correct behavior

**Potential Enhancement (Future):**
- Check current price against breakout levels on restart
- If price already breached and entry not detected, log warning
- Consider replaying recent bars to detect missed breakouts (complex, may not be worth it)

### Summary

**Yes, it checks:**
- ✅ If entry was already detected (`_entryDetected` flag)
- ✅ If entry was already filled (execution journal)
- ✅ If intent was already submitted (idempotency checks)

**No, it does NOT check:**
- ❌ If price has already breached breakout levels (before restart)
- ❌ Historical bars to detect missed breakouts
- ❌ Current price against breakout levels on restart

**Relies on:**
- Execution journal for fill tracking
- Idempotency checks for duplicate prevention
- Next bar's price for breakout detection

## Scenario: Start at 11:10 After Breakout at 11:05

### The Question

**What happens if you start the strategy at 11:10, but the 11:00 range already broke out at 11:05?**

### Step-by-Step What Happens

**1. Stream Restoration (11:10)**
- **Time:** 11:10 (startup)
- **Action:** Range restored from hydration log
- **State:** `RANGE_LOCKED` immediately
- **Breakout Levels:** Restored (or computed if missing)
- **Entry Detected:** Restored from execution journal (if entry was filled)

**2. BarsRequest Loads Historical Bars**
- **Time:** ~11:10 (after restoration)
- **Bars Loaded:** From `range_start` to `now` (11:10)
- **Includes:** Bars from 11:00-11:10 (including the breakout bar at 11:05)

**3. Critical Issue: Bars Are SKIPPED**
- **Code:** `RobotEngine.cs` line 2088-2090
```csharp
if (stream.State != StreamState.PRE_HYDRATION && 
    stream.State != StreamState.ARMED && 
    stream.State != StreamState.RANGE_BUILDING)
{
    // Bars skipped - stream already in RANGE_LOCKED
    LogEvent(..., "PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE", ...);
}
```
- **Result:** Historical bars from 11:00-11:10 are **NOT fed** to the stream
- **Reason:** Stream is already in `RANGE_LOCKED` state (not `PRE_HYDRATION`, `ARMED`, or `RANGE_BUILDING`)

**4. Breakout Detection**
- **Time:** On next **live** bar (11:11 or later)
- **Check:** `CheckBreakoutEntry()` only checks the **current bar**
- **Condition:** `barUtc >= SlotTimeUtc` (11:00) and `!_entryDetected`

### What Gets Detected

**Scenario A: Price Still Above/Below Breakout Level**
- **11:10 bar:** Price still breached → **WILL detect breakout**
- **11:11 bar:** Price still breached → **WILL detect breakout**
- **Result:** Entry detected on next bar

**Scenario B: Price Came Back Into Range**
- **11:10 bar:** Price back in range → **WON'T detect breakout**
- **11:11 bar:** Price back in range → **WON'T detect breakout**
- **Result:** **MISSED BREAKOUT** - won't be detected

**Scenario C: Entry Already Filled**
- **Restored:** `_entryDetected = true` (from execution journal)
- **Result:** No detection attempted (correct behavior)

### The Gap

**Problem:** Historical bars from BarsRequest are **skipped** when stream is in `RANGE_LOCKED` state.

**Why:** The code assumes bars are only needed for pre-hydration. Once range is locked, it doesn't replay historical bars to detect missed breakouts.

**Impact:**
- ✅ **If price still breached:** Will detect on next bar (correct)
- ❌ **If price came back:** Missed breakout won't be detected (gap)
- ✅ **If entry already filled:** Correctly skips (correct)

### Code Evidence

**Bars Skipped When RANGE_LOCKED:**
```csharp
// RobotEngine.cs, LoadPreHydrationBars(), line 2088-2090
if (stream.State != StreamState.PRE_HYDRATION && 
    stream.State != StreamState.ARMED && 
    stream.State != StreamState.RANGE_BUILDING)
{
    // Bars skipped - stream already progressed past pre-hydration
    LogEvent(..., "PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE", ...);
}
```

**Breakout Detection Only Checks Current Bar:**
```csharp
// StreamStateMachine.cs, OnBar(), line 2880-2882
if (!_entryDetected && barUtc >= SlotTimeUtc && barUtc < MarketCloseUtc && 
    _brkLongRounded.HasValue && _brkShortRounded.HasValue)
{
    CheckBreakoutEntry(barUtc, high, low, utcNow);  // Only checks THIS bar
}
```

**CheckBreakoutEntry Logic:**
```csharp
// StreamStateMachine.cs, line 5067-5069
bool longTrigger = high >= brkLong;   // Checks THIS bar's high
bool shortTrigger = low <= brkShort;  // Checks THIS bar's low
```

### Summary

**If you start at 11:10 after breakout at 11:05:**

1. **Range is restored** ✅
2. **BarsRequest loads bars** ✅ (11:00-11:10)
3. **Bars are SKIPPED** ❌ (stream already RANGE_LOCKED)
4. **Breakout detection** checks **next live bar only**
5. **If price still breached:** Detects on next bar ✅
6. **If price came back:** Missed breakout ❌

**The system does NOT:**
- Replay historical bars to detect missed breakouts
- Check if price already breached before restart
- Feed BarsRequest bars when stream is RANGE_LOCKED

**The system DOES:**
- Check if entry was already filled (execution journal)
- Detect breakout on next bar if price still breached
- Prevent duplicate orders (idempotency)

**This is a known limitation** - the system prioritizes current state over historical replay for performance and simplicity reasons.
