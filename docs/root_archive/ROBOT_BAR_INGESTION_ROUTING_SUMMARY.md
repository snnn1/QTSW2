# Robot Bar Ingestion & Routing Summary (Authoritative)

**Date**: 2026-01-29  
**Purpose**: Pure factual summary of current bar ingestion and routing behavior. No proposed changes.

---

## 1. Where Bars Enter the System

### Entry Point
- **File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`
- **Method**: `OnBarUpdate()` (line 885)
- **Callback Type**: NinjaTrader `OnBarClose` (line 68)
  - Strategy uses `Calculate = Calculate.OnBarClose` to avoid blocking Realtime transition
  - `OnMarketData()` override handles tick-based break-even detection (line 1162)

### Instrument Identifiers Available at Entry Point

When `OnBarUpdate()` is called, the following instrument identifiers are available:

1. **`Instrument.FullName`** (line 895)
   - Contains full contract name with month (e.g., `"MES 03-26"`, `"M2K 03-26"`)
   - Used for bar tracking in `ONBARUPDATE_CALLED` events

2. **`Instrument.MasterInstrument.Name`** (line 896, 931, 1120)
   - Returns canonical/base instrument name
   - For micro futures: `"MGC"` → `"GC"`, `"M2K"` → `"RTY"`, `"MES"` → `"ES"`
   - For regular futures: `"ES"` → `"ES"` (unchanged)
   - **This is what is passed to `_engine.OnBar()`** (line 1120)

3. **Extraction Logic** (lines 129-164)
   - During initialization, the strategy extracts execution instrument name:
     - If `Instrument.FullName` exists, extracts root (e.g., `"MGC 03-26"` → `"MGC"`)
     - Compares extracted name vs `MasterInstrument.Name`
     - If different (micro futures), uses extracted name
     - If same (regular futures), uses `MasterInstrument.Name`

### What Instrument Name is Logged in Events

#### ONBARUPDATE_CALLED Event (lines 904-913)
- **`instrument`**: Canonical instrument (`Instrument.MasterInstrument.Name`) - **backward compatibility**
- **`execution_instrument_full_name`**: Full contract name (`Instrument.FullName`) - e.g., `"M2K 03-26"`

**Example log entry**:
```json
{
  "event_type": "ONBARUPDATE_CALLED",
  "instrument": "RTY",  // Canonical (backward compatibility)
  "execution_instrument_full_name": "M2K 03-26",  // Full contract
  "engine_ready": true,
  "current_bar": 1,
  "state": "Realtime"
}
```

#### ONBARUPDATE_DIAGNOSTIC Event (lines 939-948)
- **`instrument`**: Canonical instrument (`Instrument.MasterInstrument.Name`)

#### BAR_ROUTING_DIAGNOSTIC Event (lines 1473-1482)
- **`raw_instrument`**: Execution instrument passed to `OnBar()`
- **`canonical_instrument`**: Canonical instrument derived via `GetCanonicalInstrument()`

**Clarification**: Events emit **mixed** identifiers:
- `ONBARUPDATE_CALLED`: Both canonical (`instrument`) and full contract (`execution_instrument_full_name`)
- Diagnostic events: Canonical instrument (`instrument`)
- Routing events: Both raw execution and canonical

---

## 2. How Bars Are Routed to Streams

### Routing Location
- **File**: `modules/robot/core/RobotEngine.cs`
- **Method**: `OnBar()` (line 1074)
- **Routing Logic**: Lines 1434-1468

### Routing Process

1. **Bar arrives** at `RobotEngine.OnBar(barUtc, instrument, ...)` (line 1074)
   - `instrument` parameter is **execution instrument** from NinjaTrader (`Instrument.MasterInstrument.Name`)

2. **For each stream** in `_streams.Values` (line 1434):
   - Calls `stream.IsSameInstrument(instrument)` (line 1438)

3. **If match**: Calls `stream.OnBar(...)` to buffer the bar (line 1441)

### How IsSameInstrument() Works

**File**: `modules/robot/core/StreamStateMachine.cs` (line 422)

```csharp
public bool IsSameInstrument(string incomingInstrument)
{
    // PHASE 2: Canonicalize incoming instrument for comparison
    var incomingCanonical = GetCanonicalInstrument(incomingInstrument, _spec);
    return string.Equals(
        CanonicalInstrument,
        incomingCanonical,
        StringComparison.OrdinalIgnoreCase
    );
}
```

**What it compares**:
- **Input**: Execution instrument (e.g., `"MES"`, `"M2K"`, `"RTY"`)
- **Process**: Canonicalizes incoming instrument via `GetCanonicalInstrument()`
- **Comparison**: Compares stream's `CanonicalInstrument` vs canonicalized incoming instrument

**Canonical Mapping Logic** (`GetCanonicalInstrument`, line 405):
```csharp
private static string GetCanonicalInstrument(string executionInstrument, ParitySpec spec)
{
    if (spec != null &&
        spec.TryGetInstrument(executionInstrument, out var inst) &&
        inst.is_micro &&
        !string.IsNullOrWhiteSpace(inst.base_instrument))
    {
        return inst.base_instrument.ToUpperInvariant(); // MES → ES
    }
    return executionInstrument.ToUpperInvariant(); // ES → ES
}
```

**Routing is based on**: **Canonical instrument matching**
- Execution instrument (e.g., `"MES"`) is canonicalized to `"ES"`
- Stream's `CanonicalInstrument` (e.g., `"ES"`) is compared
- Match occurs if canonical instruments are equal

**Example**:
- Bar arrives with `instrument = "MES"` (execution)
- `GetCanonicalInstrument("MES")` → `"ES"` (canonical)
- Stream with `CanonicalInstrument = "ES"` matches
- Bar is routed to that stream

---

## 3. What Bars Each Stream Actually Receives

### Stream Instrument Properties

**File**: `modules/robot/core/StreamStateMachine.cs` (lines 45-56)

- **`Instrument`**: Canonical instrument (e.g., `"ES"`, `"RTY"`) - **LOGIC identity**
- **`ExecutionInstrument`**: Execution instrument (e.g., `"MES"`, `"M2K"`) - **ORDER identity**
- **`CanonicalInstrument`**: Canonical instrument (same as `Instrument`)

### For a Stream Like RTY1

**Stream Configuration**:
- `Stream`: `"RTY1"` (or `"RTY1_S1"`, etc.)
- `CanonicalInstrument`: `"RTY"`
- `ExecutionInstrument`: `"M2K"` (if micro) or `"RTY"` (if regular)

**Which Execution Instruments' Bars Are Accepted**:

1. **M2K bars** (micro futures):
   - Bar arrives with `instrument = "M2K"` (execution)
   - `GetCanonicalInstrument("M2K")` → `"RTY"` (canonical)
   - Stream's `CanonicalInstrument = "RTY"` matches
   - **M2K bars ARE routed to RTY1 stream** ✅

2. **RTY bars** (regular futures):
   - Bar arrives with `instrument = "RTY"` (execution)
   - `GetCanonicalInstrument("RTY")` → `"RTY"` (canonical)
   - Stream's `CanonicalInstrument = "RTY"` matches
   - **RTY bars ARE routed to RTY1 stream** ✅

**Conclusion**: RTY1 stream receives bars from **both M2K and RTY** execution instruments, because both canonicalize to `"RTY"`.

**Confirmed from code**:
- `IsSameInstrument()` uses canonical matching (line 422-431)
- `GetCanonicalInstrument()` maps micro futures to base instrument (line 405-415)
- Routing loop in `RobotEngine.OnBar()` uses `IsSameInstrument()` (line 1438)

---

## 4. How Bar Buffering Works

### Where Bars Are Stored

**File**: `modules/robot/core/StreamStateMachine.cs`

- **Storage**: `_barBuffer` (line 112) - `List<Bar>`
- **Lock**: `_barBufferLock` (line 113) - per-stream synchronization
- **Buffering is per stream**: Each `StreamStateMachine` instance has its own `_barBuffer`

### Buffering Logic

**Method**: `StreamStateMachine.OnBar()` (line 2621)

**Buffering Criteria** (line 2736):
```csharp
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
{
    // Buffer the bar
}
```

**Bars are buffered if**:
- Bar timestamp (Chicago time) >= `RangeStartChicagoTime`
- Bar timestamp (Chicago time) < `SlotTimeChicagoTime`
- Bar is within the range window `[RangeStartChicagoTime, SlotTimeChicagoTime)`

**State-Independent Buffering** (lines 2642-2645):
- Bars within range window are **always buffered**, regardless of stream state
- State only gates decisions (range computation, execution), not data ingestion

### Counters and Tracking

**Per-Stream Counters** (lines 128-132):
- `_historicalBarCount`: Bars from BarsRequest/pre-hydration
- `_liveBarCount`: Bars from live feed (`OnBar`)
- `_filteredFutureBarCount`: Bars filtered (future)
- `_filteredPartialBarCount`: Bars filtered (partial/in-progress)
- `_dedupedBarCount`: Bars deduplicated (replaced existing)

**Per-Stream Timestamps**:
- `_lastBarReceivedUtc`: Last bar received timestamp (line 2704)
- `_lastBarTimestampUtc`: Last bar timestamp (line 2705)

**Engine-Level Counters** (`RobotEngine.cs`):
- `_barRejectionStats`: Per-instrument rejection statistics (line 1095)
  - `TotalAccepted`, `PartialRejected`, `DateMismatch`, `BeforeDateLocked`

### When a Stream Considers "Bars Are Available"

**Bars are available** when:
1. `_barBuffer.Count > 0` (bars exist in buffer)
2. Bars are within range window `[RangeStartChicagoTime, SlotTimeChicagoTime)`
3. Stream state allows processing (not `DONE` or `COMMITTED`)

**Range computation** uses buffered bars (line 4040):
```csharp
// Use bar buffer for range computation (bars from pre-hydration + OnBar)
```

---

## 5. How Bar Liveness is Currently Tracked

### In the Robot

**File**: `modules/robot/core/RobotEngine.cs`

**Last Tick Timestamp** (lines 162-168):
- `_lastTickUtc`: Updated on every `OnBar()` call (line 1156)
- Exposed via `GetLastTickUtc()` method

**Health Monitor** (line 1152):
- `_healthMonitor?.OnBar(instrument, barUtc)` - records bar reception per instrument

**Bar Heartbeat** (lines 1158-1182):
- `ENGINE_BAR_HEARTBEAT` event (rate-limited: once per instrument per minute)
- Logs bar reception with instrument, timestamps, prices

### In the Watchdog

**File**: `modules/watchdog/state_manager.py`

**Last Bar Timestamp Storage** (lines 60-65):
- `_last_bar_utc_by_execution_instrument`: Dict mapping execution instrument full name → last bar UTC
  - Key: Full contract name (e.g., `"MES 03-26"`, `"M2K 03-26"`)
  - Value: `datetime` UTC timestamp

**Update Method** (lines 243-256):
```python
def update_last_bar(self, execution_instrument_full_name: str, timestamp_utc: datetime):
    # Store by full name
    self._last_bar_utc_by_execution_instrument[execution_instrument_full_name] = timestamp_utc
    # Also store by root name for backward compatibility
    root_name = execution_instrument_full_name.split()[0] if ' ' in execution_instrument_full_name else execution_instrument_full_name
    self._last_bar_utc_by_instrument[root_name] = timestamp_utc
```

**Event Processing** (`modules/watchdog/event_processor.py`, lines 130-148):
- `ONBARUPDATE_CALLED` events update last bar timestamp
- Uses `execution_instrument_full_name` field (line 138)
- Falls back to `instrument` field if `execution_instrument_full_name` not present (line 145)

**Key Used**: **Execution instrument full name** (e.g., `"MES 03-26"`), not canonical

**bars_expected() Evaluation** (lines 391-429):

```python
def bars_expected(self, execution_instrument_full_name: str, market_open: bool) -> bool:
    if not market_open:
        return False
    
    bar_dependent_states = {"PRE_HYDRATION", "ARMED", "RANGE_BUILDING", "RANGE_LOCKED"}
    
    for (trading_date, stream), info in self._stream_states.items():
        stream_execution_instrument = getattr(info, 'execution_instrument', None)
        if stream_execution_instrument:
            # Match by full name or root name
            if stream_execution_instrument == execution_instrument_full_name:
                if info.state in bar_dependent_states and not info.committed:
                    return True
```

**bars_expected() returns True if**:
- Market is open
- At least one stream for that execution instrument is in a bar-dependent state
- Stream is not committed

**Data Stall Detection** (lines 682-692):
- Uses `_last_bar_utc_by_execution_instrument` to compute bar age
- Compares current time vs last bar timestamp
- Flags data stall if bar age exceeds threshold

---

## 6. Concrete Example Walkthrough

### Example: Strategy Enabled on MES 03-26

**Setup**:
- NinjaTrader strategy running on `MES 03-26` contract
- Stream `ES1_S1` exists with:
  - `CanonicalInstrument = "ES"`
  - `ExecutionInstrument = "MES"`
  - `RangeStartChicagoTime = 02:00 CT`
  - `SlotTimeChicagoTime = 07:30 CT`

**Step-by-Step Flow**:

1. **Bar Arrives** (NinjaTrader calls `OnBarUpdate()`)
   - `Instrument.FullName = "MES 03-26"`
   - `Instrument.MasterInstrument.Name = "ES"` (canonical)

2. **Logged as What** (`ONBARUPDATE_CALLED` event, line 904):
   ```json
   {
     "instrument": "ES",  // Canonical (backward compatibility)
     "execution_instrument_full_name": "MES 03-26",  // Full contract
     "engine_ready": true
   }
   ```

3. **Routed as What** (`RobotEngine.OnBar()`, line 1120):
   - `_engine.OnBar(barUtcOpenTime, "ES", open, high, low, close, nowUtc)`
   - **Note**: Passes canonical instrument `"ES"`, not execution `"MES"`

4. **Routing Decision** (`RobotEngine.OnBar()`, lines 1434-1438):
   - For each stream, calls `stream.IsSameInstrument("ES")`
   - `IsSameInstrument("ES")`:
     - Canonicalizes: `GetCanonicalInstrument("ES")` → `"ES"`
     - Compares: `stream.CanonicalInstrument = "ES"` vs `"ES"`
     - **Match**: Returns `true`

5. **Buffered Where** (`StreamStateMachine.OnBar()`, line 2736):
   - Bar timestamp converted to Chicago time
   - Check: `barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime`
   - If true: Bar added to `_barBuffer` (per-stream buffer)

6. **Stream Sees What**:
   - Stream `ES1_S1` receives bar in its `_barBuffer`
   - Bar is available for range computation when stream enters `RANGE_BUILDING` state
   - Bar counters updated: `_liveBarCount++`

**Watchdog Tracking**:
- `ONBARUPDATE_CALLED` event processed (line 130)
- `update_last_bar("MES 03-26", timestamp_utc)` called (line 141)
- `_last_bar_utc_by_execution_instrument["MES 03-26"] = timestamp_utc` (line 252)
- `bars_expected("MES 03-26", market_open=True)` checks stream states (line 391)

---

## 7. Key Observations

### Instrument Identity Flow

1. **NinjaTrader → RobotEngine**: Passes canonical instrument (`Instrument.MasterInstrument.Name`)
   - For micro futures: `"MES"` → `"ES"` (already canonicalized by NinjaTrader)
   - **Note**: This is inconsistent with the initialization logic that extracts execution instrument

2. **RobotEngine → Stream**: Routes via canonical matching
   - Execution instrument canonicalized via `GetCanonicalInstrument()`
   - Matched against stream's `CanonicalInstrument`

3. **Watchdog**: Tracks by execution instrument full name
   - Uses `execution_instrument_full_name` from `ONBARUPDATE_CALLED` events
   - Key: `"MES 03-26"` (full contract), not canonical `"ES"`

### Potential Inconsistencies

1. **OnBar() Parameter**: `RobotSimStrategy.OnBarUpdate()` passes `Instrument.MasterInstrument.Name` (canonical) to `_engine.OnBar()`, but initialization extracts execution instrument name. This means:
   - Initialization: Uses execution instrument (e.g., `"MGC"`)
   - OnBar: Uses canonical instrument (e.g., `"GC"`)
   - **Mismatch**: Engine receives canonical instrument in `OnBar()`, not execution instrument

2. **Event Logging**: `ONBARUPDATE_CALLED` logs both canonical (`instrument`) and full contract (`execution_instrument_full_name`), but `_engine.OnBar()` receives canonical instrument only.

3. **Watchdog Key**: Uses execution instrument full name, but robot routes by canonical instrument. This is correct for tracking, but creates a disconnect between routing (canonical) and tracking (execution).

---

## Summary

**Bar Entry**: NinjaTrader `OnBarUpdate()` → `RobotSimStrategy.OnBarUpdate()` → `RobotEngine.OnBar()`

**Instrument Identifiers**:
- `Instrument.FullName`: Full contract (e.g., `"MES 03-26"`)
- `Instrument.MasterInstrument.Name`: Canonical (e.g., `"ES"`)

**Routing**: Based on canonical instrument matching via `IsSameInstrument()`

**Buffering**: Per-stream `_barBuffer`, stores bars within range window `[RangeStartChicagoTime, SlotTimeChicagoTime)`

**Liveness Tracking**:
- Robot: `_lastTickUtc`, `ENGINE_BAR_HEARTBEAT` events
- Watchdog: `_last_bar_utc_by_execution_instrument` (keyed by execution instrument full name)

**Example**: MES 03-26 bars → logged as `"ES"` (canonical) + `"MES 03-26"` (full contract) → routed to ES streams via canonical matching → buffered per stream → tracked by watchdog using execution instrument full name.
