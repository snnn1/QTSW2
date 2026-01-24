# Mini/Micro = Same Market (Canonical Instrument) Handling Assessment

**Date**: 2026-01-24  
**Status**: ❌ **Not Canonicalized**

## Executive Summary

The system currently **does NOT** treat mini and micro contracts as the same canonical market. If a strategy runs on MES, it operates as a distinct logical entity from ES, with separate streams, timetables, and watchdog identity.

**Answer to Final Check**: **NO** — If the robot is enabled on MES today, it does NOT fully and correctly use ES logic everywhere. It would use MES-specific streams, timetables, and logic.

---

## A. Current State Verdict

**❌ Not Canonicalized**

The system operates with execution instrument names (MES, ES) as the primary identity throughout the logic chain. There is no canonicalization layer that maps MES → ES for logic purposes.

---

## B. Evidence

### 1. Instrument Resolution Audit

**Finding**: No canonical instrument concept exists. Execution instrument names flow directly through the system.

**Evidence**:

- **Robot Engine Initialization** (`modules/robot/ninjatrader/RobotSimStrategy.cs:95-96`):
  ```csharp
  var engineInstrumentName = Instrument.MasterInstrument.Name;
  _engine = new RobotEngine(..., instrument: engineInstrumentName);
  ```
  The instrument name from NinjaTrader (e.g., "MES") is passed directly to the engine.

- **Instrument Config** (`configs/analyzer_robot_parity.json:96`):
  ```json
  "MES": { "tick_size": 0.25, "base_target": 10.0, "is_micro": true, "base_instrument": "ES", "scaling_factor": 0.1 }
  ```
  `base_instrument` exists but is **only used for scaling factors**, not logic identity.

- **InstrumentManager** (`modules/analyzer/logic/instrument_logic.py:63-65`):
  ```python
  def get_base_instrument(self, instrument: Instrument) -> str:
      """Get the base instrument for micro futures"""
      return self.instruments[instrument].base_instrument
  ```
  This method exists but is **not used** for stream identity, timetable lookup, or watchdog keys.

**Answer**: 
- ❌ MES is treated as distinct from ES everywhere
- ❌ No concept of `market_root` or `canonical_instrument` for logic purposes
- ⚠️ `base_instrument` exists but only for profit scaling, not logic identity

---

### 2. Timetable Lookup Audit

**Finding**: Timetables use execution instrument names directly. No canonicalization occurs.

**Evidence**:

- **Timetable Structure** (`modules/robot/core/Models.TimetableContract.cs:35`):
  ```csharp
  public string instrument { get; set; } = "";
  ```
  Timetable streams contain an `instrument` field that is used directly.

- **Stream Creation** (`modules/robot/core/RobotEngine.cs:1994, 215`):
  ```csharp
  var instrument = (directive.instrument ?? "").ToUpperInvariant();
  // ...
  Instrument = directive.instrument.ToUpperInvariant();
  ```
  Streams are created with the instrument name directly from the timetable directive.

- **Timetable Validation** (`modules/robot/core/RobotEngine.cs:2049`):
  ```csharp
  if (!_spec.TryGetInstrument(instrument, out _))
  {
      // Stream skipped - UNKNOWN_INSTRUMENT
  }
  ```
  The parity spec is checked for the exact instrument name from the timetable.

**Answer**:
- ❌ If strategy runs on MES, timetable must contain `"instrument": "MES"` entries
- ❌ No timetable_MES.json vs timetable_ES.json duplication exists (would be separate streams)
- ⚠️ **Logic duplication risk**: If both ES and MES are enabled, they create separate streams (ES1, ES2 vs MES1, MES2)

---

### 3. Stream Identity Audit (CRITICAL)

**Finding**: Stream IDs are keyed by execution instrument. ES and MES produce different streams.

**Evidence**:

- **Stream ID Formation** (`modules/analyzer/logic/instrument_logic.py:158-169`):
  ```python
  def get_stream_tag(self, instrument: str, session: str) -> str:
      return f"{instrument.upper()}{'1' if session=='S1' else '2'}"
  ```
  Stream tags are `{instrument}{session_number}`. MES + S1 = "MES1", ES + S1 = "ES1".

- **Timetable Stream Directive** (`modules/robot/core/StreamStateMachine.cs:214-215`):
  ```csharp
  Stream = directive.stream;  // e.g., "ES1" or "MES1"
  Instrument = directive.instrument.ToUpperInvariant();  // e.g., "ES" or "MES"
  ```
  Stream ID comes from timetable, instrument comes from timetable. No mapping occurs.

- **Bar Matching** (`modules/robot/core/RobotEngine.cs:1233`):
  ```csharp
  if (s.IsSameInstrument(instrument))
  {
      s.OnBar(...);
  }
  ```
  `IsSameInstrument()` does direct string comparison (`modules/robot/core/StreamStateMachine.cs:295`):
  ```csharp
  public bool IsSameInstrument(string instrument) =>
      string.Equals(Instrument, instrument, StringComparison.OrdinalIgnoreCase);
  ```
  No canonicalization - "MES" != "ES".

- **Watchdog Stream Keys** (`modules/watchdog/state_manager.py:107`):
  ```python
  key = (trading_date, stream)  # e.g., ("2026-01-24", "MES1")
  ```
  Stream keys use the stream ID directly from events. No canonicalization.

**Answer**:
- ❌ Stream IDs are keyed by execution instrument
- ❌ ES and MES produce **two different streams** today (ES1 vs MES1)
- ❌ Watchdog tracks them separately

---

### 4. Watchdog & UI Semantics Audit

**Finding**: Watchdog and UI display execution instrument names. No canonicalization.

**Evidence**:

- **Event Processing** (`modules/watchdog/event_processor.py:116, 136`):
  ```python
  instrument = event.get("instrument")
  # ...
  info.instrument = instrument
  ```
  Instrument name from robot logs is stored directly.

- **Stream States** (`modules/watchdog/aggregator.py:384`):
  ```python
  "instrument": getattr(info, 'instrument', ''),
  ```
  UI displays the instrument name directly from state.

- **Intent Exposure** (`modules/watchdog/state_manager.py:126-134`):
  ```python
  def update_intent_exposure(self, intent_id: str, stream_id: str, instrument: str, ...):
      self._intent_exposures[intent_id] = IntentExposureInfo(
          instrument=instrument,  # Direct from events
          ...
      )
  ```
  Intent exposures track instrument names directly.

**Answer**:
- ❌ MES and ES appear as **separate logical entities** in watchdog
- ❌ Stream tables show "MES1" vs "ES1" as distinct streams
- ❌ Event feeds show instrument="MES" vs instrument="ES"

---

### 5. P&L Aggregation Audit

**Finding**: P&L is grouped by execution instrument. MES and ES P&L would be fragmented.

**Evidence**:

- **Ledger Builder** (`modules/watchdog/pnl/ledger_builder.py:206-207`):
  ```python
  "stream": journal.get("stream", ""),  # e.g., "MES1"
  "instrument": journal.get("instrument", ""),  # e.g., "MES"
  ```
  P&L ledger rows contain execution instrument names directly.

- **P&L Aggregation** (`modules/watchdog/aggregator.py:86-90`):
  ```python
  streams_dict = defaultdict(list)
  for row in ledger_rows:
      stream_id = row.get("stream", "")  # e.g., "MES1"
      if stream_id:
          streams_dict[stream_id].append(row)
  ```
  P&L is aggregated by stream ID (which includes instrument name).

- **Execution Journal** (`modules/robot/core/Execution/ExecutionJournal.cs:64`):
  ```csharp
  var canonical = $"{tradingDate}|{stream}|{instrument}|...";
  ```
  Journal entries include execution instrument name directly.

**Answer**:
- ❌ P&L is grouped by execution instrument
- ❌ MES and ES P&L would be **fragmented** (separate streams, separate aggregation)
- ❌ No market-level aggregation exists

---

## C. Required Changes (If Any)

### Summary of Gaps

1. **Timetable Lookup**: Must map execution instrument → canonical instrument before timetable lookup
2. **Stream Identity**: Stream IDs must use canonical instrument (ES1, not MES1)
3. **Bar Matching**: Must match bars to streams using canonical instrument
4. **Watchdog Keys**: Stream keys must use canonical instrument
5. **P&L Aggregation**: Must aggregate by canonical instrument/market

### Minimal Surgical Changes

#### 1. Instrument Canonicalization Function

**File**: `modules/robot/core/RobotEngine.cs` (new method)

**Responsibility**: Map execution instrument → canonical instrument

**Before**: 
```csharp
// No mapping - uses execution instrument directly
var instrument = directive.instrument;
```

**After**:
```csharp
private string GetCanonicalInstrument(string executionInstrument)
{
    if (_spec?.TryGetInstrument(executionInstrument, out var inst) == true && inst.is_micro)
    {
        return inst.base_instrument;  // MES → ES
    }
    return executionInstrument;  // ES → ES
}
```

---

#### 2. Timetable Stream Creation

**File**: `modules/robot/core/RobotEngine.cs:1994`

**Responsibility**: Use canonical instrument for stream creation

**Before**:
```csharp
var instrument = (directive.instrument ?? "").ToUpperInvariant();
// ... creates stream with execution instrument
```

**After**:
```csharp
var executionInstrument = (directive.instrument ?? "").ToUpperInvariant();
var instrument = GetCanonicalInstrument(executionInstrument);  // MES → ES
// ... creates stream with canonical instrument
```

**Note**: Must preserve execution instrument separately for order placement (NinjaTrader adapter needs MES, not ES).

---

#### 3. Stream ID Formation

**File**: `modules/robot/core/RobotEngine.cs` (in `ApplyTimetable`)

**Responsibility**: Ensure stream IDs use canonical instrument

**Before**:
```csharp
var streamId = directive.stream;  // e.g., "MES1" from timetable
```

**After**:
```csharp
var canonicalInstrument = GetCanonicalInstrument(executionInstrument);
// Validate stream ID matches canonical instrument
// If timetable has "MES1", map to "ES1"
var streamId = directive.stream.Replace(executionInstrument, canonicalInstrument);
// Or: derive stream ID from canonical instrument + session
```

**Note**: Timetable generation must also be updated to use canonical instruments, OR robot must map timetable stream IDs.

---

#### 4. Bar Matching

**File**: `modules/robot/core/StreamStateMachine.cs:295`

**Responsibility**: Match bars using canonical instrument

**Before**:
```csharp
public bool IsSameInstrument(string instrument) =>
    string.Equals(Instrument, instrument, StringComparison.OrdinalIgnoreCase);
```

**After**:
```csharp
public bool IsSameInstrument(string instrument)
{
    // Get canonical instrument for both
    var canonicalInstrument = GetCanonicalInstrument(instrument);
    var canonicalThis = GetCanonicalInstrument(Instrument);
    return string.Equals(canonicalThis, canonicalInstrument, StringComparison.OrdinalIgnoreCase);
}
```

**Note**: Requires `GetCanonicalInstrument` helper accessible from StreamStateMachine.

---

#### 5. Watchdog Stream Keys

**File**: `modules/watchdog/event_processor.py` (new helper)

**Responsibility**: Canonicalize instrument in events before state updates

**Before**:
```python
instrument = event.get("instrument")  # e.g., "MES"
stream = event.get("stream")  # e.g., "MES1"
```

**After**:
```python
def get_canonical_instrument(instrument: str) -> str:
    """Map execution instrument to canonical instrument."""
    from modules.analyzer.logic.instrument_logic import InstrumentManager
    mgr = InstrumentManager()
    if mgr.is_micro_future(instrument):
        return mgr.get_base_instrument(instrument)
    return instrument

instrument = event.get("instrument")  # e.g., "MES"
canonical_instrument = get_canonical_instrument(instrument)  # "ES"
stream = event.get("stream")  # e.g., "MES1"
canonical_stream = stream.replace(instrument, canonical_instrument) if instrument in stream else stream  # "ES1"
```

**Note**: Must canonicalize both instrument and stream ID in watchdog state.

---

#### 6. P&L Aggregation

**File**: `modules/watchdog/pnl/ledger_builder.py`

**Responsibility**: Group by canonical instrument

**Before**:
```python
stream_id = row.get("stream", "")  # e.g., "MES1"
streams_dict[stream_id].append(row)
```

**After**:
```python
def canonicalize_stream(stream_id: str, instrument: str) -> str:
    """Map stream ID to canonical stream ID."""
    canonical_instrument = get_canonical_instrument(instrument)
    return stream_id.replace(instrument, canonical_instrument)

stream_id = row.get("stream", "")  # e.g., "MES1"
instrument = row.get("instrument", "")  # e.g., "MES"
canonical_stream_id = canonicalize_stream(stream_id, instrument)  # "ES1"
streams_dict[canonical_stream_id].append(row)
```

---

### Additional Considerations

1. **Execution Instrument Preservation**: The NinjaTrader adapter must still receive execution instrument (MES) for order placement. This should be stored separately from logic instrument.

2. **Timetable Generation**: The timetable generator must either:
   - Generate timetables with canonical instruments (ES), OR
   - Robot must map timetable entries (MES → ES) during application

3. **Logging**: Logs should include both execution instrument and canonical instrument for clarity.

4. **Backward Compatibility**: Existing execution journals and logs use execution instrument names. Migration may be needed, or dual-key lookup (canonical + execution).

---

## D. Impact Assessment

### Current Behavior (MES Strategy)

If a strategy runs on MES today:
- ✅ Uses MES-specific timetable entries (if they exist)
- ✅ Creates MES1, MES2 streams (separate from ES1, ES2)
- ✅ Watchdog tracks MES streams separately
- ✅ P&L aggregated separately for MES
- ❌ **Does NOT share timetable with ES**
- ❌ **Does NOT share streams with ES**
- ❌ **Does NOT share watchdog identity with ES**
- ❌ **Does NOT aggregate P&L with ES**

### Required Behavior (After Canonicalization)

If a strategy runs on MES after changes:
- ✅ Uses ES timetable entries (canonical)
- ✅ Creates ES1, ES2 streams (canonical)
- ✅ Watchdog tracks ES streams (MES events map to ES streams)
- ✅ P&L aggregated with ES (same market)
- ✅ Order placement still uses MES (execution instrument preserved)

---

## E. Conclusion

The system requires **canonicalization changes** to treat mini/micro contracts as the same market. The changes are surgical and focused, but touch critical paths:

1. **Robot Core**: Instrument resolution, stream creation, bar matching
2. **Watchdog**: Stream keys, state management
3. **P&L**: Aggregation grouping

The `base_instrument` concept exists in config but is **not used for logic identity**—only for profit scaling. This must be extended to drive all logic decisions.

**Final Answer**: **NO** — The robot enabled on MES today does NOT fully and correctly use ES logic everywhere. It operates as a distinct entity with separate streams, timetables, and watchdog identity.
