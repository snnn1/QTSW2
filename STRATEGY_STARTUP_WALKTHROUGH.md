# Strategy Startup Walkthrough

## What Happens When the Strategy is Turned On

This document walks through the complete execution flow when the NinjaTrader strategy is enabled.

---

## Phase 1: NinjaTrader Strategy Initialization (`State.DataLoaded`)

### Step 1: Account Verification
```csharp
// RobotSimStrategy.OnStateChange() - State.DataLoaded
```
- **Checks**: Account exists and is a SIM account
- **If invalid**: Logs error and aborts
- **If valid**: Sets `_simAccountVerified = true`

### Step 2: RobotEngine Construction
```csharp
_engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, ...)
```
- **Creates**: `RobotEngine` instance in SIM mode
- **Initializes**:
  - Logging service (async singleton)
  - Journal store
  - Timetable file poller (2-second interval)
  - Kill switch
  - Execution journal
  - Health monitor (if config exists)

### Step 3: Set Account Info
```csharp
_engine.SetAccountInfo(accountName, environment)
```
- **Stores**: Account name and environment ("SIM") for startup banner

### Step 4: Engine Start
```csharp
_engine.Start()
```
This is where the real initialization happens...

---

## Phase 2: RobotEngine.Start() Sequence

### Step 1: LIVE Mode Check
```csharp
if (_executionMode == ExecutionMode.LIVE)
```
- **If LIVE**: Blocks execution, sends emergency notification, throws exception
- **If SIM/DRYRUN**: Continues

### Step 2: Start Async Logging Service
```csharp
_loggingService?.Start()
```
- **Starts**: Background worker for async log writing
- **Logs**: `ENGINE_START` event

### Step 3: Load Parity Spec
```csharp
_spec = ParitySpec.LoadFromFile(_specPath)
_time = new TimeService(_spec.timezone)
```
- **Loads**: `configs/analyzer_robot_parity.json`
- **Validates**: Spec structure (sessions, instruments, etc.)
- **Creates**: `TimeService` with spec timezone ("America/Chicago")
- **Logs**: `SPEC_LOADED` event

### Step 4: Initialize Execution Components
```csharp
_riskGate = new RiskGate(_spec, _time, _log, _killSwitch)
_executionAdapter = ExecutionAdapterFactory.Create(_executionMode, ...)
```
- **Creates**: Risk gate (position sizing, risk limits)
- **Creates**: Execution adapter (`NinjaTraderSimAdapter` for SIM mode)
- **Wires**: Engine callbacks to adapter (stand-down, notifications)
- **Logs**: `EXECUTION_MODE_SET` event

### Step 5: Load Timetable and Lock Trading Date
```csharp
ReloadTimetableIfChanged(utcNow, force: true)
```
**CRITICAL**: This is where the trading date is locked!

**Process**:
1. **Reads**: `data/timetable/timetable_current.json`
2. **Parses**: `timetable.trading_date` (Chicago date, e.g., "2026-01-16")
3. **Validates**: Trading date exists and is valid
4. **Locks**: `_activeTradingDate = trading_date` (immutable for entire run)
5. **Logs**: `TRADING_DATE_LOCKED` event with `source="TIMETABLE"`

**Fail-Closed Rules**:
- If timetable missing `trading_date`: Logs error, calls `StandDown()`, no trading
- If trading date invalid: Logs error, calls `StandDown()`, no trading
- Trading date **never changes** after being set

### Step 6: Create Streams (If Trading Date Locked)
```csharp
if (_activeTradingDate.HasValue)
{
    EnsureStreamsCreated(utcNow)
    EmitStartupBanner(utcNow)
}
```

**EnsureStreamsCreated()**:
1. **Reads**: Timetable again (if not already loaded)
2. **Validates**: Timetable structure
3. **Iterates**: Over each enabled stream in timetable
4. **Creates**: `StreamStateMachine` instance for each stream

**StreamStateMachine Constructor**:
- **Extracts**: Stream config (instrument, session, slot_time)
- **Computes**: Time boundaries:
  - `RangeStartChicagoTime` (from spec: `sessions["S1"].range_start_time`, e.g., "02:00")
  - `SlotTimeChicagoTime` (from timetable: `slot_time`, e.g., "07:30")
  - Converts to UTC: `RangeStartUtc`, `SlotTimeUtc`
- **Loads**: Existing journal (if restart) or creates new journal
- **Detects**: Mid-session restart (journal exists, not committed, time >= range_start)
- **Initializes**: State to `PRE_HYDRATION` (for SIM/DRYRUN)
- **Logs**: `STREAM_CREATED` or `MID_SESSION_RESTART_DETECTED`

**EmitStartupBanner()**:
- **Logs**: `OPERATOR_BANNER` event with:
  - Execution mode (SIM)
  - Account name
  - Environment
  - Trading date
  - Enabled streams count
  - Enabled instruments
  - Spec name/revision
  - Kill switch status

### Step 7: Start Health Monitor
```csharp
_healthMonitor?.Start()
```
- **Starts**: Background monitoring for data feed health
- **Initializes**: Notification service worker

---

## Phase 3: NinjaTrader Strategy - Request Historical Bars

### Step 1: RequestHistoricalBarsForPreHydration()
```csharp
// Called immediately after engine.Start()
RequestHistoricalBarsForPreHydration()
```

**Process**:
1. **Gets**: Trading date from engine (`_engine.GetTradingDate()`)
2. **Gets**: Session info from spec (`_engine.GetSessionInfo(instrument, "S1")`):
   - `rangeStartChicago` (from spec: "02:00")
   - `slotTimeChicago` (from spec: first slot time, e.g., "07:30")
3. **Calculates**: End time for BarsRequest:
   - `endTimeChicago = min(slotTimeChicago, nowChicago)`
   - Prevents requesting future bars
4. **Requests**: Historical bars from NinjaTrader:
   ```csharp
   NinjaTraderBarRequest.RequestBarsForTradingDate(
       Instrument, tradingDate, rangeStartChicago, endTimeChicago, timeService)
   ```
5. **Filters**: Bars (future bars, partial bars < 1 minute old)
6. **Feeds**: Filtered bars to engine:
   ```csharp
   _engine.LoadPreHydrationBars(instrument, bars, utcNow)
   ```

**LoadPreHydrationBars()**:
- **Validates**: Streams exist (should be created in `Start()`)
- **Filters**: Future bars, partial bars (< 1 minute old)
- **Feeds**: Each bar to matching streams:
  ```csharp
  stream.OnBar(bar.TimestampUtc, bar.Open, bar.High, bar.Low, bar.Close, utcNow, isHistorical: true)
  ```
- **Marks**: Bars as `BarSource.BARSREQUEST` (historical)
- **Logs**: `PRE_HYDRATION_BARS_LOADED` event

**StreamStateMachine.OnBar()** (for each historical bar):
- **Validates**: Bar trading date matches stream trading date
- **Rejects**: Partial bars (< 1 minute old)
- **Buffers**: Bar in `_barBuffer` (if state is `PRE_HYDRATION`, `ARMED`, or `RANGE_BUILDING`)
- **Deduplicates**: By timestamp with precedence (LIVE > BARSREQUEST > CSV)
- **Tracks**: Bar source (`_historicalBarCount` or `_liveBarCount`)

### Step 2: Wire NinjaTrader Context
```csharp
WireNTContextToAdapter()
```
- **Wires**: Account, Instrument, Order events to `NinjaTraderSimAdapter`
- **Enables**: Order placement callbacks

---

## Phase 4: Strategy Enters Realtime (`State.Realtime`)

### Step 1: Start Tick Timer
```csharp
StartTickTimer()
```
- **Creates**: Periodic timer (1-second interval)
- **Calls**: `_engine.Tick(DateTimeOffset.UtcNow)` every second

**Engine.Tick()**:
- **Updates**: Engine heartbeat timestamp
- **Polls**: Timetable for changes (every 2 seconds)
- **Calls**: `stream.Tick(utcNow)` for each stream

**StreamStateMachine.Tick()**:
- **Handles**: State-specific logic based on current state
- **Transitions**: Between states based on time and bar availability

---

## Phase 5: Stream State Transitions

### Initial State: `PRE_HYDRATION`

**SIM Mode Behavior**:
- **Waits**: For bars to arrive (from BarsRequest or live feed)
- **Checks**: If bars exist in buffer OR current time >= `RangeStartChicagoTime`
- **Transitions**: To `ARMED` when conditions met
- **Logs**: `PRE_HYDRATION_COMPLETE_SIM`

**DRYRUN Mode Behavior**:
- **Loads**: Historical bars from CSV files (`data/raw/{instrument}/1m/{yyyy}/{MM}/{yyyy-MM-dd}.csv`)
- **Buffers**: Bars in `_barBuffer`
- **Transitions**: To `ARMED` when CSV loading complete
- **Logs**: `PRE_HYDRATION_COMPLETE`

### State: `ARMED`

**Behavior**:
- **Waits**: For current time to reach `RangeStartChicagoTime`
- **Buffers**: All incoming bars (live and historical)
- **Transitions**: To `RANGE_BUILDING` when `nowChicago >= RangeStartChicagoTime`

### State: `RANGE_BUILDING`

**Behavior**:
- **Continues**: Buffering bars until `SlotTimeChicagoTime`
- **Computes**: Range incrementally (updates high/low as bars arrive)
- **Transitions**: To `RANGE_LOCKED` when `nowChicago >= SlotTimeChicagoTime`

**Range Computation**:
- **Filters**: Bars by trading date (must match `TradingDate`)
- **Filters**: Bars within range window (`RangeStartChicagoTime` to `SlotTimeChicagoTime`)
- **Computes**: `RangeHigh` and `RangeLow` from filtered bars
- **Logs**: `RANGE_COMPUTE_COMPLETE` with:
  - Range high/low
  - Bar counts (historical, live, deduped)
  - Expected vs actual bar count (informational only)

### State: `RANGE_LOCKED`

**Behavior**:
- **Locks**: Range values (immutable)
- **Computes**: Breakout levels (long/short)
- **Evaluates**: Entry conditions
- **Places**: Orders (if conditions met)
- **Transitions**: To `DONE` at market close

---

## Phase 6: Live Bar Processing

### When NinjaTrader Emits a Bar
```csharp
// RobotSimStrategy.OnBarUpdate()
_engine.OnBar(barUtc, instrument, open, high, low, close, nowUtc)
```

**RobotEngine.OnBar()**:
1. **Validates**: Trading date is locked
2. **Extracts**: Bar Chicago date
3. **Validates**: Bar date matches locked trading date
4. **Rejects**: Partial bars (< 1 minute old)
5. **Routes**: Bar to matching streams
6. **Logs**: `BAR_ACCEPTED` (rate-limited, once per minute per instrument)

**StreamStateMachine.OnBar()**:
1. **Validates**: Bar trading date matches stream trading date
2. **Rejects**: Partial bars (< 1 minute old)
3. **Buffers**: Bar in `_barBuffer` (if state allows)
4. **Deduplicates**: By timestamp with precedence (LIVE > BARSREQUEST > CSV)
5. **Tracks**: Bar source (`BarSource.LIVE`)

---

## Summary: Complete Startup Sequence

```
1. NinjaTrader Strategy Enabled
   └─> State.DataLoaded
       ├─> Verify SIM Account ✓
       ├─> Create RobotEngine
       ├─> Set Account Info
       └─> engine.Start()
           ├─> Load Spec ✓
           ├─> Create Execution Adapter ✓
           ├─> Load Timetable ✓
           ├─> Lock Trading Date (from timetable) ✓
           ├─> Create Streams ✓
           │   └─> StreamStateMachine instances (state: PRE_HYDRATION)
           └─> Emit Startup Banner ✓
       
2. Request Historical Bars
   └─> RequestHistoricalBarsForPreHydration()
       ├─> Get Session Info from Spec ✓
       ├─> Request Bars from NinjaTrader ✓
       ├─> Filter Future/Partial Bars ✓
       └─> Load Bars into Streams ✓
           └─> Bars buffered in _barBuffer (BarSource.BARSREQUEST)

3. Enter Realtime
   └─> State.Realtime
       └─> Start Tick Timer
           └─> engine.Tick() every second
               └─> stream.Tick() for each stream
                   └─> State transitions based on time/bars

4. Stream State Progression
   PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE
   
5. Live Bar Processing
   └─> OnBarUpdate() → engine.OnBar() → stream.OnBar()
       └─> Bars buffered, deduplicated, range computed
```

---

## Key Invariants

1. **Trading Date**: Locked once from timetable, never changes
2. **Streams**: Created only after trading date is locked
3. **Pre-Hydration**: Runs after streams exist, fills missing bars for locked trading day
4. **Bar Deduplication**: Centralized precedence (LIVE > BARSREQUEST > CSV)
5. **Bar Count Mismatch**: Informational only, not an error
6. **Range Windows**: Immutable for life of stream
7. **Partial Bars**: Rejected at multiple layers (< 1 minute old)

---

## Log Events to Watch

**Startup**:
- `ENGINE_START`
- `SPEC_LOADED`
- `TRADING_DATE_LOCKED` (source: TIMETABLE)
- `STREAM_CREATED`
- `OPERATOR_BANNER`
- `PRE_HYDRATION_BARS_LOADED`

**State Transitions**:
- `PRE_HYDRATION_COMPLETE_SIM`
- `STREAM_ARMED`
- `RANGE_COMPUTE_START`
- `RANGE_COMPUTE_COMPLETE`
- `RANGE_LOCKED`

**Bar Processing**:
- `BAR_ACCEPTED` (rate-limited)
- `BAR_DUPLICATE_REPLACED` (if deduplication occurs)
- `BAR_PARTIAL_REJECTED` (if partial bar detected)
