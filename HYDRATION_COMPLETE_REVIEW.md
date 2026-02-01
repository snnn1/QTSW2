# Hydration Complete Review

## Overview

**Hydration** is the process of loading historical bars into the stream state machine before it can transition to `ARMED` and begin range building. This document provides a comprehensive review of the hydration system, covering all modes, edge cases, timing issues, and recovery mechanisms.

---

## 1. What is Hydration?

### 1.1 Definition

**Hydration** = Loading historical bars from `[range_start, slot_time)` into the bar buffer before the stream can begin range building.

**Purpose**: 
- Provide historical context for range computation
- Enable retrospective range calculation at slot time
- Support late-start scenarios (starting after slot_time)

**State**: Stream starts in `PRE_HYDRATION` state and must complete hydration before transitioning to `ARMED`.

### 1.2 Hydration Window

**Window Definition**:
```
[RangeStartChicagoTime, SlotTimeChicagoTime)
```

**Key Properties**:
- **Start**: Range start time (from TradingHours, default 17:00 CT previous day)
- **End**: Slot time (e.g., "07:30" CT) - **EXCLUSIVE**
- **Purpose**: Bars in this window are used for range computation

**Example**:
```
Range Start: 2025-02-01 17:00:00 CT (previous day)
Slot Time:   2025-02-01 07:30:00 CT
Window:      [2025-02-01 17:00:00, 2025-02-01 07:30:00)
Duration:    ~14.5 hours
Expected Bars: ~870 bars (1-minute bars)
```

---

## 2. Hydration Lifecycle

### 2.1 State Machine Flow

```
PRE_HYDRATION
    ↓ (hydration complete)
ARMED
    ↓ (range start time reached)
RANGE_BUILDING
    ↓ (slot_time reached)
RANGE_LOCKED
```

### 2.2 Key Flags

**`_preHydrationComplete`** (`StreamStateMachine.cs:146`):
- **Purpose**: Gate flag - must be `true` before transitioning to `ARMED`
- **Set When**:
  - DRYRUN: After CSV file loaded (or file missing)
  - SIM: After BarsRequest completes (or not pending)
- **Enforcement**: `HandleArmedState()` checks this flag (fail-closed if false)

**`State == PRE_HYDRATION`**:
- Initial state for all streams
- Stream remains in this state until hydration completes
- Transition to `ARMED` requires: `_preHydrationComplete == true`

---

## 3. Execution Mode Differences

### 3.1 DRYRUN Mode: File-Based Hydration

**Method**: `PerformPreHydration()` (`StreamStateMachine.cs:3531`)

**Process**:
1. **Resolve Project Root**: Use `ProjectRootResolver.ResolveProjectRoot()`
2. **Construct CSV Path**: `data/raw/{instrument}/1m/{yyyy}/{MM}/{INSTRUMENT}_1m_{yyyy-MM-dd}.csv`
   - Example: `data/raw/es/1m/2025/02/ES_1m_2025-02-01.csv`
3. **Read CSV File**: Line-by-line parsing
4. **Filter Bars**: Only bars in `[range_start, min(now, slot_time))`
5. **Add to Buffer**: Call `AddBarToBuffer()` with `BarSource.CSV`
6. **Mark Complete**: Set `_preHydrationComplete = true`

**CSV Format**:
```
timestamp_utc,open,high,low,close
2025-02-01T00:00:00Z,5000.00,5000.25,4999.75,5000.10
...
```

**Error Handling**:
- File not found → Log `PRE_HYDRATION_ZERO_BARS`, mark complete (allows progression)
- Parse errors → Skip malformed lines, continue
- Empty file → Log error, mark complete

**Key Code** (`StreamStateMachine.cs:3531-3750`):
```csharp
private void PerformPreHydration(DateTimeOffset utcNow)
{
    // Resolve project root
    var projectRoot = ProjectRootResolver.ResolveProjectRoot();
    
    // Construct CSV path
    var filePath = Path.Combine(projectRoot, "data", "raw", Instrument.ToLowerInvariant(), 
        "1m", year.ToString("0000"), month.ToString("00"), 
        $"{Instrument.ToUpperInvariant()}_1m_{year:0000}-{month:00}-{day:00}.csv");
    
    // Read CSV line-by-line
    using (var reader = new StreamReader(filePath))
    {
        // Skip header
        reader.ReadLine();
        
        // Parse each bar
        while ((line = reader.ReadLine()) != null)
        {
            // Parse timestamp_utc, open, high, low, close
            // Filter by hydration window
            // Add to buffer with BarSource.CSV
        }
    }
    
    _preHydrationComplete = true;
}
```

### 3.2 SIM Mode: BarsRequest-Based Hydration

**Process** (`StreamStateMachine.cs:1039-1101`):
1. **Check BarsRequest Status**: Query `_engine.IsBarsRequestPending()`
   - Checks both `CanonicalInstrument` and `ExecutionInstrument`
2. **Wait for Completion**: If pending, stay in `PRE_HYDRATION` and log `PRE_HYDRATION_WAITING_FOR_BARSREQUEST`
3. **Mark Complete**: When BarsRequest not pending, set `_preHydrationComplete = true`
4. **Bars Arrive**: Bars arrive via `OnBar()` callback with `isHistorical: true`
5. **Add to Buffer**: Bars added with `BarSource.BARSREQUEST`

**Critical Fix** (`StreamStateMachine.cs:1042-1076`):
```csharp
// CRITICAL FIX: Wait for BarsRequest to complete before marking pre-hydration complete
// This prevents range lock from happening before historical bars arrive
var isPending = _engine != null && (
    _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
    _engine.IsBarsRequestPending(ExecutionInstrument, utcNow)
);

if (isPending)
{
    // Stay in PRE_HYDRATION state until BarsRequest completes
    return;
}

// Mark pre-hydration as complete so we can transition when bars arrive
_preHydrationComplete = true;
```

**Why This Matters**: Without this fix, range lock could occur before historical bars arrive, causing incorrect range computation.

### 3.3 LIVE Mode

**Status**: Not yet implemented (same as SIM mode expected)

**Expected Behavior**: Similar to SIM mode, using live data feed for historical bars.

---

## 4. Bar Sources & Deduplication

### 4.1 Bar Source Enum

**Definition** (`StreamStateMachine.cs:25-41`):
```csharp
public enum BarSource
{
    LIVE = 0,        // Live feed bars (OnBar callback) - highest precedence
    BARSREQUEST = 1, // Historical bars from NinjaTrader API - medium precedence
    CSV = 2          // File-based pre-hydration - lowest precedence
}
```

**Precedence Order**: `LIVE > BARSREQUEST > CSV`

### 4.2 Deduplication Logic

**Location**: `AddBarToBuffer()` (`StreamStateMachine.cs:2400-2600`)

**Process**:
1. **Check Existing**: Lookup bar timestamp in `_barSourceMap`
2. **Precedence Check**: If exists, compare source precedence
3. **Replace or Skip**:
   - If new source has higher precedence → Replace existing bar
   - If new source has lower precedence → Skip new bar
   - If same precedence → Skip (idempotent)

**Example**:
```
Bar at 2025-02-01 07:00:00 CT:
- CSV bar arrives first → Added to buffer, source = CSV
- BARSREQUEST bar arrives → Replaces CSV bar (BARSREQUEST > CSV)
- LIVE bar arrives → Replaces BARSREQUEST bar (LIVE > BARSREQUEST)
```

**Tracking** (`StreamStateMachine.cs:135-139`):
```csharp
private int _historicalBarCount = 0;      // Bars from BarsRequest/pre-hydration
private int _liveBarCount = 0;            // Bars from live feed (OnBar)
private int _dedupedBarCount = 0;         // Bars deduplicated (replaced existing)
private int _filteredFutureBarCount = 0;  // Bars filtered (future)
private int _filteredPartialBarCount = 0;  // Bars filtered (partial/in-progress)
```

### 4.3 Bar Filtering

**Future Bars**: Filtered out (bar timestamp > now)
**Partial Bars**: Filtered out (bar timestamp too close to now)
**Date Mismatch**: Filtered out (bar trading date != stream trading date)

**Filtering Logic** (`StreamStateMachine.cs:2400-2600`):
```csharp
// Filter future bars
if (barUtc > utcNow.AddSeconds(5))
{
    _filteredFutureBarCount++;
    return; // Skip future bars
}

// Filter partial bars (too close to now)
if ((utcNow - barUtc).TotalSeconds < 60)
{
    _filteredPartialBarCount++;
    return; // Skip partial bars
}

// Filter by trading date
var barTradingDate = _time.GetChicagoDateToday(barUtc).ToString("yyyy-MM-dd");
if (barTradingDate != TradingDate)
{
    return; // Skip bars from wrong trading date
}
```

---

## 5. Transition Conditions

### 5.1 PRE_HYDRATION → ARMED

**Conditions** (`StreamStateMachine.cs:1412`):
```csharp
if (barCount > 0 || nowChicago >= RangeStartChicagoTime || shouldForceTransition)
{
    // Transition to ARMED
}
```

**Three Paths**:
1. **Normal**: `barCount > 0` AND `nowChicago >= RangeStartChicagoTime`
2. **Timeout**: `nowChicago >= RangeStartChicagoTime` (even with 0 bars)
3. **Hard Timeout**: `shouldForceTransition == true` (RangeStartChicagoTime + 1 minute)

### 5.2 Hard Timeout (Liveness Guarantee)

**Purpose**: Prevent streams from getting stuck in `PRE_HYDRATION` forever.

**Implementation** (`StreamStateMachine.cs:1295-1342`):
```csharp
// HARD TIMEOUT: Liveness guarantee - PRE_HYDRATION must exit no later than RangeStartChicagoTime + 1 minute
var hardTimeoutChicago = RangeStartChicagoTime.AddMinutes(1.0);
var shouldForceTransition = nowChicago >= hardTimeoutChicago;
var minutesPastRangeStart = (nowChicago - RangeStartChicagoTime).TotalMinutes;
```

**Behavior**:
- If `nowChicago >= RangeStartChicagoTime + 1 minute` → Force transition to `ARMED`
- Log `PRE_HYDRATION_FORCED_TRANSITION` event
- Stream proceeds even with 0 bars (will fail later if needed)

**Why Needed**: Prevents infinite wait if:
- CSV file missing
- BarsRequest never completes
- Data feed issues

### 5.3 Transition Logging

**HYDRATION_SUMMARY Event** (`StreamStateMachine.cs:1553-1592`):
- Logged once per stream per day at `PRE_HYDRATION → ARMED` transition
- Captures:
  - Bar counts by source (historical, live, deduped, filtered)
  - Completeness metrics (expected vs loaded bars)
  - Late-start handling (missed breakout detection)
  - Reconstructed range (if available)

**Example Log**:
```json
{
  "event_type": "HYDRATION_SUMMARY",
  "stream_id": "ES1",
  "total_bars_in_buffer": 850,
  "historical_bar_count": 800,
  "live_bar_count": 50,
  "deduped_bar_count": 10,
  "filtered_future_bar_count": 5,
  "filtered_partial_bar_count": 2,
  "expected_bars": 870,
  "completeness_pct": 97.7,
  "late_start": false,
  "missed_breakout": false,
  "reconstructed_range_high": 5000.25,
  "reconstructed_range_low": 4999.75
}
```

---

## 6. Late-Start Handling

### 6.1 Late-Start Detection

**Definition**: Starting after `slot_time` (e.g., starting at 08:00 CT when slot_time is 07:30 CT).

**Detection** (`StreamStateMachine.cs:1471`):
```csharp
bool isLateStart = nowChicago > SlotTimeChicagoTime;
```

### 6.2 Missed Breakout Detection

**Purpose**: If starting after slot_time, check if breakout already occurred.

**Method**: `CheckMissedBreakout()` (`StreamStateMachine.cs:903-976`)

**Process**:
1. **Compute Range**: Retrospectively compute range from bars `[range_start, slot_time)`
2. **Scan Post-Slot Bars**: Check bars in `[slot_time, now]` for breakout
3. **Breakout Detection**: 
   - `bar.High > rangeHigh` → Long breakout
   - `bar.Low < rangeLow` → Short breakout
4. **Strict Inequalities**: Uses `>` and `<` (not `>=` or `<=`)

**Boundary Semantics** (`StreamStateMachine.cs:924-930`):
```csharp
// Missed-breakout scan window: [slot_time, now] (inclusive on both ends)
if (barChicagoTime < slotChicago)
    continue; // Skip bars before slot_time (these are in range build window)

if (barChicagoTime > nowChicago)
    break; // Past current time, stop checking (no lookahead)
```

**Breakout Detection** (`StreamStateMachine.cs:959-972`):
```csharp
// CRITICAL: Use STRICT inequalities for breakout detection
// bar.High > rangeHigh (not >=) - price must exceed range high
// bar.Low < rangeLow (not <=) - price must exceed range low
// Price equals range boundary is NOT a breakout
if (bar.High > rangeHigh)
{
    return (true, bar.TimestampUtc, barChicagoTime, bar.High, "LONG");
}
else if (bar.Low < rangeLow)
{
    return (true, bar.TimestampUtc, barChicagoTime, bar.Low, "SHORT");
}
```

### 6.3 Late-Start Response

**If Missed Breakout Detected** (`StreamStateMachine.cs:1594-1612`):
1. Log `LATE_START_MISSED_BREAKOUT` health event
2. Commit stream as `NO_TRADE_LATE_START_MISSED_BREAKOUT`
3. Block trading (cannot trade after breakout occurred)

**If No Missed Breakout**:
- Proceed normally
- Range computed retrospectively
- Stream transitions to `ARMED` → `RANGE_BUILDING`

---

## 7. Range Reconstruction

### 7.1 Retrospective Range Computation

**Method**: `ComputeRangeRetrospectively()` (`StreamStateMachine.cs:3790-4200`)

**Purpose**: Compute range from bars in buffer at any point in time.

**Process**:
1. **Get Bar Snapshot**: `GetBarBufferSnapshot()` (thread-safe copy)
2. **Filter by Trading Date**: Only bars matching `TradingDate`
3. **Filter by Time Window**: Only bars in `[RangeStartChicagoTime, endTimeUtc)`
4. **Compute Range**:
   - `RangeHigh = max(bar.High)`
   - `RangeLow = min(bar.Low)`
   - `FreezeClose = last bar.Close` (before endTime)

**Key Properties**:
- **End Time**: Defaults to `SlotTimeUtc`, but can be current time for hybrid init
- **Chicago Time Filtering**: All filtering uses Chicago time (not UTC)
- **Date Validation**: Bars must match trading date exactly

**Filtering Logic** (`StreamStateMachine.cs:3833-3903`):
```csharp
// CRITICAL: Filter by trading date first
var barTradingDate = _time.GetChicagoDateToday(barRawUtc).ToString("yyyy-MM-dd");
if (barTradingDate != expectedTradingDate)
{
    barsFilteredByDate++;
    continue; // Skip bars from wrong trading date
}

// Filter by time window: [RangeStartChicagoTime, endTimeChicagoActual)
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < endTimeChicagoActual)
{
    filteredBars.Add(bar);
    barsAccepted++;
}
```

### 7.2 Completeness Metrics

**Calculation** (`StreamStateMachine.cs:1507-1527`):
```csharp
var hydrationEndChicago = nowChicago < SlotTimeChicagoTime ? nowChicago : SlotTimeChicagoTime;
var rangeDurationMinutes = (hydrationEndChicago - RangeStartChicagoTime).TotalMinutes;
var fullRangeDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;

expectedBars = Math.Max(0, (int)Math.Floor(rangeDurationMinutes));
expectedFullRangeBars = Math.Max(0, (int)Math.Floor(fullRangeDurationMinutes));

completenessPct = expectedBars > 0 
    ? Math.Round((currentBarCount / (double)expectedBars) * 100.0, 2) 
    : 0.0;
```

**Metrics Logged**:
- `expected_bars`: Expected bars for hydration window
- `expected_full_range_bars`: Expected bars for full range window
- `loaded_bars`: Actual bars in buffer
- `completeness_pct`: Percentage of expected bars loaded

**Example**:
```
Range Start: 17:00 CT
Slot Time:   07:30 CT
Duration:    14.5 hours = 870 minutes
Expected Bars: 870
Loaded Bars: 850
Completeness: 97.7%
```

---

## 8. Hydration Event Persistence

### 8.1 HydrationEvent Class

**Location**: `modules/robot/core/HydrationEvent.cs`

**Structure**:
```csharp
public sealed class HydrationEvent
{
    public string event_type { get; }              // "STREAM_INITIALIZED", "PRE_HYDRATION_COMPLETE", etc.
    public string trading_day { get; }
    public string stream_id { get; }
    public string canonical_instrument { get; }
    public string execution_instrument { get; }
    public string session { get; }
    public string slot_time_chicago { get; }
    public string timestamp_utc { get; }
    public string timestamp_chicago { get; }
    public string state { get; }
    public Dictionary<string, object> data { get; } // Event-specific data
}
```

**Event Types**:
- `STREAM_INITIALIZED`: Stream created
- `PRE_HYDRATION_COMPLETE`: Hydration finished
- `ARMED`: Transitioned to ARMED state
- `RANGE_BUILDING_START`: Range building started
- `RANGE_LOCKED`: Range locked

### 8.2 HydrationEventPersister

**Location**: `modules/robot/core/HydrationEventPersister.cs`

**Properties**:
- **Singleton**: One instance per project root
- **Thread-Safe**: Per-file locks for concurrent writes
- **Fail-Safe**: Never throws exceptions (fail-safe design)

**File Format**: JSONL (one event per line)
**File Location**: `logs/robot/hydration_{tradingDay}.jsonl`

**Example**:
```jsonl
{"event_type":"STREAM_INITIALIZED","trading_day":"2025-02-01","stream_id":"ES1",...}
{"event_type":"PRE_HYDRATION_COMPLETE","trading_day":"2025-02-01","stream_id":"ES1",...}
{"event_type":"RANGE_LOCKED","trading_day":"2025-02-01","stream_id":"ES1",...}
```

### 8.3 Recovery from Hydration Logs

**Method**: `RestoreRangeLockedFromHydrationLog()` (`StreamStateMachine.cs:4498-4850`)

**Purpose**: Restore range lock state on restart by reading hydration logs.

**Process**:
1. **Find Log File**: Check `hydration_{tradingDay}.jsonl` first, fallback to `ranges_{tradingDay}.jsonl`
2. **Scan for RANGE_LOCKED Event**: Find most recent event matching `(tradingDay, streamId, slotTime)`
3. **Extract Range Data**: Parse `data` dictionary for `range_high`, `range_low`, `freeze_close`
4. **Restore State**: Set `RangeHigh`, `RangeLow`, `FreezeClose`, `_rangeLocked = true`

**Fallback Behavior**:
- If hydration log missing → Fall back to journal `LastState` check
- If restoration fails → Range will be recomputed on next tick

**Key Code** (`StreamStateMachine.cs:4538-4686`):
```csharp
// Read hydration/ranges log and find RANGE_LOCKED event for this stream
// IMPORTANT: Find the MOST RECENT event (last one in file), not the first
var lines = File.ReadAllLines(hydrationFile);

foreach (var line in lines)
{
    // Quick check: Does this line contain RANGE_LOCKED event for our stream?
    if (!line.Contains($"\"stream_id\":\"{streamId}\"") || 
        !line.Contains($"\"trading_day\":\"{tradingDay}\"") ||
        !line.Contains("RANGE_LOCKED"))
    {
        continue; // Skip lines that can't possibly match
    }
    
    // Parse and extract range data
    var dict = JsonUtil.Deserialize<Dictionary<string, object>>(line);
    if (dict.TryGetValue("data", out var dataObj))
    {
        // Extract range_high, range_low, freeze_close from data dictionary
        latestHydrationData = dataDict;
    }
}
```

---

## 9. Edge Cases & Timing Issues

### 9.1 BarsRequest Never Completes

**Scenario**: SIM mode, BarsRequest initiated but never completes (timeout, API error, etc.)

**Handling**:
- **Hard Timeout**: After `RangeStartChicagoTime + 1 minute`, force transition
- **Log**: `PRE_HYDRATION_FORCED_TRANSITION` with reason
- **Result**: Stream proceeds with 0 bars (will fail range lock later)

**Code** (`StreamStateMachine.cs:1295-1342`):
```csharp
// HARD TIMEOUT: Liveness guarantee
var hardTimeoutChicago = RangeStartChicagoTime.AddMinutes(1.0);
if (nowChicago >= hardTimeoutChicago)
{
    shouldForceTransition = true;
    forceTransitionReason = "PRE_HYDRATION_HARD_TIMEOUT";
}
```

### 9.2 CSV File Missing

**Scenario**: DRYRUN mode, CSV file doesn't exist for trading date

**Handling**:
- **Log**: `PRE_HYDRATION_ZERO_BARS` (ERROR if past range start, WARN if before)
- **Mark Complete**: Set `_preHydrationComplete = true` (allows progression)
- **Result**: Stream proceeds with 0 bars (will fail range lock later)

**Code** (`StreamStateMachine.cs:3599-3615`):
```csharp
if (!File.Exists(filePath))
{
    var logLevel = nowChicago >= RangeStartChicagoTime ? "ERROR" : "WARN";
    LogHealth(logLevel, "PRE_HYDRATION_ZERO_BARS", "Pre-hydration file not found - zero bars loaded", ...);
    _preHydrationComplete = true;
    return;
}
```

### 9.3 Starting Before Range Start

**Scenario**: Stream starts before `RangeStartChicagoTime` (e.g., 16:00 CT when range start is 17:00 CT)

**Handling**:
- **Wait**: Stream stays in `PRE_HYDRATION` until `nowChicago >= RangeStartChicagoTime`
- **Hydration**: Can load bars, but won't transition until range start time
- **Log**: `PRE_HYDRATION_HANDLER_TRACE` every 5 minutes

**Transition Condition** (`StreamStateMachine.cs:1412`):
```csharp
if (barCount > 0 || nowChicago >= RangeStartChicagoTime || shouldForceTransition)
{
    // Transition to ARMED
}
```

### 9.4 Starting After Slot Time (Late Start)

**Scenario**: Stream starts after `slot_time` (e.g., 08:00 CT when slot_time is 07:30 CT)

**Handling**:
1. **Compute Range**: Retrospectively compute range from `[range_start, slot_time)`
2. **Check Missed Breakout**: Scan bars `[slot_time, now]` for breakout
3. **If Missed Breakout**: Commit as `NO_TRADE_LATE_START_MISSED_BREAKOUT`
4. **If No Missed Breakout**: Proceed normally

**Code** (`StreamStateMachine.cs:1471-1493`):
```csharp
bool isLateStart = nowChicago > SlotTimeChicagoTime;

if (isLateStart)
{
    // Compute range from [range_start, slot_time)
    var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
    
    // Check if breakout already occurred
    var missedBreakoutResult = CheckMissedBreakout(utcNow, rangeHigh, rangeLow);
    missedBreakout = missedBreakoutResult.MissedBreakout;
}
```

### 9.5 Bars Arrive Out of Order

**Scenario**: Bars arrive with timestamps out of chronological order

**Handling**:
- **Deduplication**: `AddBarToBuffer()` handles duplicates by timestamp
- **Sorting**: `ComputeRangeRetrospectively()` sorts bars before processing
- **Missed Breakout**: `CheckMissedBreakout()` sorts bars before scanning

**Code** (`StreamStateMachine.cs:917-918`):
```csharp
// CRITICAL: Get snapshot and explicitly sort by timestamp
var barsSnapshot = GetBarBufferSnapshot();
barsSnapshot.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
```

### 9.6 Trading Date Mismatch

**Scenario**: Bars arrive with wrong trading date (e.g., previous day's bars)

**Handling**:
- **Filter**: `ComputeRangeRetrospectively()` filters by trading date
- **Log**: `RANGE_COMPUTE_BAR_FILTERING` event with `bars_filtered_by_date` count
- **Result**: Wrong-date bars excluded from range computation

**Code** (`StreamStateMachine.cs:3833-3847`):
```csharp
// CRITICAL: Filter by trading date first
var barTradingDate = _time.GetChicagoDateToday(barRawUtc).ToString("yyyy-MM-dd");
if (barTradingDate != expectedTradingDate)
{
    barsFilteredByDate++;
    continue; // Skip bars from wrong trading date
}
```

---

## 10. Bar Buffer Management

### 10.1 Thread-Safe Buffer

**Implementation**: `List<Bar>` with `_barBufferLock` mutex

**Operations**:
- **Add**: `AddBarToBuffer()` - thread-safe append
- **Read**: `GetBarBufferCount()` - thread-safe count
- **Snapshot**: `GetBarBufferSnapshot()` - thread-safe copy

**Code** (`StreamStateMachine.cs:115, 116`):
```csharp
private readonly List<Bar> _barBuffer = new();
private readonly object _barBufferLock = new();
```

### 10.2 Buffer Lifecycle

**Initialization**: Empty buffer at stream creation
**Hydration**: Bars added during `PRE_HYDRATION` state
**Range Building**: Additional bars added during `RANGE_BUILDING` state
**Range Lock**: Buffer frozen (read-only) after range lock
**Reset**: Buffer cleared on trading date rollover

**Reset Logic** (`StreamStateMachine.cs:699-700`):
```csharp
// Clear bar buffer on reset
lock (_barBufferLock)
{
    _barBuffer.Clear();
    _barSourceMap.Clear();
    // Reset counters
}
```

---

## 11. Gap Detection & Tolerance

### 11.1 Gap Tracking

**Purpose**: Detect missing bars (data feed failures vs low liquidity)

**Tracking** (`StreamStateMachine.cs:148-152`):
```csharp
private double _largestSingleGapMinutes = 0.0;
private double _totalGapMinutes = 0.0;
private DateTimeOffset? _lastBarOpenChicago = null;
```

**Gap Calculation** (`StreamStateMachine.cs:2569-2611`):
```csharp
if (_lastBarOpenChicago.HasValue)
{
    var gapDeltaMinutes = (barChicagoTime - _lastBarOpenChicago.Value).TotalMinutes;
    
    // Only track gaps > 1 minute (normal 1-minute bars have ~1 minute gaps)
    if (gapDeltaMinutes > 1.0)
    {
        var missingMinutes = gapDeltaMinutes - 1.0;
        
        if (missingMinutes > _largestSingleGapMinutes)
            _largestSingleGapMinutes = missingMinutes;
        
        _totalGapMinutes += missingMinutes;
    }
}
```

### 11.2 Gap Classification

**DATA_FEED_FAILURE**:
- Gaps during `PRE_HYDRATION` (BARSREQUEST should return complete data)
- Gaps from `BARSREQUEST` source (historical data should be complete)
- Very low bar count overall

**LOW_LIQUIDITY**:
- Gaps during `RANGE_BUILDING` from `LIVE` feed (market genuinely sparse)
- Reasonable bar count but with gaps (some trading occurred)

**Classification Logic** (`StreamStateMachine.cs:2613-2631`):
```csharp
var isDataFeedFailure = 
    State == StreamState.PRE_HYDRATION || // PRE_HYDRATION gaps = data feed issue
    barSource == BarSource.BARSREQUEST || // Historical gaps = data feed issue
    totalBarCount < expectedMinBars;      // Very low bar count = data feed issue

var gapType = isDataFeedFailure ? "DATA_FEED_FAILURE" : "LOW_LIQUIDITY";
```

### 11.3 Gap Tolerance (Currently Disabled)

**Previous Rules** (now disabled):
- `MAX_SINGLE_GAP_MINUTES = 3.0`
- `MAX_TOTAL_GAP_MINUTES = 6.0`
- `MAX_GAP_LAST_10_MINUTES = 2.0`

**Current Behavior**: All gaps are tolerated and logged (never invalidate range)

**Code** (`StreamStateMachine.cs:2633-2664`):
```csharp
// TEMPORARILY DISABLED: DATA_FEED_FAILURE gap invalidation
// Previously, DATA_FEED_FAILURE gaps would invalidate ranges, but this is now disabled
// All gaps (both DATA_FEED_FAILURE and LOW_LIQUIDITY) are tolerated and logged for monitoring
```

---

## 12. Diagnostic Logging

### 12.1 Rate-Limited Diagnostics

**PRE_HYDRATION_HANDLER_TRACE**: Once per stream per 5 minutes
**PRE_HYDRATION_WAITING_FOR_BARSREQUEST**: Once per stream per 1 minute
**PRE_HYDRATION_RANGE_START_DIAGNOSTIC**: Always logged (critical diagnostic)

### 12.2 Comprehensive Summary Log

**HYDRATION_SUMMARY Event**: Logged once per stream per day at transition

**Contains**:
- Bar counts by source (historical, live, deduped, filtered)
- Completeness metrics (expected vs loaded)
- Late-start handling (missed breakout detection)
- Reconstructed range (if available)
- Timing context (now, range_start, slot_time)

**Location**: `StreamStateMachine.cs:1553-1592`

---

## 13. Recovery & Restart

### 13.1 Restart Scenarios

**Scenario 1: Restart Before Range Lock**
- **State**: Stream in `PRE_HYDRATION` or `ARMED`
- **Recovery**: Re-run hydration (CSV or BarsRequest)
- **Journal**: `EntryDetected` flag restored from journal

**Scenario 2: Restart After Range Lock**
- **State**: Stream was `RANGE_LOCKED`
- **Recovery**: `RestoreRangeLockedFromHydrationLog()` restores range
- **Fallback**: If hydration log missing, recompute range on next tick

**Scenario 3: Restart After Trade**
- **State**: Stream was `DONE` (trade completed)
- **Recovery**: Journal indicates committed, stream stays `DONE`

### 13.2 Recovery Process

**On Stream Initialization** (`StreamStateMachine.cs:402-457`):
```csharp
// REQUIRED CHANGE #4: Restart recovery — make hydration/range events canonical
// On startup, replay hydration_{day}.jsonl (or ranges_{day}.jsonl)
if (existing != null && existing.Committed == false)
{
    // First, try to restore from hydration/ranges log (canonical source)
    RestoreRangeLockedFromHydrationLog(tradingDateStr, Stream);
    
    // If hydration log restoration failed, fall back to journal LastState check
    if (!_rangeLocked && existing.LastState == "RANGE_LOCKED")
    {
        // Fallback: Journal indicates locked but hydration/ranges log missing
        LogHealth("WARN", "RANGE_LOCKED_RESTORE_FALLBACK", ...);
    }
}
```

---

## 14. Key Invariants

### 14.1 Enforced Invariants

1. **Pre-Hydration Completion**: `ARMED` state requires `_preHydrationComplete == true`
   - **Enforcement**: `HandleArmedState()` checks flag (fail-closed if false)

2. **Bar Source Precedence**: `LIVE > BARSREQUEST > CSV`
   - **Enforcement**: `AddBarToBuffer()` replaces lower-precedence bars

3. **Trading Date Matching**: Only bars matching `TradingDate` used for range
   - **Enforcement**: `ComputeRangeRetrospectively()` filters by date

4. **Time Window**: Only bars in `[RangeStartChicagoTime, SlotTimeChicagoTime)` used for range
   - **Enforcement**: `ComputeRangeRetrospectively()` filters by time window

5. **Liveness Guarantee**: `PRE_HYDRATION` must exit within `RangeStartChicagoTime + 1 minute`
   - **Enforcement**: Hard timeout forces transition

### 14.2 Fail-Closed Behaviors

**Missing CSV File**: Log error, mark complete, proceed (will fail range lock later)
**BarsRequest Timeout**: Hard timeout forces transition (will fail range lock later)
**Trading Date Mismatch**: Filter wrong-date bars, log filtering stats
**Range Computation Failure**: Log error, continue (non-blocking)

---

## 15. Performance Considerations

### 15.1 Bar Buffer Size

**Typical Size**: ~870 bars (14.5 hours × 60 minutes)
**Memory**: ~870 × 40 bytes = ~35 KB per stream
**Scaling**: Linear with number of streams

### 15.2 CSV File Reading

**Performance**: Line-by-line reading (no bulk load)
**Optimization**: Early exit if past hydration window
**Bottleneck**: File I/O (acceptable for DRYRUN mode)

### 15.3 BarsRequest Performance

**Async**: Bars arrive via callback (non-blocking)
**Waiting**: Stream waits in `PRE_HYDRATION` until complete
**Timeout**: Hard timeout prevents infinite wait

---

## 16. Summary: Hydration Flow

### 16.1 Complete Flow Diagram

```
Stream Created
    ↓
State = PRE_HYDRATION
    ↓
[DRYRUN] PerformPreHydration()
    ├─ Read CSV file
    ├─ Filter bars by window
    └─ Add to buffer (BarSource.CSV)
    ↓
[SIM] Wait for BarsRequest
    ├─ Check IsBarsRequestPending()
    ├─ If pending → Wait (log PRE_HYDRATION_WAITING_FOR_BARSREQUEST)
    └─ If not pending → Mark _preHydrationComplete = true
    ↓
Bars Arrive (via OnBar callback)
    ├─ AddBarToBuffer() with BarSource.BARSREQUEST or LIVE
    └─ Deduplication (LIVE > BARSREQUEST > CSV)
    ↓
Check Transition Conditions
    ├─ barCount > 0 OR
    ├─ nowChicago >= RangeStartChicagoTime OR
    └─ Hard timeout (RangeStartChicagoTime + 1 minute)
    ↓
[If Late Start] CheckMissedBreakout()
    ├─ Compute range from [range_start, slot_time)
    ├─ Scan bars [slot_time, now] for breakout
    └─ If missed breakout → Commit NO_TRADE_LATE_START_MISSED_BREAKOUT
    ↓
Log HYDRATION_SUMMARY
    ├─ Bar counts by source
    ├─ Completeness metrics
    └─ Reconstructed range (if available)
    ↓
Transition to ARMED
    └─ State = ARMED
```

### 16.2 Key Metrics

**Completeness**: `(loaded_bars / expected_bars) × 100%`
**Bar Sources**: Historical (BARSREQUEST/CSV), Live (LIVE)
**Deduplication**: Count of bars replaced by higher-precedence sources
**Filtering**: Future bars, partial bars, wrong-date bars

---

## 17. Testing Recommendations

### 17.1 Unit Tests

1. **CSV Loading**: Test `PerformPreHydration()` with various CSV formats
2. **BarsRequest Waiting**: Test SIM mode waiting logic
3. **Deduplication**: Test bar source precedence
4. **Missed Breakout**: Test late-start breakout detection
5. **Range Reconstruction**: Test `ComputeRangeRetrospectively()` with various bar sets

### 17.2 Integration Tests

1. **DRYRUN Mode**: Test full hydration flow with CSV files
2. **SIM Mode**: Test BarsRequest completion and bar arrival
3. **Late Start**: Test starting after slot_time with/without missed breakout
4. **Recovery**: Test restart scenarios (before/after range lock)

### 17.3 Edge Case Tests

1. **Missing CSV**: Test behavior when CSV file doesn't exist
2. **BarsRequest Timeout**: Test hard timeout behavior
3. **Trading Date Mismatch**: Test filtering of wrong-date bars
4. **Out-of-Order Bars**: Test deduplication and sorting
5. **Zero Bars**: Test transition with 0 bars (hard timeout)

---

## 18. Known Issues & Limitations

### 18.1 Current Limitations

1. **Gap Tolerance Disabled**: All gaps tolerated (no range invalidation)
2. **CSV Format Assumed**: Assumes specific CSV format (timestamp_utc, open, high, low, close)
3. **BarsRequest Timeout**: Hard timeout is 1 minute (may be too short for slow APIs)
4. **Recovery Fallback**: Falls back to journal if hydration log missing (less reliable)

### 18.2 Potential Improvements

1. **Configurable Timeout**: Make hard timeout configurable
2. **CSV Format Validation**: Validate CSV format before parsing
3. **BarsRequest Retry**: Add retry logic for failed BarsRequests
4. **Hydration Log Priority**: Always use hydration log (never fall back to journal)

---

## 19. Critical Code Locations

### 19.1 Main Methods

- **`HandlePreHydrationState()`**: Main hydration handler (`StreamStateMachine.cs:981`)
- **`PerformPreHydration()`**: CSV file loading (`StreamStateMachine.cs:3531`)
- **`AddBarToBuffer()`**: Bar addition with deduplication (`StreamStateMachine.cs:2400`)
- **`ComputeRangeRetrospectively()`**: Range computation (`StreamStateMachine.cs:3790`)
- **`CheckMissedBreakout()`**: Late-start breakout detection (`StreamStateMachine.cs:903`)
- **`RestoreRangeLockedFromHydrationLog()`**: Recovery from logs (`StreamStateMachine.cs:4498`)

### 19.2 Key Files

- **`StreamStateMachine.cs`**: Main hydration logic
- **`HydrationEvent.cs`**: Event model
- **`HydrationEventPersister.cs`**: Event persistence
- **`RobotEngine.cs`**: BarsRequest tracking

---

## 20. Conclusion

The hydration system is a **critical component** that ensures streams have sufficient historical data before beginning range building. Key strengths:

✅ **Mode-Specific**: Different strategies for DRYRUN (CSV) vs SIM (BarsRequest)  
✅ **Robust Recovery**: Can restore range lock state from hydration logs  
✅ **Late-Start Handling**: Detects missed breakouts when starting after slot_time  
✅ **Fail-Safe**: Hard timeout prevents infinite wait  
✅ **Comprehensive Logging**: Detailed metrics and diagnostics  

**Areas for Improvement**:
- Configurable timeout values
- BarsRequest retry logic
- Enhanced CSV format validation
- Performance optimization for large bar buffers

---

**End of Hydration Review**
