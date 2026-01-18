# BarsRequest Process Walkthrough (SIM Mode)

## Complete Flow: From Strategy Startup to Range Computation

This document walks through the complete BarsRequest process as SIM mode executes it, step-by-step.

---

## Phase 1: Strategy Initialization

### Step 1.1: Strategy Starts in NinjaTrader
**Location**: `RobotSimStrategy.cs:OnStateChange(State.DataLoaded)`

```
1. NinjaTrader loads the strategy
2. Strategy verifies SIM account (must be Sim account)
3. Creates RobotEngine in SIM mode:
   _engine = new RobotEngine(projectRoot, TimeSpan.FromSeconds(2), ExecutionMode.SIM, ...)
4. Sets account info: _engine.SetAccountInfo(accountName, "SIM")
5. Calls: _engine.Start()
```

**What happens in `engine.Start()`:**
- Loads parity spec
- Loads timetable
- Locks trading date from timetable
- Creates streams (ES1, ES2, etc.)
- Streams start in `PRE_HYDRATION` state

**Critical Check:**
```csharp
var tradingDateStr = _engine.GetTradingDate();
if (string.IsNullOrEmpty(tradingDateStr))
{
    throw new InvalidOperationException("Trading date not locked!");
}
```

---

## Phase 2: BarsRequest Setup

### Step 2.1: Get Session Info from Spec
**Location**: `RobotSimStrategy.cs:RequestHistoricalBarsForPreHydration()`

```csharp
// Get instrument name (e.g., "ES")
var instrumentName = Instrument.MasterInstrument.Name.ToUpperInvariant();
var sessionName = "S1"; // Default session

// CRITICAL: Get range start and slot time from engine spec (not hardcoded!)
var sessionInfo = _engine.GetSessionInfo(instrumentName, sessionName);
```

**Example Result:**
```csharp
sessionInfo = (
    rangeStartTime: "07:30",  // From spec.sessions["S1"].range_start_time
    slotEndTimes: ["09:00", "10:00"]  // From spec.sessions["S1"].slot_end_times
)
```

**Why this matters:**
- Avoids hardcoded values
- Ensures alignment with spec
- Supports multiple sessions/instruments

### Step 2.2: Calculate Time Range
**Location**: `RobotSimStrategy.cs:RequestHistoricalBarsForPreHydration()`

```csharp
var tradingDate = DateOnly.Parse("2026-01-16");
var rangeStartChicago = "07:30";
var slotTimeChicago = "09:00";  // First slot from spec

var timeService = new TimeService("America/Chicago");
var nowUtc = DateTimeOffset.UtcNow;
var nowChicago = timeService.ConvertUtcToChicago(nowUtc);
var slotTimeChicagoTime = timeService.ConstructChicagoTime(tradingDate, slotTimeChicago);

// CRITICAL: Only request bars up to "now" to avoid future bars
var endTimeChicago = nowChicago < slotTimeChicagoTime
    ? nowChicago.ToString("HH:mm")  // e.g., "08:45" if started at 08:45
    : slotTimeChicago;              // e.g., "09:00" if started after slot time
```

**Example Scenarios:**

**Scenario A: Started before slot time (08:45)**
```
rangeStartChicago = "07:30"
endTimeChicago = "08:45"  // Current time
Request: 07:30 to 08:45
```

**Scenario B: Started after slot time (09:15)**
```
rangeStartChicago = "07:30"
endTimeChicago = "09:00"  // Slot time (not current time!)
Request: 07:30 to 09:00
Note: "RESTART_POLICY: Restarting after slot time - BarsRequest limited to slot_time"
```

**Why limit to slot time:**
- Prevents loading bars beyond range window
- Ensures deterministic range input set
- Matches restart policy: "Restart = Full Reconstruction"

---

## Phase 3: NinjaTrader BarsRequest API Call

### Step 3.1: Create BarsRequest
**Location**: `NinjaTraderBarRequest.cs:RequestHistoricalBars()`

```csharp
// Convert Chicago times to UTC
var rangeStartUtc = rangeStartChicagoTime.ToUniversalTime();
var slotTimeUtc = slotTimeChicagoTime.ToUniversalTime();

// Create BarsRequest
var barsRequest = new BarsRequest(instrument)
{
    BarsPeriod = new BarsPeriod { 
        BarsPeriodType = BarsPeriodType.Minute, 
        Value = 1  // 1-minute bars
    },
    StartTime = rangeStartUtc.DateTime,  // e.g., 2026-01-16 13:30:00 UTC
    EndTime = slotTimeUtc.DateTime,       // e.g., 2026-01-16 15:00:00 UTC
    TradingHours = instrument.MasterInstrument.TradingHours
};

// Request bars synchronously
var barsSeries = barsRequest.Request();
```

**What NinjaTrader does:**
- Queries historical data database
- Filters by trading hours
- Returns bars in chronological order
- May return null if no data available

### Step 3.2: Convert NinjaTrader Bars to Robot Bars
**Location**: `NinjaTraderBarRequest.cs:RequestHistoricalBars()`

```csharp
for (int i = 0; i < barsSeries.Count; i++)
{
    var ntBar = barsSeries.Get(i);
    
    // Convert exchange time (Chicago) to UTC
    var barExchangeTime = ntBar.Time;  // e.g., 2026-01-16 08:00:00 (Chicago, Unspecified)
    var barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
    
    // Create Robot.Core.Bar struct
    var bar = new Bar(
        timestampUtc: barUtc,           // e.g., 2026-01-16 14:00:00 UTC
        open: (decimal)ntBar.Open,       // e.g., 7000.00
        high: (decimal)ntBar.High,       // e.g., 7005.00
        low: (decimal)ntBar.Low,        // e.g., 6995.00
        close: (decimal)ntBar.Close,     // e.g., 7002.00
        volume: (decimal?)ntBar.Volume   // e.g., 1000
    );
    
    bars.Add(bar);
}
```

**Result**: List of `Bar` structs, one per minute from range start to end time

**Example Output:**
```
Bar[0]: 2026-01-16 13:30:00 UTC, O:7000, H:7005, L:6995, C:7002
Bar[1]: 2026-01-16 13:31:00 UTC, O:7002, H:7007, L:6997, C:7004
Bar[2]: 2026-01-16 13:32:00 UTC, O:7004, H:7009, L:6999, C:7006
...
Bar[90]: 2026-01-16 15:00:00 UTC, O:7020, H:7025, L:7015, C:7022
Total: 91 bars (07:30 to 09:00 Chicago time)
```

---

## Phase 4: Engine Receives Bars

### Step 4.1: Load Bars into Engine
**Location**: `RobotSimStrategy.cs:RequestHistoricalBarsForPreHydration()`

```csharp
// Feed bars to engine
_engine.LoadPreHydrationBars(Instrument.MasterInstrument.Name, bars, DateTimeOffset.UtcNow);
Log($"Loaded {bars.Count} historical bars from NinjaTrader for pre-hydration");
```

**What happens next:**
- Engine receives list of bars
- Filters bars (future/partial removal)
- Feeds bars to matching streams

### Step 4.2: Filter Bars
**Location**: `RobotEngine.cs:LoadPreHydrationBars()`

```csharp
var filteredBars = new List<Bar>();
var barsFilteredFuture = 0;
var barsFilteredPartial = 0;
const double MIN_BAR_AGE_MINUTES = 1.0;

foreach (var bar in bars)
{
    // Filter 1: Reject future bars
    if (bar.TimestampUtc > utcNow)
    {
        barsFilteredFuture++;
        continue;  // Skip this bar
    }
    
    // Filter 2: Reject partial/in-progress bars (must be at least 1 minute old)
    var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
    if (barAgeMinutes < MIN_BAR_AGE_MINUTES)
    {
        barsFilteredPartial++;
        continue;  // Skip this bar
    }
    
    // Bar passed filters - add to filtered list
    filteredBars.Add(bar);
}
```

**Example Filtering:**

**Input**: 91 bars from BarsRequest
**Current Time**: 2026-01-16 15:00:30 UTC (08:00:30 Chicago)

**Filtering:**
- Bar at 15:00:00 UTC (08:00:00 Chicago): ✅ Pass (30 seconds old, but > 1 min threshold)
- Bar at 15:00:30 UTC (08:00:30 Chicago): ❌ Filtered (partial, < 1 min old)
- Bar at 15:01:00 UTC (08:01:00 Chicago): ❌ Filtered (future bar)

**Output**: 90 bars (last bar filtered as partial)

**Why filter:**
- Prevents duplicate bars when live feed arrives
- Ensures only fully closed bars used
- Avoids off-by-one minute errors

### Step 4.3: Feed Bars to Streams
**Location**: `RobotEngine.cs:LoadPreHydrationBars()`

```csharp
foreach (var stream in _streams.Values)
{
    if (stream.IsSameInstrument(instrument))  // e.g., "ES"
    {
        // Verify stream is ready to receive bars
        if (stream.State != StreamState.PRE_HYDRATION && 
            stream.State != StreamState.ARMED && 
            stream.State != StreamState.RANGE_BUILDING)
        {
            // Stream not ready - skip
            continue;
        }
        
        // Feed bars to stream
        foreach (var bar in filteredBars)
        {
            stream.OnBar(
                bar.TimestampUtc, 
                bar.Open, 
                bar.High, 
                bar.Low, 
                bar.Close, 
                utcNow, 
                isHistorical: true  // ← Marks as BarsRequest source
            );
        }
        streamsFed++;
    }
}
```

**What happens in `stream.OnBar()`:**
- Bar is added to stream's buffer
- Marked as `BarSource.BARSREQUEST` (precedence: LIVE > BARSREQUEST > CSV)
- Deduplicated if same timestamp exists (BARSREQUEST wins over CSV)
- Sorted chronologically

---

## Phase 5: Stream Buffers Bars

### Step 5.1: Bar Added to Buffer
**Location**: `StreamStateMachine.cs:OnBar()`

```csharp
// Buffer bars when in PRE_HYDRATION, ARMED, or RANGE_BUILDING state
if (State == StreamState.PRE_HYDRATION || State == StreamState.ARMED || State == StreamState.RANGE_BUILDING)
{
    // Convert UTC to Chicago time
    var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
    
    // Only buffer bars in range window [range_start, slot_time)
    if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
    {
        // Add bar to buffer with source tracking
        AddBarToBuffer(bar, BarSource.BARSREQUEST);
    }
}
```

**What `AddBarToBuffer()` does:**
```csharp
lock (_barBufferLock)
{
    // Check if bar already exists (deduplication)
    if (_barSourceMap.TryGetValue(barUtc, out var existingSource))
    {
        // Bar exists - check precedence
        if (BarSource.BARSREQUEST > existingSource)  // BARSREQUEST > CSV
        {
            // Replace with higher precedence source
            _barBuffer.RemoveAll(b => b.TimestampUtc == barUtc);
            _barBuffer.Add(bar);
            _barSourceMap[barUtc] = BarSource.BARSREQUEST;
            _dedupedBarCount++;
        }
        // Otherwise, keep existing bar (higher precedence)
    }
    else
    {
        // New bar - add to buffer
        _barBuffer.Add(bar);
        _barSourceMap[barUtc] = BarSource.BARSREQUEST;
        _historicalBarCount++;  // Track as historical bar
    }
    
    // Sort buffer chronologically
    _barBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
}
```

**Result**: Stream's `_barBuffer` now contains bars from BarsRequest

**Example Buffer State:**
```
_barBuffer = [
    Bar(2026-01-16 13:30:00 UTC, O:7000, H:7005, L:6995, C:7002),
    Bar(2026-01-16 13:31:00 UTC, O:7002, H:7007, L:6997, C:7004),
    ...
    Bar(2026-01-16 14:59:00 UTC, O:7020, H:7025, L:7015, C:7022)
]
_barSourceMap = {
    2026-01-16 13:30:00 UTC: BARSREQUEST,
    2026-01-16 13:31:00 UTC: BARSREQUEST,
    ...
}
```

---

## Phase 6: Stream Transitions to ARMED

### Step 6.1: Pre-Hydration State Check
**Location**: `StreamStateMachine.cs:HandlePreHydrationState()`

```csharp
// SIM mode: Skip CSV files, rely solely on BarsRequest
if (IsSimMode())
{
    // Mark pre-hydration complete immediately (no CSV to load)
    _preHydrationComplete = true;
}

// After pre-hydration setup completes
if (_preHydrationComplete)
{
    if (IsSimMode())
    {
        // Check if we have bars or if past range start time
        int barCount = GetBarBufferCount();  // e.g., 90 bars
        var nowChicago = _time.ConvertUtcToChicago(utcNow);  // e.g., 08:00:30
        
        // Transition condition
        if (barCount > 0 || nowChicago >= RangeStartChicagoTime)
        {
            // ✅ Transition to ARMED!
            Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM");
        }
        // Otherwise, wait for more bars (they'll be buffered in OnBar)
    }
}
```

**Transition Conditions:**
- **Condition 1**: `barCount > 0` → Has bars from BarsRequest ✅
- **Condition 2**: `nowChicago >= RangeStartChicagoTime` → Past range start time (time threshold)

**In our example:**
- `barCount = 90` ✅ (has bars)
- `nowChicago = 08:00:30` ✅ (past 07:30 range start)
- **Result**: Transition to ARMED immediately

### Step 6.2: Log Hydration Summary
**Location**: `StreamStateMachine.cs:HandlePreHydrationState()`

```csharp
_log.Write(RobotEvents.Base(..., "HYDRATION_SUMMARY", "PRE_HYDRATION",
    new
    {
        instrument = "ES",
        slot = "ES1",
        trading_date = "2026-01-16",
        total_bars_in_buffer = 90,
        historical_bar_count = 90,      // BarsRequest bars
        live_bar_count = 0,             // No live bars yet
        deduped_bar_count = 0,          // No duplicates
        filtered_future_bar_count = 0,  // No future bars
        filtered_partial_bar_count = 1, // 1 partial bar filtered
        execution_mode = "SIM",
        note = "Consolidated hydration summary..."
    }));

LogHealth("INFO", "PRE_HYDRATION_COMPLETE", 
    "Pre-hydration complete (SIM mode) - 90 bars total (BarsRequest only)",
    note = "SIM mode uses BarsRequest only (no CSV files)"
);
```

**Log Output:**
```json
{
  "event_type": "HYDRATION_SUMMARY",
  "stream": "ES1",
  "instrument": "ES",
  "trading_date": "2026-01-16",
  "data": {
    "total_bars_in_buffer": 90,
    "historical_bar_count": 90,
    "live_bar_count": 0,
    "deduped_bar_count": 0,
    "filtered_future_bar_count": 0,
    "filtered_partial_bar_count": 1,
    "execution_mode": "SIM"
  }
}
```

---

## Phase 7: Range Computation (After ARMED)

### Step 7.1: Wait for Range Start Time
**Location**: `StreamStateMachine.cs:HandleArmedState()`

```csharp
// Stream is now in ARMED state
// Wait until current time >= RangeStartUtc

if (utcNow >= RangeStartUtc)  // e.g., 13:30 UTC (07:30 Chicago)
{
    // Transition to RANGE_BUILDING
    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
    
    // Compute initial range from buffered bars
    var initialRangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: utcNow);
    
    if (initialRangeResult.Success)
    {
        RangeHigh = initialRangeResult.RangeHigh;  // e.g., 7025.00
        RangeLow = initialRangeResult.RangeLow;    // e.g., 6995.00
        FreezeClose = initialRangeResult.FreezeClose;  // e.g., 7002.00
        _rangeComputed = true;
    }
}
```

**Range Computation:**
- Uses all bars in buffer (90 bars from BarsRequest)
- Computes: `range_high = max(bar.high)`, `range_low = min(bar.low)`
- Updates incrementally as live bars arrive
- Locks at slot time (09:00 Chicago)

---

## Complete Timeline Example

**Trading Date**: 2026-01-16  
**Range Start**: 07:30 Chicago  
**Slot Time**: 09:00 Chicago  
**Strategy Start**: 08:00:30 Chicago (13:00:30 UTC)

```
08:00:30  Strategy starts in NinjaTrader
          ├─ Engine.Start() → Trading date locked: 2026-01-16
          ├─ Streams created: ES1, ES2 (PRE_HYDRATION state)
          └─ RequestHistoricalBarsForPreHydration() called

08:00:31  BarsRequest setup
          ├─ Get session info: range_start="07:30", slot_time="09:00"
          ├─ Calculate end time: min(now=08:00, slot=09:00) = 08:00
          └─ Request bars: 07:30 to 08:00 Chicago (13:30 to 14:00 UTC)

08:00:32  NinjaTrader BarsRequest API
          ├─ Queries historical database
          ├─ Returns 31 bars (07:30 to 08:00)
          └─ Converts to Robot.Core.Bar format

08:00:33  Engine receives bars
          ├─ LoadPreHydrationBars("ES", 31 bars, utcNow)
          ├─ Filters: 0 future, 1 partial → 30 bars passed
          └─ Feeds to ES1, ES2 streams

08:00:34  Stream buffers bars
          ├─ ES1.OnBar() called 30 times
          ├─ Bars added to _barBuffer (BarSource.BARSREQUEST)
          └─ Buffer sorted chronologically

08:00:35  Stream state check (every tick)
          ├─ HandlePreHydrationState() called
          ├─ barCount = 30 > 0 ✅
          ├─ nowChicago = 08:00:35 >= 07:30 ✅
          └─ Transition to ARMED state

08:00:36  ARMED state
          ├─ Log HYDRATION_SUMMARY (30 bars, BarsRequest only)
          ├─ Wait for range start time (already past)
          └─ Transition to RANGE_BUILDING

08:00:37  RANGE_BUILDING state
          ├─ Compute initial range from 30 bars
          ├─ RangeHigh = 7005.00, RangeLow = 6995.00
          └─ Continue updating as live bars arrive...

09:00:00  Slot time reached
          ├─ Range locks permanently
          ├─ RangeHigh = 7025.00, RangeLow = 6995.00
          └─ Transition to RANGE_LOCKED
```

---

## Key Points

1. **SIM mode skips CSV files** - Only uses BarsRequest
2. **BarsRequest is synchronous** - Blocks until bars arrive
3. **Bars are filtered** - Future and partial bars removed
4. **Bars are deduplicated** - BARSREQUEST precedence over CSV
5. **Stream transitions when bars arrive** - Or time threshold reached
6. **Range computed from buffered bars** - Historical + live combined

---

## Error Scenarios

### Scenario 1: BarsRequest Returns No Bars
```
BarsRequest.Request() → null or empty
Result: Logs warning, continues with zero bars
Stream transitions to ARMED when time threshold reached
Range computation relies on live bars only (may be incomplete)
```

### Scenario 2: BarsRequest Fails
```
BarsRequest.Request() → throws exception
Result: Exception logged, strategy continues
Stream transitions to ARMED when time threshold reached
Range computation relies on live bars only
```

### Scenario 3: All Bars Filtered Out
```
BarsRequest returns 100 bars
All bars filtered (future or partial)
Result: Logs PRE_HYDRATION_NO_BARS_AFTER_FILTER
Stream transitions to ARMED when time threshold reached
Range computation relies on live bars only
```

---

## Summary

The BarsRequest process in SIM mode:
1. ✅ Gets session info from spec (not hardcoded)
2. ✅ Calculates time range (up to "now" or slot time)
3. ✅ Calls NinjaTrader BarsRequest API
4. ✅ Converts bars to Robot format
5. ✅ Filters future/partial bars
6. ✅ Feeds bars to streams
7. ✅ Streams buffer bars (BarSource.BARSREQUEST)
8. ✅ Stream transitions to ARMED when bars arrive
9. ✅ Range computed from buffered bars

**No CSV files involved** - Pure BarsRequest flow!
