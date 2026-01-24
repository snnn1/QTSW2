# Phase 3: Identity Flow Map & Execution Abstraction

**Date**: 2026-01-24  
**Purpose**: Document identity flow and identify canonical vs execution boundaries

## Identity Flow Map

### Entry Point: RobotSimStrategy.cs

| Location | Field | Current Type | Should Be | Notes |
|----------|-------|--------------|-----------|-------|
| Line 95 | `engineInstrumentName` | Execution (MES) | Execution | From NT `Instrument.MasterInstrument.Name` |
| Line 96 | `RobotEngine(instrument:)` | Execution (MES) | Execution | Passed to engine constructor |
| Line 121 | `SetSessionStartTime(instrument:)` | Execution (MES) | Execution | Session timing per execution contract |

**Classification**: ✅ **EXECUTION** - All NT-provided instrument names are execution contracts.

---

### RobotEngine.cs - Engine Core

| Location | Field | Current Type | Should Be | Notes |
|----------|-------|--------------|-----------|-------|
| Line 190 | Constructor `instrument` param | Execution (MES) | Execution | Stored but not used for logic |
| Line 886 | `OnBar(instrument:)` | Execution (MES) | Execution | From NT, must match NT contract |
| Line 922 | `IsSameInstrument(instrument)` | Execution (MES) | Execution | Input is execution, comparison uses canonical |
| Line 1235 | `s.IsSameInstrument(instrument)` | Execution (MES) | Execution | Routes bars by canonical matching |
| Line 1994 | `ApplyTimetable()` - `executionInstrument` | Execution (MES) | Execution | From timetable directive |
| Line 1994 | `ApplyTimetable()` - `canonicalInstrument` | Canonical (ES) | Canonical | Computed via `GetCanonicalInstrument()` |
| Line 2007 | `streamId` canonicalization | Mixed | Canonical | MES1 → ES1 mapping |
| Line 2072 | `TryGetInstrument(canonicalInstrument)` | Canonical (ES) | Canonical | Timetable validation uses canonical |

**Classification**: 
- **Inputs**: Execution (from NT/timetable)
- **Logic**: Canonical (stream matching, timetable lookup)
- **Boundary**: `OnBar()` receives execution, routes via canonical matching

---

### StreamStateMachine.cs - Stream Logic

| Location | Field | Current Type | Should Be | Notes |
|----------|-------|--------------|-----------|-------|
| Line 46 | `Instrument` property | Canonical (ES) | Canonical | ✅ Phase 2: Logic identity |
| Line 51 | `ExecutionInstrument` property | Execution (MES) | Execution | ✅ Phase 2: Order placement |
| Line 56 | `CanonicalInstrument` property | Canonical (ES) | Canonical | ✅ Phase 2: Explicit canonical |
| Line 225 | `Stream` property | Canonical (ES1) | Canonical | ✅ Phase 2: Stream ID canonicalized |
| Line 295 | `IsSameInstrument(incomingInstrument)` | Execution (MES) | Execution | Input is execution, compares canonical |
| Line 2666 | `SubmitStopEntryOrder(Instrument)` | ❌ **WRONG** | ExecutionInstrument | Currently uses `Instrument` (canonical) |
| Line 3779 | `SubmitEntryOrder(ExecutionInstrument)` | Execution (MES) | Execution | ✅ Phase 2: Correct |
| Line 3710 | `ComputeIntentId()` - `Instrument` | Canonical (ES) | Canonical | Intent ID uses canonical for logic identity |
| Line 3795 | `RecordSubmission(ExecutionInstrument)` | Execution (MES) | Execution | ✅ Phase 2: Correct |

**Classification**:
- **Logic fields**: Canonical (Instrument, Stream, CanonicalInstrument)
- **Order placement**: ExecutionInstrument ✅
- **Intent ID**: Uses canonical Instrument ✅
- **Journal**: Uses ExecutionInstrument ✅

**⚠️ ISSUE FOUND**: Line 2666 uses `Instrument` instead of `ExecutionInstrument` for stop entry orders.

---

### ExecutionJournal.cs - Journal & Intent Tracking

| Location | Field | Current Type | Should Be | Notes |
|----------|-------|--------------|-----------|-------|
| Line 52 | `ComputeIntentId(stream, instrument)` | Mixed | Canonical | Stream is canonical (ES1), instrument should be canonical (ES) |
| Line 135 | `RecordSubmission(stream, instrument)` | Mixed | Mixed | Stream is canonical (ES1), instrument is execution (MES) ✅ |
| Line 64 | Journal file key: `{tradingDate}|{stream}|{instrument}` | Mixed | Mixed | Stream canonical, instrument execution |

**Classification**:
- **Intent ID**: Should use canonical (logic identity)
- **Journal entries**: Stream canonical, instrument execution ✅
- **File naming**: Uses canonical stream, execution instrument ✅

**⚠️ ISSUE FOUND**: `ComputeIntentId()` receives `instrument` parameter that may be ambiguous. Currently called with `Instrument` (canonical) from StreamStateMachine, which is correct.

---

### NinjaTraderSimAdapter.cs - Order Execution

| Location | Field | Current Type | Should Be | Notes |
|----------|-------|--------------|-----------|-------|
| Line 154 | `SubmitEntryOrder(instrument:)` | Execution (MES) | Execution | ✅ Phase 2: Correct |
| Line 156 | Parameter type | `string instrument` | Execution | Should be explicit: `string executionInstrument` |
| Line 263 | `SubmitStopEntryOrder(instrument:)` | Execution (MES) | Execution | ✅ Phase 2: Correct |
| Line 83 | `SetNTContext(instrument:)` | Execution (MES) | Execution | NT-provided contract |

**Classification**: ✅ **EXECUTION** - All adapter methods correctly use execution instrument.

**⚠️ AMBIGUITY**: Parameter name `instrument` is ambiguous. Should be `executionInstrument` for clarity.

---

### Watchdog Event Processing

| Location | Field | Current Type | Should Be | Notes |
|----------|-------|--------------|-----------|-------|
| `event_processor.py:91` | `event.get("instrument")` | Mixed | Canonical | Robot logs now emit canonical |
| `event_processor.py:90` | `event.get("stream")` | Mixed | Canonical | Robot logs now emit canonical |
| `event_processor.py:130` | Canonicalization applied | N/A | Conditional | Should trust robot fields if present |
| `state_manager.py:107` | Stream key `(trading_date, stream)` | Canonical (ES1) | Canonical | ✅ Phase 2: Correct |

**Classification**:
- **Robot events**: Now emit canonical instrument/stream (Phase 2)
- **Watchdog processing**: Currently canonicalizes (redundant if robot already canonical)
- **State keys**: Canonical ✅

**⚠️ ISSUE**: Watchdog canonicalizes even when robot already emits canonical fields. Should trust robot fields and only canonicalize if missing.

---

## Ambiguous Functions (Bare String Parameters)

### High Priority

1. **`ExecutionJournal.ComputeIntentId(string instrument)`**
   - **Current**: Receives canonical Instrument from StreamStateMachine ✅
   - **Risk**: Parameter name is ambiguous
   - **Fix**: Rename to `canonicalInstrument` or add ExecutionContext parameter

2. **`NinjaTraderSimAdapter.SubmitEntryOrder(string instrument)`**
   - **Current**: Receives ExecutionInstrument ✅
   - **Risk**: Parameter name is ambiguous
   - **Fix**: Rename to `executionInstrument`

3. **`NinjaTraderSimAdapter.SubmitStopEntryOrder(string instrument)`**
   - **Current**: Receives ExecutionInstrument ✅
   - **Risk**: Parameter name is ambiguous
   - **Fix**: Rename to `executionInstrument`

### Medium Priority

4. **`RobotEngine.OnBar(string instrument)`**
   - **Current**: Receives execution instrument from NT ✅
   - **Risk**: Parameter name is ambiguous
   - **Fix**: Rename to `executionInstrument` or add ExecutionContext

5. **`StreamStateMachine.IsSameInstrument(string instrument)`**
   - **Current**: Receives execution instrument, compares canonical ✅
   - **Risk**: Parameter name is ambiguous
   - **Fix**: Rename to `executionInstrument`

---

## Summary: Identity Boundaries

### Execution Boundary (NT → Robot)
- **Entry**: RobotSimStrategy receives execution instrument from NT
- **Preservation**: ExecutionInstrument stored separately
- **Usage**: Order placement, journal execution tracking

### Logic Boundary (Robot → Streams)
- **Entry**: Timetable provides execution instrument
- **Conversion**: Canonicalized immediately in ApplyTimetable
- **Usage**: Stream IDs, timetable lookup, bar routing, intent IDs

### Watchdog Boundary (Robot → Watchdog)
- **Entry**: Robot logs emit canonical instrument/stream (Phase 2)
- **Processing**: Watchdog should trust robot fields
- **Fallback**: Canonicalize only if fields missing

---

## Required Changes

### Step B: ExecutionContext Structure
- Add `ExecutionContext` class with canonical + execution fields
- Route through adapter calls

### Step C: Hardened Boundaries
- Rename ambiguous `instrument` parameters
- Add assertions at boundaries
- Fix stop entry order bug (line 2666)

### Step D: Watchdog Trust Robot Fields
- Check if robot events contain canonical fields
- Only canonicalize if missing
- Store execution_instrument separately if present

### Step E: Self-Test Diagnostic
- Emit CANONICALIZATION_SELF_TEST at startup
- Include canonical/execution mapping

### Step F: Verification Report
- List top 10 events to check
- Provide runbook for MES strategy
