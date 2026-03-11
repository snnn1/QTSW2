# Stream State Snapshot & Restoration Analysis

## Executive Summary

**Does the system support snapshot/restore?**
- **Snapshot as record only**: ✅ YES (extensive logging)
- **Snapshot as fork (replay/testing)**: ❌ NO
- **Snapshot as resume (restore into live execution)**: ❌ NO

The system logs extensive state snapshots but **never restores them**. All restarts result in **full reconstruction** from scratch, not restoration from prior state.

---

## 1. State Capture & Persistence

### 1.1 StreamJournal (Minimal Persistence)

**Location**: `modules/robot/core/JournalStore.cs` (lines 102-117)

**Persisted Fields**:
```csharp
public sealed class StreamJournal
{
    public string TradingDate { get; set; } = "";
    public string Stream { get; set; } = "";
    public bool Committed { get; set; }
    public string? CommitReason { get; set; }
    public string LastState { get; set; } = "";
    public string LastUpdateUtc { get; set; } = "";
    public string? TimetableHashAtCommit { get; set; }
}
```

**What is NOT persisted**:
- Range values (RangeHigh, RangeLow)
- Bar buffer contents
- Entry detection state (_entryDetected)
- Pre-hydration completion (_preHydrationComplete)
- Range computation state (_rangeComputed)
- Breakout levels (_brkLongRounded, _brkShortRounded)
- Any derived state

**Storage**: `logs/robot/journal/{TradingDate}_{Stream}.json`

**When saved**:
- On every state transition (`Transition()` method, line 5172)
- On commit (`Commit()` method, line 4096)
- On trading day rollover (line 526)

---

### 1.2 Event-Based Snapshots (Logs Only)

**HYDRATION_SUMMARY** (`StreamStateMachine.cs`, lines 1407-1445, 1529-1567, 1764-1802)
- Captures: bar counts, range values, completeness metrics, late-start flags, missed breakout details
- **Not restorable**: Logged to JSONL files, never read back

**RANGE_COMPUTE_COMPLETE** (`StreamStateMachine.cs`, line 2326)
- Captures: RangeHigh, RangeLow, bar counts, computation timing
- **Not restorable**: Diagnostic log only

**RANGE_LOCK_SNAPSHOT** (`StreamStateMachine.cs`, line 2448)
- Captures: RangeHigh, RangeLow, range size, slot time
- **Not restorable**: Logged but never restored

**RANGE_LOCKED_INCREMENTAL** (`StreamStateMachine.cs`, line 2398)
- Captures: Updated range values during incremental computation
- **Not restorable**: Diagnostic log only

**STREAM_STATE_TRANSITION** (`StreamStateMachine.cs`, line 5177)
- Captures: Previous state, new state, transition time, time in previous state
- **Not restorable**: Observability event only

---

## 2. State Restoration (What Actually Happens)

### 2.1 Constructor Behavior (`StreamStateMachine.cs`, lines 280-346)

**On stream initialization**:
```csharp
var existing = journals.TryLoad(tradingDateStr, Stream);
var isRestart = existing != null;
```

**If journal exists (restart)**:
- Journal is loaded (`_journal = existing`)
- **BUT**: All runtime state is initialized to defaults:
  - `State = StreamState.PRE_HYDRATION` (line 61, always starts here)
  - `_preHydrationComplete = false` (line 136)
  - `_rangeComputed = false` (line 112)
  - `_entryDetected = false` (line 183)
  - `RangeHigh = null`, `RangeLow = null` (lines 67-68)
  - Bar buffer is empty (not restored)

**Mid-session restart detection** (lines 286-330):
- Checks if `!existing.Committed && nowChicago >= RangeStartChicagoTime`
- Logs `MID_SESSION_RESTART_DETECTED` event
- **Policy**: "Restart = Full Reconstruction"
- **Result**: Stream re-enters PRE_HYDRATION and reconstructs range from BarsRequest

**If journal is committed** (lines 476-500):
- Stream is marked `DONE` immediately
- No state restoration attempted
- Stream is permanently terminated for that trading day

---

### 2.2 What Gets Restored?

**Answer: NOTHING beyond the journal itself**

The journal is loaded but **only used for**:
1. Checking `Committed` flag (prevents re-arming, line 2506)
2. Checking `LastState` (logged in MID_SESSION_RESTART_DETECTED, line 319)
3. Preserving commit reason and timetable hash (for audit)

**No runtime state is restored**:
- ❌ Range values are NOT restored
- ❌ Bar buffer is NOT restored
- ❌ Entry detection state is NOT restored
- ❌ Pre-hydration completion is NOT restored
- ❌ Breakout levels are NOT restored

---

## 3. Lifecycle Boundaries & Invariants

### 3.1 "One Hydration Per Stream Per Trading Day" Enforcement

**Location**: `StreamStateMachine.cs`, `HandlePreHydrationState()` method

**Enforcement mechanism**: `_preHydrationComplete` flag (line 136)

**How it works**:
- `_preHydrationComplete` is set to `true` after hydration completes (lines 937, 3520, 3691)
- Once `true`, `HandlePreHydrationState()` skips hydration logic (line 964)
- Stream transitions to ARMED state (line 987)

**Reset conditions**:
- ✅ On `Arm()` call (line 2519) - resets for new slot
- ✅ On trading day rollover (line 4677) - resets for new day
- ✅ On `ResetDailyState()` (line 4677)

**Prevention of re-hydration**:
- Once `_preHydrationComplete = true`, hydration logic is skipped
- **BUT**: On restart, flag is reset to `false`, so re-hydration occurs

**Conclusion**: The invariant is **per-slot**, not **per-trading-day**. Restarts allow re-hydration.

---

### 3.2 State Irreversibility Guards

**Committed streams** (`StreamStateMachine.cs`):
- Line 728: `Arm()` returns early if `_journal.Committed`
- Line 2506: `Arm()` sets state to DONE if committed
- Line 2542: `EnterRecoveryManage()` commits if already committed
- Line 2570: `OnBar()` returns early if committed
- Line 3139: `CheckBreakoutEntry()` returns early if committed
- Line 4084: `Commit()` returns early if already committed

**Range invalidation** (`StreamStateMachine.cs`):
- Line 3140: `CheckBreakoutEntry()` returns early if `_rangeInvalidated`
- Once invalidated, range cannot be recomputed

**Entry detection** (`StreamStateMachine.cs`):
- Line 3008: `canDetectEntries` requires `!_entryDetected`
- Once entry detected, no further entries allowed
- Line 4242: Breakout detection skipped if `_entryDetected`

---

### 3.3 PRE_HYDRATION Re-entry Conditions

**PRE_HYDRATION can be re-entered**:
1. **On `Arm()` call** (line 2537): Always transitions to PRE_HYDRATION
2. **On trading day rollover** (line 570): Resets to PRE_HYDRATION if not committed
3. **On restart** (line 61): Always starts in PRE_HYDRATION

**What happens on re-entry**:
- `_preHydrationComplete` is reset to `false` (lines 2519, 4677)
- Bar buffer is cleared (on rollover, line 542)
- Range values are reset (on rollover, lines 4664-4665)
- All state flags are reset

**Conclusion**: PRE_HYDRATION is **not a terminal state** - it can be re-entered multiple times per day (once per slot, or on restart).

---

## 4. Restart Scenarios

### 4.1 Strategy Restart

**What happens** (`StreamStateMachine.cs`, constructor, lines 280-346):
1. Journal is loaded via `journals.TryLoad()`
2. If journal exists and not committed:
   - Stream starts in `PRE_HYDRATION` (always)
   - All runtime state is reset to defaults
   - Mid-session restart is detected if `nowChicago >= RangeStartChicagoTime`
   - `MID_SESSION_RESTART_DETECTED` event is logged
3. BarsRequest is issued to reconstruct range from `range_start` to `min(slot_time, now)`
4. Range is recomputed from scratch
5. Stream progresses through states normally

**State restoration**: ❌ NONE - full reconstruction

---

### 4.2 Engine Restart

**What happens** (`RobotEngine.cs`):
- Engine creates new `StreamStateMachine` instances
- Each stream's constructor runs (see Strategy Restart above)
- No cross-stream state is preserved

**State restoration**: ❌ NONE - all streams restart from scratch

---

### 4.3 Reconnect (Connection Loss)

**What happens** (`StreamStateMachine.cs`, `EnterRecoveryManage()`, line 2540):
- Stream is committed immediately: `Commit(utcNow, "STREAM_STAND_DOWN", "STREAM_STAND_DOWN")`
- Stream transitions to DONE
- **No restoration**: Stream is terminated for the day

**State restoration**: ❌ NONE - stream is committed and terminated

---

### 4.4 Late Start

**What happens** (`StreamStateMachine.cs`, `HandlePreHydrationState()`, lines 796-868):
- `CheckMissedBreakout()` scans bars from `slot_time` to `now`
- If breakout detected: stream is committed with `NO_TRADE_LATE_START_MISSED_BREAKOUT`
- If no breakout: stream proceeds normally
- Range is still computed from `[range_start, slot_time)`

**State restoration**: ❌ NONE - range is computed fresh from bars

---

## 5. Guardrails & State Checks

### 5.1 Flags That Prevent State Progression

**`_preHydrationComplete`** (line 136):
- **Purpose**: Prevents transition to ARMED/RANGE_BUILDING without hydration
- **Checked**: Line 930, 1829
- **Reset**: Lines 2519, 4677

**`_rangeComputed`** (line 112):
- **Purpose**: Prevents duplicate range computations
- **Checked**: Lines 1925, 1973, 2075, 2122, 2411
- **Reset**: Lines 1938, 1994, 2262, 4676

**`_entryDetected`** (line 183):
- **Purpose**: Prevents multiple entries per stream per day
- **Checked**: Lines 2016, 2473, 2497, 2980, 3008, 4242
- **Reset**: Lines 4671 (on daily reset), 4486 (on market close)

**`_journal.Committed`** (line 62):
- **Purpose**: Prevents any further state changes once committed
- **Checked**: Lines 728, 2506, 2542, 2570, 3139, 4084
- **Reset**: ❌ NEVER - committed streams are terminal

**`_rangeInvalidated`** (line 142):
- **Purpose**: Prevents trading if range has gap violations
- **Checked**: Line 3140
- **Reset**: Lines 2524, 4701 (on Arm() and daily reset)

---

### 5.2 Early Returns That Block Execution

**In `Arm()`** (line 2504):
- Returns if `_journal.Committed` (line 2506)

**In `OnBar()`** (line 2551):
- Returns if `_journal.Committed` (line 2570)

**In `CheckBreakoutEntry()`** (line 3139):
- Returns if `_journal.Committed` (line 3139)
- Returns if `_rangeInvalidated` (line 3140)
- Returns if execution adapter/journal/risk gate missing (line 3141)
- Returns if breakout levels not computed (line 3142)
- Returns if range not computed (line 3143)

**In `HandlePreHydrationState()`** (line 930):
- Skips hydration if `_preHydrationComplete` (line 964)
- Early returns for various error conditions (lines 3510, 3520, 3530, 3566, 3590)

**In `HandleArmedState()`** (line 1826):
- Returns if `!_preHydrationComplete` (line 1829)

**In `HandleRangeBuildingState()`** (line 2012):
- Returns if no bars available (line 2025)
- Commits if market closed (line 2016)

**In `HandleRangeLockedState()`** (line 2494):
- Commits if market closed (line 2497)

---

## 6. Explicit Answers

### 6.1 Does the system support snapshot as record only?

**✅ YES**

**Evidence**:
- `HYDRATION_SUMMARY` events logged with full state (lines 1407-1445)
- `RANGE_COMPUTE_COMPLETE` events logged (line 2326)
- `RANGE_LOCK_SNAPSHOT` events logged (line 2448)
- `STREAM_STATE_TRANSITION` events logged (line 5177)
- All events written to JSONL files in `logs/robot/`

**Limitation**: Logs are never read back for restoration

---

### 6.2 Does the system support snapshot as fork (replay/testing)?

**❌ NO**

**Evidence**:
- No mechanism to load historical state from logs
- No replay mode that uses logged snapshots
- No testing framework that restores from snapshots
- All state must be reconstructed from bars

**What exists instead**:
- DRYRUN mode uses CSV files for pre-hydration (line 959)
- But CSV files are not snapshots - they're raw bar data

---

### 6.3 Does the system support snapshot as resume (restore into live execution)?

**❌ NO**

**Evidence**:
- Constructor always starts in `PRE_HYDRATION` (line 61)
- Journal is loaded but only `Committed` flag is used (line 282)
- All runtime state is reset to defaults on restart
- No code path restores range values, bar buffer, or entry state
- Mid-session restart policy is "Full Reconstruction" (line 300)

**What happens on resume**:
- Stream re-enters PRE_HYDRATION
- BarsRequest loads historical bars
- Range is recomputed from scratch
- Result may differ from uninterrupted operation (line 304)

---

## 7. Classes & Methods Responsible

### 7.1 State Persistence

**`JournalStore.Save()`** (`JournalStore.cs`, line 45):
- Saves StreamJournal to disk
- Called on every state transition and commit

**`StreamStateMachine.Transition()`** (`StreamStateMachine.cs`, line 5172):
- Updates `_journal.LastState` and `_journal.LastUpdateUtc`
- Calls `_journals.Save(_journal)`

**`StreamStateMachine.Commit()`** (`StreamStateMachine.cs`, line 4096):
- Sets `_journal.Committed = true`
- Saves journal to disk

---

### 7.2 State Loading

**`JournalStore.TryLoad()`** (`JournalStore.cs`, line 23):
- Loads StreamJournal from disk
- Returns `null` if file doesn't exist
- Returns `null` on IOException (fail-open)

**`StreamStateMachine` constructor** (`StreamStateMachine.cs`, line 282):
- Calls `journals.TryLoad(tradingDateStr, Stream)`
- Uses journal only to check `Committed` flag
- Does NOT restore any runtime state

---

### 7.3 State Reset

**`StreamStateMachine.ResetDailyState()`** (`StreamStateMachine.cs`, line 4661):
- Resets all daily state flags
- Clears bar buffer
- Resets range values
- Called on trading day rollover

**`StreamStateMachine.Arm()`** (`StreamStateMachine.cs`, line 2504):
- Resets pre-hydration flags
- Transitions to PRE_HYDRATION
- Called when stream is re-armed for new slot

---

## 8. Summary of Invariants

### 8.1 Enforced Invariants

1. **"One hydration per slot"**: Enforced by `_preHydrationComplete` flag
2. **"Committed streams cannot be re-armed"**: Enforced by `_journal.Committed` checks
3. **"One entry per stream per day"**: Enforced by `_entryDetected` flag
4. **"Range computed once per slot"**: Enforced by `_rangeComputed` flag
5. **"Invalidated ranges cannot trade"**: Enforced by `_rangeInvalidated` checks

### 8.2 Violated Invariants (On Restart)

1. **"One hydration per trading day"**: ❌ Violated - restart allows re-hydration
2. **"State continuity"**: ❌ Violated - restart resets all state
3. **"Deterministic execution"**: ❌ Violated - restart may produce different results (line 304)

---

## 9. Conclusion

The system **logs extensive state snapshots** but **never restores them**. All restarts result in **full reconstruction** from BarsRequest/CSV, not restoration from prior state. This is an intentional design choice documented as "Restart = Full Reconstruction" (line 300).

**No snapshot/restore semantics exist** beyond the minimal `StreamJournal` which only tracks commit status, not runtime state.
