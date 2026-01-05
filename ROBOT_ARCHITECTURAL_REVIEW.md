# Robot Execution Engine Architectural Review

**Date:** 2026-01-02  
**Reviewer:** Architectural Analysis  
**Scope:** Robot execution code correctness, safety, and fail-closed behavior

---

## Executive Summary

This review analyzes the Robot execution engine against the stated authoritative rules. The Robot demonstrates **strong fail-closed behavior** and **correct authority boundaries** in most areas. However, several **critical risks** and **moderate concerns** were identified that could lead to silent failures or incorrect execution.

### Key Findings

- **Confirmed Correctness:** Timetable consumption, enabled flag handling, one-trade-per-day enforcement, timezone validation
- **Critical Issues:** Silent failure in kill switch error handling (fail-open), missing DecisionTime field usage validation, potential race condition in timetable updates
- **Moderate Risks:** Partial execution allowed in some error paths, incomplete error propagation in execution adapters
- **Acceptable Design:** Stream state persistence across timetable updates, replay mode detection via file read

---

## 1. Purpose Alignment

### What the Robot Currently Does

The Robot acts as a **deterministic interpreter** of `timetable_current.json`. It:

1. **Reads timetable file** - Polls and loads `timetable_current.json` periodically
2. **Validates timetable** - Checks trading_date, timezone, and stream fields
3. **Manages stream state machines** - Creates/updates StreamStateMachine instances per enabled stream
4. **Executes trading logic** - Builds ranges, locks ranges, detects breakouts, places orders
5. **Enforces one trade per stream per day** - Uses journal commitment to prevent re-execution
6. **Tracks execution** - Maintains execution journals for idempotency

### What Decisions the Robot Makes

**Correct Decisions (Robot's Authority):**
- ✅ When to transition stream states (IDLE → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE)
- ✅ When to lock range (at `slot_time`)
- ✅ When to detect breakout entries (based on bar data vs breakout levels)
- ✅ When to place protective orders (after entry fill)
- ✅ When to modify stop to break-even (at 65% of target)
- ✅ When to commit stream (at market close or after trade)

**Decisions Robot Does NOT Make (Correctly):**
- ✅ **Slot selection** - Uses `slot_time` from timetable, never chooses
- ✅ **Tradability** - Only trades streams with `enabled=true`, never infers
- ✅ **Trading date** - Validates against Chicago today, never assumes
- ✅ **Instrument configuration** - Reads from ParitySpec, never hardcodes
- ✅ **Risk limits** - Uses RiskGate checks, never bypasses

### Inference vs Explicit Reading

**✅ Correctly Reads Explicitly:**
- `enabled` flag - Only processes streams where `enabled=true` (line 288: `timetable.Streams.Where(s => s.Enabled)`)
- `slot_time` - Uses `directive.SlotTime` directly, never infers (line 301, 376)
- `trading_date` - Validates against Chicago today, never assumes (lines 246-271)
- `timezone` - Requires exact match "America/Chicago" (line 238)

**⚠️ Potential Inference Risk:**
- **DecisionTime field ignored** - The timetable model includes `DecisionTime` field (line 51 in `Models.TimetableContract.cs`), but Robot never reads or validates it. This is **correct behavior** (Robot should ignore it), but there's no explicit validation that ensures DecisionTime is not accidentally used instead of SlotTime.

### Defaults vs Explicit Instructions

**✅ No Hard-Coded Defaults Found:**
- All stream fields validated for non-empty (lines 313-316)
- No default slot_time assumed
- No default enabled state assumed
- No default instrument configuration

**✅ Correct Use of Spec Defaults:**
- Instrument tick_size and base_target from ParitySpec (lines 109-110) - This is correct as spec is configuration, not decision data
- Session range_start_time from spec (line 102) - Correct, spec defines session structure

---

## 2. Authority & Ownership

### Data Ownership

**Robot Owns (Correctly):**
- ✅ Stream journals (`StreamJournal`) - Per (trading_date, stream) persistence
- ✅ Execution journals (`ExecutionJournal`) - Per (trading_date, stream, intent_id) tracking
- ✅ Stream state machines - Runtime state (IDLE, ARMED, RANGE_BUILDING, etc.)
- ✅ Range tracking - RangeHigh, RangeLow, FreezeClose (computed from bar data)
- ✅ Breakout levels - brkLongRounded, brkShortRounded (computed from range)
- ✅ Entry detection flags - _entryDetected, intendedDirection, etc.

**Robot Reads (Read-Only, Correctly):**
- ✅ Timetable file - Never modifies, only reads
- ✅ ParitySpec - Configuration, never modifies
- ✅ Kill switch file - Reads only, never writes

### Override/Reinterpretation Analysis

**✅ No Overrides Found:**
- Robot never modifies timetable fields
- Robot never reinterprets timetable semantics
- Robot never overrides timetable decisions

**✅ Correct Reinterpretation:**
- Converts `slot_time` (HH:MM string) to UTC for internal use - This is **correct** as it's a format conversion, not semantic change
- Uses `enabled=false` streams for logging only - Correct, Robot ignores disabled streams

### Historical Data Usage

**✅ No Historical Decision-Making:**
- Robot does not use historical data to decide live execution
- Journal loading (line 112) is for **idempotency** (preventing double-execution), not decision-making
- Previous day's committed state prevents re-arming - This is **correct** (one trade per day enforcement)

**✅ Correct Historical Usage:**
- Loads existing journal to check if stream already committed - Correct idempotency check
- Uses journal's `Committed` flag to prevent re-execution - Correct safety mechanism

### Hard-Coded Defaults

**✅ No Hard-Coded Execution Defaults:**
- All execution parameters come from timetable or spec
- No fallback to defaults when timetable data missing

**⚠️ Configuration Defaults (Acceptable):**
- Kill switch defaults to "disabled" if file missing (line 42 in `KillSwitch.cs`) - This is **fail-open** behavior, see Error Handling section
- Execution journal defaults to "not submitted" if file corrupted (line 79 in `ExecutionJournal.cs`) - This is **fail-open** behavior

---

## 3. Timetable Consumption Safety

### Missing Field Handling

**✅ Correct Fail-Closed Behavior:**

1. **Missing stream fields** (lines 313-316):
   - Checks: `streamId`, `instrument`, `session`, `slotTimeChicago` all non-empty
   - Action: Skips stream, logs `STREAM_SKIPPED` with reason `MISSING_FIELDS`
   - ✅ **Correct:** Fail-closed per stream

2. **Missing slot_time in update** (lines 371-375):
   - Checks: `directive.SlotTime` non-empty before updating existing stream
   - Action: Skips update, continues (no log for this case)
   - ⚠️ **Issue:** Silent skip - should log this case

3. **Missing trading_date** (lines 230-236):
   - Checks: `TimeService.TryParseDateOnly(timetable.TradingDate)`
   - Action: Logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Fail-closed at engine level

4. **Missing timezone** (lines 238-244):
   - Checks: `timetable.Timezone != "America/Chicago"`
   - Action: Logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Fail-closed at engine level

**⚠️ Missing Enabled Flag Handling:**
- Robot uses `timetable.Streams.Where(s => s.Enabled)` (line 288)
- If `Enabled` property is missing from JSON, C# deserializer will default to `false` (bool default)
- ✅ **Correct:** Missing enabled defaults to false (disabled), Robot correctly ignores
- However, there's no explicit validation that `Enabled` field exists - relies on deserializer default

**⚠️ Missing DecisionTime Field:**
- `DecisionTime` field exists in model (line 51) but Robot never reads it
- ✅ **Correct:** Robot should ignore DecisionTime (it's informational)
- ⚠️ **Risk:** No validation that DecisionTime is not accidentally used - if code changed to use DecisionTime instead of SlotTime, no validation would catch it

### Partial Stream Lists

**✅ Correct Handling:**
- Robot processes only streams present in timetable (line 288: `timetable.Streams.Where(s => s.Enabled)`)
- Streams not in timetable are **left as-is** (line 405-406 comment)
- ✅ **Correct:** Timetable is authoritative about enabled streams
- Existing streams not in timetable continue their state machines (no new orders, but state preserved)

**⚠️ Potential Issue:**
- If timetable is updated mid-day and removes a stream, that stream's state machine remains active
- Robot comment says "skeleton remains fail-closed (no orders)" - but state machine still ticks
- This is **acceptable** - stream will commit at market close or when it reaches DONE state

### Duplicate Streams

**✅ Correct Handling:**
- Robot uses `Dictionary<string, StreamStateMachine>` keyed by stream ID (line 26)
- `seen` HashSet tracks duplicates but is only used for logging (line 289, 358)
- If same stream ID appears twice in timetable:
  - First occurrence creates/updates state machine
  - Second occurrence would update same state machine (line 361-376)
  - ✅ **Correct:** Last update wins, no duplicate state machines

**⚠️ Silent Duplicate Handling:**
- No explicit log when duplicate stream ID detected
- Robot silently processes last occurrence
- **Recommendation:** Log duplicate stream IDs for observability

### Enabled Flag Handling

**✅ Correct Behavior:**
- Robot filters to `enabled=true` streams only (line 288)
- `enabled=false` streams are completely ignored
- ✅ **Correct:** Robot never infers tradability from absence

**✅ BlockReason Field:**
- `BlockReason` field exists in model (line 45) but Robot never reads it
- ✅ **Correct:** Robot should ignore BlockReason (it's informational for upstream systems)

### Fail-Closed Validation

**✅ Strong Fail-Closed Behavior:**

1. **Timetable parse failure** (lines 222-228):
   - Exception caught, logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Engine clears all streams, stops execution

2. **Trading date mismatch** (lines 264-270):
   - Live mode: Requires exact match with Chicago today
   - Replay mode: Allows `trading_date <= chicagoToday`
   - ✅ **Correct:** Fail-closed for live, permissive for replay

3. **Invalid slot_time** (lines 337-346):
   - Validates against `spec.Sessions[session].SlotEndTimes`
   - Skips stream, logs `STREAM_SKIPPED` with reason `INVALID_SLOT_TIME`
   - ✅ **Correct:** Fail-closed per stream

4. **Unknown session** (lines 326-334):
   - Skips stream, logs `STREAM_SKIPPED` with reason `UNKNOWN_SESSION`
   - ✅ **Correct:** Fail-closed per stream

5. **Unknown instrument** (lines 348-356):
   - Skips stream, logs `STREAM_SKIPPED` with reason `UNKNOWN_INSTRUMENT`
   - ✅ **Correct:** Fail-closed per stream

---

## 4. Stream State Machine Integrity

### One Trade Per Stream Per Day Enforcement

**✅ Strong Enforcement Mechanisms:**

1. **Journal Commitment Check** (lines 238-243 in `StreamStateMachine.cs`):
   ```csharp
   if (_journal.Committed)
   {
       State = StreamState.DONE;
       return;
   }
   ```
   - ✅ **Correct:** Committed streams cannot re-arm or execute

2. **Commit on Entry** (lines 434-457):
   - When stream commits (entry filled or market close), sets `_journal.Committed = true`
   - Saves journal to disk
   - Sets state to DONE
   - ✅ **Correct:** Prevents re-execution

3. **Journal Loading on Init** (lines 112-122):
   - Loads existing journal for (trading_date, stream)
   - If journal exists and committed, stream starts in DONE state
   - ✅ **Correct:** Survives Robot restarts

4. **Update Ignored if Committed** (lines 363-368 in `RobotEngine.cs`):
   - If timetable update arrives for committed stream, update is ignored
   - ✅ **Correct:** Committed streams cannot be modified

**✅ Correct Behavior:**
- One trade per stream per day is **strongly enforced**
- Multiple mechanisms prevent double-execution
- State persists across Robot restarts

### Double-Execution Paths

**✅ No Double-Execution Paths Found:**

1. **Entry Detection Guard** (line 526):
   ```csharp
   if (_entryDetected) return; // Already detected
   ```
   - ✅ **Correct:** `_entryDetected` flag prevents multiple entry detections

2. **Intent Idempotency** (lines 585-597):
   - Checks `_executionJournal.IsIntentSubmitted(intentId, TradingDate, Stream)`
   - Skips if intent already submitted
   - ✅ **Correct:** Prevents duplicate order submissions

3. **State Machine State** (lines 238-243):
   - Committed streams cannot transition states
   - ✅ **Correct:** Terminal state prevents re-execution

**⚠️ Potential Race Condition:**
- Between checking `_entryDetected` (line 526) and setting it (line 528), if `RecordIntendedEntry` is called concurrently, could theoretically detect twice
- However, Robot is single-threaded (Tick/OnBar called sequentially), so this is **not a real risk**

### State Transitions

**✅ Clean State Machine:**

States: `IDLE → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE`

**Transitions:**
1. **IDLE → ARMED** (line 342): Via `Arm()` method, only if state is IDLE
2. **ARMED → RANGE_BUILDING** (line 254): When `utcNow >= RangeStartUtc`
3. **RANGE_BUILDING → RANGE_LOCKED** (line 265): When `utcNow >= SlotTimeUtc`
4. **RANGE_LOCKED → DONE** (line 311, 319): Via `Commit()` when market close or entry filled
5. **Any → DONE** (line 241): If `_journal.Committed` is true

**✅ Correct Properties:**
- Transitions are deterministic (time-based or event-based)
- No backward transitions (except DONE terminal state)
- State persisted to journal on each transition (line 755)

**⚠️ State Reset on Trading Day Rollover:**
- `UpdateTradingDate()` resets state to ARMED if not committed (lines 211-213)
- ✅ **Correct:** New trading day, new execution opportunity
- However, resets `_entryDetected` flag (line 200) - this is correct for new day

### Re-Entry Risks

**✅ No Re-Entry Risks Found:**

1. **Committed Check** (lines 238-243):
   - Every `Tick()` checks if committed, exits early if so
   - ✅ **Correct:** Prevents any state machine activity for committed streams

2. **Entry Detection Flag** (line 526):
   - `_entryDetected` prevents multiple entry detections
   - ✅ **Correct:** Once entry detected, no further detection

3. **Journal Persistence** (line 448):
   - Committed state persisted to disk
   - ✅ **Correct:** Survives Robot restarts

### Order/Execution Callback Ordering

**✅ Correct Sequencing:**

1. **Entry Order Submission** (lines 634-640):
   - Submits entry order via adapter
   - Records submission in execution journal
   - ✅ **Correct:** Journal updated before order submitted

2. **Protective Orders on Fill** (lines 252-304 in `NinjaTraderSimAdapter.cs`):
   - Protective orders submitted **only after entry fill confirmation**
   - ✅ **Correct:** Safety-first approach

3. **Intent Registration** (lines 661-666):
   - Registers intent with adapter for fill callback
   - ✅ **Correct:** Enables adapter to submit protective orders on fill

**⚠️ Potential Issue:**
- Intent registration uses reflection (line 664) - fragile, but works
- If adapter doesn't support `RegisterIntent`, intent not registered
- Protective orders might not be submitted automatically
- **Risk:** Manual intervention required if adapter doesn't support registration

---

## 5. Time & Session Semantics

### Slot Time Interpretation

**✅ Correct Interpretation:**

1. **Slot Time Source** (line 301 in `RobotEngine.cs`):
   - Reads `directive.SlotTime` from timetable (HH:MM string)
   - ✅ **Correct:** Uses explicit timetable field

2. **Conversion to UTC** (lines 104, 132 in `StreamStateMachine.cs`):
   - Converts Chicago local time to UTC using `TimeService.ConvertChicagoLocalToUtc()`
   - ✅ **Correct:** DST-aware conversion

3. **Slot Time Usage** (line 259):
   - Range locks when `utcNow >= SlotTimeUtc`
   - ✅ **Correct:** Uses converted UTC time for comparison

**✅ No Inference:**
- Robot never infers slot_time from other fields
- Robot never uses DecisionTime (correctly ignores it)

### Exchange Time vs Chicago Time

**✅ Correct Handling:**

1. **All Times in Chicago** (line 238):
   - Timetable must have `timezone == "America/Chicago"`
   - ✅ **Correct:** Enforces Chicago timezone

2. **Bar Timestamps** (line 118):
   - `OnBar()` receives `barUtc` (UTC timestamp)
   - Converts to Chicago for date calculation (line 123)
   - ✅ **Correct:** Uses UTC for precision, Chicago for date

3. **Time Service** (`TimeService.cs`):
   - All conversions use Chicago timezone
   - DST-aware conversions (line 38)
   - ✅ **Correct:** Handles DST transitions correctly

**✅ No Timezone Mismatches:**
- Robot consistently uses Chicago timezone
- No exchange time confusion

### Bar Timestamp Logic

**✅ Correct Bar Processing:**

1. **Bar Date Derivation** (lines 123-124):
   - Derives trading date from bar timestamp (Chicago date)
   - ✅ **Correct:** Bar-derived date is authoritative in replay mode

2. **Trading Date Rollover** (lines 127-151):
   - Detects when bar date differs from engine trading_date
   - Updates engine trading_date and all stream state machines
   - ✅ **Correct:** Handles day transitions correctly

3. **Replay Invariant Check** (lines 153-188):
   - In replay mode, validates engine trading_date matches bar-derived date
   - Stands down if mismatch detected
   - ✅ **Correct:** Prevents trading date bugs in replay

**⚠️ Live Mode Trading Date:**
- In live mode, trading_date comes from timetable (line 273)
- Bar-derived date not used for live mode trading_date validation
- ✅ **Correct:** Timetable is authoritative for live mode

### Previous Day State Leakage

**✅ Correct Isolation:**

1. **Journal Per Trading Date** (line 16 in `JournalStore.cs`):
   - Journal path: `{tradingDate}_{stream}.json`
   - ✅ **Correct:** Each trading date has separate journal

2. **Trading Date Rollover** (lines 137-234 in `StreamStateMachine.cs`):
   - `UpdateTradingDate()` loads journal for new trading_date
   - Resets range state, entry flags for new day
   - ✅ **Correct:** Clean slate for new trading day

3. **State Reset** (lines 194-208):
   - Resets RangeHigh, RangeLow, FreezeClose, entry flags
   - ✅ **Correct:** No state leakage between days

**✅ No Leakage Found:**
- Previous day state does not leak into today
- Each trading day starts fresh

---

## 6. Error Handling & Fail-Closed Guarantees

### Error Paths Analysis

**✅ Fail-Closed Error Paths:**

1. **Timetable Parse Error** (lines 222-228 in `RobotEngine.cs`):
   - Exception caught, logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Engine clears streams, stops execution

2. **Trading Date Invalid** (lines 230-236):
   - Logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Fail-closed

3. **Timezone Mismatch** (lines 238-244):
   - Logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Fail-closed

4. **Stale Trading Date** (lines 264-270):
   - Live mode: Logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Fail-closed for live mode

5. **File Poll Error** (lines 203-209):
   - Logs `TIMETABLE_INVALID`, calls `StandDown()`
   - ✅ **Correct:** Fail-closed

**⚠️ Fail-Open Error Paths:**

1. **Kill Switch File Error** (lines 73-85 in `KillSwitch.cs`):
   ```csharp
   catch (Exception ex)
   {
       // If kill switch file is corrupted, treat as disabled (fail open)
       _cachedState = new KillSwitchState { Enabled = false, Message = null };
       return false;
   }
   ```
   - ⚠️ **CRITICAL:** Kill switch errors default to "disabled" (fail-open)
   - **Risk:** If kill switch file is corrupted, Robot continues execution
   - **Should be:** Fail-closed (treat as enabled if file error)

2. **Execution Journal Corruption** (lines 77-80 in `ExecutionJournal.cs`):
   ```csharp
   catch
   {
       // If journal is corrupted, treat as not submitted (fail open)
   }
   ```
   - ⚠️ **MODERATE:** Journal corruption defaults to "not submitted"
   - **Risk:** Could allow duplicate submissions if journal corrupted
   - **Mitigation:** Intent ID check still prevents duplicates (line 585)

3. **BE Modification Journal Corruption** (lines 292-295):
   - Similar fail-open behavior
   - ⚠️ **MODERATE:** Could allow duplicate BE modifications

### Logging vs Silent Handling

**✅ Good Logging:**

1. **Timetable Errors** - All logged with `TIMETABLE_INVALID` event
2. **Stream Skipped** - Logged with reason (MISSING_FIELDS, UNKNOWN_SESSION, etc.)
3. **Execution Events** - Logged via `RobotLogger` and `ExecutionJournal`

**⚠️ Silent Failures:**

1. **Slot Time Update Skip** (lines 371-375):
   - If `directive.SlotTime` is empty when updating existing stream, silently skips
   - ⚠️ **MODERATE:** Should log this case for observability

2. **Adapter Registration Failure** (lines 661-666):
   - Uses reflection to register intent, silently fails if method not found
   - ⚠️ **MODERATE:** No error logged if registration fails

### Partial Execution Risks

**✅ Strong Prevention:**

1. **StandDown() Method** (lines 409-413):
   - Clears all streams, resets trading_date
   - ✅ **Correct:** Complete shutdown on error

2. **Per-Stream Fail-Closed** (lines 313-356):
   - Invalid streams skipped, others continue
   - ✅ **Correct:** One bad stream doesn't stop others

**⚠️ Partial Execution Allowed:**

1. **Execution Adapter Errors** (lines 201-214 in `NinjaTraderSimAdapter.cs`):
   - Entry order submission failure logs error but doesn't stop stream
   - Stream continues to DONE state at market close
   - ⚠️ **ACCEPTABLE:** This is correct - order failure doesn't mean stream should stop

2. **Protective Order Failures** (lines 270-289):
   - Stop/target order failures logged but don't stop execution
   - ⚠️ **ACCEPTABLE:** Position management continues even if protective orders fail

### Try-Best vs Stand-Down

**✅ Mostly Stand-Down:**

- Timetable errors → StandDown() ✅
- Trading date errors → StandDown() ✅
- Timezone errors → StandDown() ✅

**⚠️ Try-Best Behavior:**

- Execution adapter errors → Log and continue ⚠️
- Journal corruption → Treat as safe state and continue ⚠️
- Kill switch errors → Treat as disabled and continue ⚠️ **CRITICAL**

### Silent Failures

**⚠️ Silent Failure Modes:**

1. **Kill Switch File Error** (lines 73-85 in `KillSwitch.cs`):
   - Error logged but execution continues (fail-open)
   - ⚠️ **CRITICAL:** Should fail-closed (treat as enabled)

2. **Slot Time Update Skip** (lines 371-375):
   - Silent skip, no log
   - ⚠️ **MODERATE:** Should log for observability

3. **Intent Registration Failure** (lines 661-666):
   - Reflection failure silent, no error logged
   - ⚠️ **MODERATE:** Should log if registration fails

---

## 7. Replay / SIM Mode Isolation

### Replay Mode Detection

**✅ Explicit Detection:**

1. **Replay Flag** (lines 250, 164):
   - Checks `timetable.Metadata?.Replay == true`
   - ✅ **Correct:** Explicit flag in timetable metadata

2. **Replay Invariant Check** (lines 153-188):
   - Only validates trading_date mismatch in replay mode
   - ✅ **Correct:** Replay-specific validation

**⚠️ Detection Method:**

- Replay detection reads timetable file **every bar** (lines 159-166)
- File I/O on every bar could be expensive
- ✅ **ACCEPTABLE:** File read is cached by FilePoller, but still checked every bar

### Live Safeguard Bypass

**✅ No Bypasses Found:**

1. **Trading Date Validation** (lines 248-271):
   - Replay mode: Allows `trading_date <= chicagoToday`
   - Live mode: Requires exact match
   - ✅ **Correct:** Different rules for replay vs live

2. **Replay Invariant** (lines 172-187):
   - Only enforced in replay mode
   - ✅ **Correct:** Replay-specific check

**✅ Safeguards Preserved:**

- Kill switch still checked in replay mode ✅
- Risk gate still checked ✅
- Execution journal still used ✅

### Mode Leakage

**✅ No Leakage Found:**

1. **Execution Mode** (line 27 in `RobotEngine.cs`):
   - Set at construction, never changes
   - ✅ **Correct:** Mode is immutable

2. **Adapter Selection** (lines 14-49 in `ExecutionAdapterFactory.cs`):
   - Adapter created once at startup
   - ✅ **Correct:** No mode switching at runtime

3. **Replay Detection** (line 164):
   - Checks timetable metadata, doesn't affect execution mode
   - ✅ **Correct:** Replay is data property, not execution mode

**✅ Clean Separation:**

- DRYRUN mode → NullExecutionAdapter ✅
- SIM mode → NinjaTraderSimAdapter ✅
- LIVE mode → Not enabled (throws exception) ✅

### SIM vs LIVE Ambiguity

**✅ Clear Separation:**

1. **SIM Account Verification** (lines 69-121 in `NinjaTraderSimAdapter.cs`):
   - Verifies account is Sim account
   - Fails if not Sim
   - ✅ **Correct:** SIM adapter only works with Sim accounts

2. **LIVE Mode Not Enabled** (line 42 in `ExecutionAdapterFactory.cs`):
   - Throws exception if LIVE mode requested
   - ✅ **Correct:** Explicit prevention of LIVE mode

**✅ No Ambiguity:**

- SIM and LIVE are distinct execution modes
- SIM adapter verifies Sim account
- LIVE mode explicitly disabled

---

## 8. Output & Observability

### External Observability

**✅ Strong Observability:**

1. **RobotLogger** (`RobotLogger.cs`):
   - Writes JSONL logs to `logs/robot/robot_skeleton.jsonl`
   - ✅ **Correct:** Structured logging

2. **Event Types** (`RobotEvents.cs`):
   - Engine events, stream events, execution events
   - ✅ **Correct:** Comprehensive event coverage

3. **Execution Journal** (`ExecutionJournal.cs`):
   - Persists execution attempts, fills, rejections
   - ✅ **Correct:** Audit trail for execution

4. **Stream Journal** (`JournalStore.cs`):
   - Persists stream state, commitment status
   - ✅ **Correct:** State persistence and audit

### Execution Intent Auditability

**✅ Strong Auditability:**

1. **Intent ID** (lines 55-58 in `Intent.cs`):
   - Computed from canonical fields (trading_date, stream, instrument, session, slot_time, direction, prices)
   - ✅ **Correct:** Deterministic intent identification

2. **Execution Journal** (lines 89-140 in `ExecutionJournal.cs`):
   - Records submission attempts, fills, rejections
   - ✅ **Correct:** Complete execution audit trail

3. **Log Events**:
   - `ENTRY_SUBMITTED`, `ENTRY_FILLED`, `EXECUTION_BLOCKED`, etc.
   - ✅ **Correct:** Comprehensive execution logging

### Failure Diagnosability

**✅ Good Diagnosability:**

1. **Error Logging**:
   - All errors logged with context (trading_date, stream, instrument, reason)
   - ✅ **Correct:** Errors are diagnosable

2. **State Logging**:
   - State transitions logged with event types
   - ✅ **Correct:** State changes are observable

**⚠️ Missing Diagnostics:**

1. **Slot Time Update Skip** (lines 371-375):
   - Silent skip, no log
   - ⚠️ **MODERATE:** Should log for diagnostics

2. **Intent Registration Failure** (lines 661-666):
   - Silent failure, no log
   - ⚠️ **MODERATE:** Should log for diagnostics

### Missing Logs

**⚠️ Missing Log Cases:**

1. **Duplicate Stream IDs**:
   - No log when same stream ID appears multiple times in timetable
   - ⚠️ **MODERATE:** Should log for observability

2. **Slot Time Update Skip**:
   - No log when update skipped due to empty slot_time
   - ⚠️ **MODERATE:** Should log for diagnostics

3. **Intent Registration Failure**:
   - No log if reflection fails to register intent
   - ⚠️ **MODERATE:** Should log for diagnostics

### Ambiguous Messages

**✅ Clear Messages:**

- Error reasons are explicit: `MISSING_FIELDS`, `UNKNOWN_SESSION`, `INVALID_SLOT_TIME`, etc.
- Event types are descriptive: `TIMETABLE_INVALID`, `STREAM_SKIPPED`, `EXECUTION_BLOCKED`, etc.
- ✅ **Correct:** Messages are clear and actionable

### Post-Hoc Reasoning

**✅ Good State Tracking:**

1. **Journal Persistence**:
   - Stream journals persist state, commitment status, timetable hash
   - ✅ **Correct:** Can reconstruct state from journals

2. **Execution Journals**:
   - Persist intent IDs, submission times, fill prices
   - ✅ **Correct:** Can audit execution history

3. **Log Timestamps**:
   - All logs include UTC and Chicago timestamps
   - ✅ **Correct:** Time-aware analysis possible

**✅ States Are Reasonable:**

- State machine states are clear: IDLE, ARMED, RANGE_BUILDING, RANGE_LOCKED, DONE
- Commitment status is explicit
- ✅ **Correct:** States are easy to reason about

---

## Summary of Findings

### Confirmed Correctness ✅

1. **Timetable Consumption:** Strong fail-closed behavior, correct field validation
2. **Enabled Flag Handling:** Correctly ignores disabled streams, never infers tradability
3. **One Trade Per Day:** Strong enforcement via journal commitment, multiple safeguards
4. **Timezone Handling:** Consistent Chicago timezone usage, DST-aware
5. **State Machine:** Clean transitions, no re-entry risks, deterministic behavior
6. **Authority Boundaries:** Robot never overrides timetable, correctly reads-only
7. **Mode Isolation:** Clear separation between DRYRUN/SIM/LIVE, no leakage

### Critical Issues ⚠️

1. **Kill Switch Fail-Open** (`KillSwitch.cs` lines 73-85):
   - **Issue:** Kill switch file errors default to "disabled" (fail-open)
   - **Risk:** If kill switch file corrupted, Robot continues execution when it should stop
   - **Severity:** CRITICAL - Safety mechanism fails open
   - **Recommendation:** Change to fail-closed (treat as enabled if file error)

### Moderate Risks ⚠️

1. **Execution Journal Corruption Fail-Open** (`ExecutionJournal.cs` lines 77-80, 292-295):
   - **Issue:** Journal corruption defaults to "safe" state (not submitted, not modified)
   - **Risk:** Could allow duplicate submissions/modifications if journal corrupted
   - **Severity:** MODERATE - Mitigated by intent ID checks
   - **Recommendation:** Consider fail-closed or explicit error handling

2. **Silent Slot Time Update Skip** (`RobotEngine.cs` lines 371-375):
   - **Issue:** Update skipped silently if slot_time empty
   - **Risk:** Reduced observability, harder to diagnose
   - **Severity:** MODERATE - Functional correctness OK, observability issue
   - **Recommendation:** Add log event for skipped update

3. **Intent Registration Silent Failure** (`StreamStateMachine.cs` lines 661-666):
   - **Issue:** Reflection-based intent registration fails silently
   - **Risk:** Protective orders might not be submitted automatically
   - **Severity:** MODERATE - Manual intervention might be required
   - **Recommendation:** Add error log if registration fails

4. **Duplicate Stream ID Handling** (`RobotEngine.cs` line 289):
   - **Issue:** Duplicate stream IDs processed silently (last wins)
   - **Risk:** Reduced observability
   - **Severity:** MODERATE - Functional correctness OK
   - **Recommendation:** Log warning when duplicate stream ID detected

### Acceptable Design Choices ✅

1. **Stream State Persistence:** Existing streams not in timetable remain active (acceptable - will commit at market close)
2. **Replay Detection via File Read:** Reads timetable file every bar (acceptable - FilePoller caches)
3. **Partial Execution on Order Failures:** Order failures don't stop stream execution (acceptable - correct behavior)
4. **DecisionTime Field Ignored:** Robot correctly ignores DecisionTime field (acceptable - informational only)

---

## Recommendations

### Critical (Must Fix)

1. **Fix Kill Switch Fail-Open Behavior:**
   - Change `KillSwitch.cs` error handling to fail-closed
   - If kill switch file error, treat as enabled (block execution)
   - Log error clearly indicating fail-closed behavior

### Moderate (Should Fix)

1. **Add Logging for Silent Failures:**
   - Log when slot_time update skipped
   - Log when intent registration fails
   - Log when duplicate stream ID detected

2. **Consider Execution Journal Fail-Closed:**
   - Evaluate if journal corruption should fail-closed
   - If fail-open is intentional, document rationale

### Low Priority (Nice to Have)

1. **Validate DecisionTime Field:**
   - Add explicit validation that DecisionTime is not used
   - Document that DecisionTime is informational only

2. **Optimize Replay Detection:**
   - Cache replay flag in RobotEngine instead of reading every bar
   - Only re-read when timetable changes

---

## Conclusion

The Robot execution engine demonstrates **strong architectural correctness** and **fail-closed behavior** in most areas. The code correctly respects authority boundaries, enforces one trade per stream per day, and handles timetable consumption safely.

The **critical issue** with kill switch fail-open behavior should be addressed immediately, as it undermines a key safety mechanism. The **moderate risks** are primarily observability issues that should be addressed to improve diagnosability.

Overall, the Robot is **well-architected** for its role as a deterministic interpreter of the timetable, with clear separation of concerns and strong safety mechanisms.

---

**End of Review**
