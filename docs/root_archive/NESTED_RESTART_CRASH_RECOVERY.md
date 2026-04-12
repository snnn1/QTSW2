# Nested Restart & Crash Recovery - What Happens When NinjaTrader Crashes Mid-Range

**Date**: 2026-01-30  
**Scenario**: Start robot mid-range → NinjaTrader crashes → Restart NinjaTrader

---

## Answer: Robot Recovers Gracefully (But Requests Bars Again)

**Short Answer**: The robot **recovers from crashes** using persisted journal state, but **requests historical bars again** because BarsRequest tracking is in-memory only.

---

## Complete Crash Recovery Flow

### Scenario: Start Mid-Range → Crash → Restart

**Timeline**:
```
02:00 CT - Range Start Time
05:00 CT - Robot Starts (mid-session restart detected)
         ↓
         BarsRequest: Load bars from 02:00 to 05:00
         ↓
         Bars loaded, range building begins
         ↓
06:00 CT - NinjaTrader CRASHES ← CRASH POINT
         ↓
         (In-memory state lost)
         ↓
06:05 CT - NinjaTrader Restarts ← RESTART POINT
         ↓
         Robot detects restart from journal
         ↓
         BarsRequest: Load bars from 02:00 to 06:05 (again)
         ↓
         Range reconstruction continues
```

---

## What State is Preserved vs Lost

### ✅ PRESERVED (Persisted to Disk)

**Journal State** (`logs/robot/journal/{trading_date}_{stream}.json`):
- ✅ `TradingDate` - Trading date
- ✅ `Stream` - Stream ID
- ✅ `Committed` - Whether stream is committed
- ✅ `LastState` - Last stream state (e.g., "RANGE_BUILDING", "RANGE_LOCKED")
- ✅ `LastUpdateUtc` - Last update timestamp
- ✅ `StopBracketsSubmittedAtLock` - Whether stop brackets were submitted
- ✅ `EntryDetected` - Whether entry was detected

**Hydration/Range Logs** (`logs/robot/hydration/`, `logs/robot/ranges/`):
- ✅ Range lock events (if range was locked)
- ✅ Hydration events
- ✅ Range high/low values (if locked)

**Execution Journal** (`logs/robot/execution/`):
- ✅ Entry fills
- ✅ Order submissions
- ✅ Execution history

### ❌ LOST (In-Memory Only)

**BarsRequest Tracking**:
- ❌ `_barsRequestPending` dictionary - Lost
- ❌ `_barsRequestCompleted` dictionary - Lost
- **Result**: Robot doesn't know BarsRequest was already completed

**Bar Buffer**:
- ❌ Stream's bar buffer - Lost (in-memory)
- **Result**: Historical bars need to be reloaded

**In-Memory State**:
- ❌ Current range high/low (if not locked yet)
- ❌ Bar count
- ❌ State machine flags (except persisted ones)

---

## Recovery Process on Restart

### Step 1: Journal Load & Restart Detection

**Location**: `StreamStateMachine.cs` constructor (lines 300-314)

```csharp
var existing = journals.TryLoad(tradingDateStr, Stream);
var isRestart = existing != null;
var isMidSessionRestart = false;

if (isRestart)
{
    // Check if this is a mid-session restart
    isMidSessionRestart = !existing.Committed && nowChicago >= RangeStartChicagoTime;
}
```

**What Happens**:
- ✅ Loads journal from disk
- ✅ Detects it's a restart (journal exists)
- ✅ Detects it's a mid-session restart (not committed, after range start)
- ✅ Logs `MID_SESSION_RESTART_DETECTED` event

### Step 2: State Restoration

**Location**: `StreamStateMachine.cs` constructor (lines 382-455)

```csharp
// RESTART RECOVERY: Restore flags from persisted state
if (isRestart && existing != null)
{
    // Restore order submission flag
    _stopBracketsSubmittedAtLock = existing.StopBracketsSubmittedAtLock;
    
    // Restore entry detection
    _entryDetected = existing.EntryDetected;
    
    // Restore range lock from hydration/ranges log
    RestoreRangeLockedFromHydrationLog(tradingDateStr, Stream);
}
```

**What Happens**:
- ✅ Restores `_stopBracketsSubmittedAtLock` flag
- ✅ Restores `_entryDetected` flag
- ✅ Attempts to restore range lock from hydration/ranges log
- ✅ If range was locked, tries to restore range high/low

### Step 3: BarsRequest Re-Request (Because Tracking Lost)

**Location**: `RobotSimStrategy.cs` (lines 428-486)

**Problem**: BarsRequest tracking is in-memory only (`_barsRequestPending`, `_barsRequestCompleted` dictionaries)

**What Happens**:
- ❌ Robot doesn't know BarsRequest was already completed
- ✅ Requests BarsRequest again (from `range_start` to `current_time`)
- ✅ Loads historical bars again
- ✅ Feeds bars to stream buffers

**Why This Happens**:
- BarsRequest tracking is **not persisted** to journal
- On crash, tracking dictionaries are lost
- Robot assumes BarsRequest needs to be requested again

### Step 4: Range Reconstruction

**What Happens**:
- ✅ Historical bars reloaded via BarsRequest
- ✅ Live bars continue arriving
- ✅ Range recomputed from all available bars
- ✅ If range was locked before crash, tries to restore it

**If Range Was Locked Before Crash**:
- ✅ Attempts to restore from hydration/ranges log
- ✅ If restore succeeds: Uses restored range high/low
- ✅ If restore fails + bars insufficient: **Suspends stream** (fails closed)

---

## Critical Recovery Scenarios

### Scenario A: Crash During Range Building (Before Lock)

**State Before Crash**:
- Stream state: `RANGE_BUILDING`
- Range not locked yet
- BarsRequest completed
- Bars in buffer

**After Restart**:
1. ✅ Journal shows `LastState = "RANGE_BUILDING"`
2. ✅ Detects mid-session restart
3. ✅ Requests BarsRequest again (tracking lost)
4. ✅ Reloads historical bars
5. ✅ Continues range building from reloaded bars
6. ✅ Locks range at `slot_time` (normal flow)

**Result**: ✅ **Full recovery** - Range building continues normally

---

### Scenario B: Crash After Range Lock (Range Already Locked)

**State Before Crash**:
- Stream state: `RANGE_LOCKED`
- Range high/low locked
- Stop brackets submitted
- BarsRequest completed

**After Restart**:
1. ✅ Journal shows `LastState = "RANGE_LOCKED"`
2. ✅ Detects mid-session restart
3. ✅ Requests BarsRequest again (tracking lost)
4. ✅ Reloads historical bars
5. ✅ **Attempts to restore range lock** from hydration/ranges log
6. ✅ If restore succeeds: Uses restored range, continues trading
7. ✅ If restore fails + bars insufficient: **Suspends stream**

**Result**: ✅ **Recovery with range restoration** - Trading continues if restore succeeds

---

### Scenario C: Crash After Entry Fill

**State Before Crash**:
- Stream state: `IN_POSITION` or `DONE`
- Entry filled
- Position open
- Protective orders active

**After Restart**:
1. ✅ Journal shows `LastState = "IN_POSITION"` or `"DONE"`
2. ✅ `EntryDetected = true` (restored from journal)
3. ✅ Execution journal shows entry fill
4. ✅ Robot reconciles with live account state
5. ✅ Recreates protective orders if needed
6. ✅ Continues position management

**Result**: ✅ **Position recovery** - Robot manages existing position

---

## Important Limitations

### 1. BarsRequest Always Re-Requested

**Why**: BarsRequest tracking (`_barsRequestPending`, `_barsRequestCompleted`) is in-memory only

**Impact**:
- ✅ Historical bars reloaded (redundant but safe)
- ✅ Slight delay on restart (BarsRequest takes time)
- ✅ No data loss (bars reloaded correctly)

**Trade-off**: Redundant BarsRequest is safer than trying to persist tracking state

### 2. Bar Buffer Lost

**Why**: Bar buffer is in-memory only

**Impact**:
- ✅ Bars reloaded via BarsRequest (redundant but safe)
- ✅ Range recomputed from reloaded bars
- ✅ Deterministic reconstruction (same bars = same range)

**Trade-off**: Reloading bars ensures consistency

### 3. Range Lock Restoration May Fail

**Why**: Range lock restoration depends on hydration/ranges log files

**Impact**:
- ✅ If log exists: Range restored successfully
- ✅ If log missing + bars insufficient: Stream suspended (fails closed)
- ✅ If log missing + bars sufficient: Range recomputed

**Trade-off**: Fail-closed prevents trading with incorrect range

---

## Recovery Safety Features

### 1. Fail-Closed on Insufficient Data

**Location**: `StreamStateMachine.cs` (lines 408-434)

```csharp
if (existing.LastState == "RANGE_LOCKED" && !_rangeLocked)
{
    var barCount = GetBarBufferCount();
    if (!HasSufficientRangeBars(barCount, out var expectedBarCount, out var minimumRequired))
    {
        // SUSPEND STREAM - Do not recompute
        State = StreamState.SUSPENDED_DATA_INSUFFICIENT;
        return; // Exit constructor early
    }
}
```

**What This Does**:
- ✅ If range was locked but restore failed
- ✅ AND bars are insufficient
- ✅ **Suspends stream** (prevents trading with incorrect range)

**Result**: ✅ **Fails closed** - Safer than trading with wrong range

### 2. Execution Journal Reconciliation

**Location**: `StreamStateMachine.cs` (lines 388-398)

```csharp
// Restore entry detection from execution journal
if (_executionJournal != null && !existing.EntryDetected)
{
    _entryDetected = _executionJournal.HasEntryFillForStream(tradingDateStr, Stream);
}
```

**What This Does**:
- ✅ Scans execution journal for entry fills
- ✅ Restores `_entryDetected` flag
- ✅ Prevents duplicate entries

**Result**: ✅ **Duplicate prevention** - Won't enter twice

### 3. Order State Restoration

**Location**: `StreamStateMachine.cs` (lines 2196-2228)

```csharp
// RESTART RECOVERY: Retry stop bracket placement if it failed previously
if (isRestart && !_stopBracketsSubmittedAtLock && State == StreamState.RANGE_LOCKED)
{
    // Retry stop bracket placement
}
```

**What This Does**:
- ✅ Retries stop bracket placement on restart
- ✅ Ensures protective orders are active
- ✅ Prevents unprotected positions

**Result**: ✅ **Position protection** - Ensures protective orders exist

---

## Summary

### What Happens on Nested Restart:

1. ✅ **Journal Loaded** - Detects restart from persisted journal
2. ✅ **State Restored** - Restores flags (stop brackets, entry detected)
3. ✅ **BarsRequest Re-Requested** - Tracking lost, so requests again
4. ✅ **Bars Reloaded** - Historical bars reloaded via BarsRequest
5. ✅ **Range Restored** - Attempts to restore range lock from logs
6. ✅ **Trading Continues** - If recovery succeeds, trading continues

### Key Points:

- ✅ **Recovery is automatic** - No manual intervention needed
- ✅ **BarsRequest is redundant** - But safe (reloads bars correctly)
- ✅ **Range restoration** - Attempts to restore locked ranges
- ✅ **Fail-closed protection** - Suspends if data insufficient
- ✅ **Duplicate prevention** - Won't enter positions twice

### Trade-offs:

- ⚠️ **BarsRequest redundancy** - Always re-requested (tracking not persisted)
- ⚠️ **Bar buffer reload** - Bars reloaded (buffer not persisted)
- ⚠️ **Range recomputation** - May differ if restoration fails

**Result**: ✅ **Robust crash recovery** - Robot recovers gracefully from crashes, with redundant BarsRequest as a safety measure.

---

**Status**: ✅ **COMPLETE EXPLANATION**
