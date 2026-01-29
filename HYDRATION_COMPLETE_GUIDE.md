# Hydration Complete Guide

**Date**: 2026-01-28  
**Purpose**: Comprehensive explanation of how the robot loads and processes bars, including pre-hydration, bar sources, deduplication, and state transitions.

---

## Table of Contents

1. [What is Hydration?](#what-is-hydration)
2. [Pre-Hydration State](#pre-hydration-state)
3. [Bar Sources](#bar-sources)
4. [Bar Buffer and Deduplication](#bar-buffer-and-deduplication)
5. [Transition to ARMED](#transition-to-armed)
6. [DRYRUN vs SIM Mode](#dryrun-vs-sim-mode)
7. [Late-Start Handling](#late-start-handling)
8. [Completeness Metrics](#completeness-metrics)
9. [Range Reconstruction](#range-reconstruction)
10. [Restart Behavior](#restart-behavior)

---

## What is Hydration?

**Hydration** is the process of loading historical bars into the robot's memory before trading begins. The robot needs bars from the **range start time** (e.g., 02:00 CT for S1) up to the **slot time** (e.g., 07:30 CT) to:

1. **Build the range**: Compute the high/low range from all bars in the range window
2. **Detect breakouts**: Determine if price has already broken out of the range
3. **Make trading decisions**: Calculate breakout levels and place orders

**Key Concept**: The robot operates in two phases:
- **Pre-Hydration**: Loading historical bars (state: `PRE_HYDRATION`)
- **Live Trading**: Processing new bars as they arrive (states: `ARMED`, `RANGE_BUILDING`, `RANGE_LOCKED`)

---

## Pre-Hydration State

### Initial State

When a stream is created, it starts in `PRE_HYDRATION` state:

```csharp
public StreamState State { get; private set; } = StreamState.PRE_HYDRATION;
```

### What Happens During Pre-Hydration

1. **Bar Collection**: Bars are collected from various sources (BarsRequest, CSV files, or live feed)
2. **Bar Buffering**: Bars are stored in an in-memory buffer (`_barBuffer`)
3. **Deduplication**: Duplicate bars from different sources are handled with precedence rules
4. **Filtering**: Future bars and partial bars are filtered out
5. **Transition Check**: When conditions are met, the stream transitions to `ARMED`

### Pre-Hydration Completion Flag

```csharp
private bool _preHydrationComplete = false;
```

This flag indicates that pre-hydration setup is complete:
- **DRYRUN mode**: Set to `true` after CSV files are loaded
- **SIM mode**: Set to `true` immediately (BarsRequest bars arrive asynchronously)

**Important**: Setting `_preHydrationComplete = true` does NOT mean bars have arrived yet. It means the pre-hydration process has been initiated.

---

## Bar Sources

The robot receives bars from three sources, each with a different precedence level:

### 1. LIVE (Highest Precedence)

**Source**: Live feed from NinjaTrader (`OnBar()` callback)  
**Precedence**: `0` (highest)  
**When**: Bars arrive in real-time as they close

**Characteristics**:
- Most current data (vendor corrections applied)
- Always wins over historical sources
- Used during `ARMED`, `RANGE_BUILDING`, and `RANGE_LOCKED` states
- Age validation bypassed (liveness guarantee)

### 2. BARSREQUEST (Medium Precedence)

**Source**: NinjaTrader `BarsRequest` API (historical data)  
**Precedence**: `1` (medium)  
**When**: Requested during strategy initialization (`DataLoaded` state)

**Characteristics**:
- Historical bars from NinjaTrader's data feed
- More authoritative than CSV files
- Used for pre-hydration in SIM mode
- Arrives asynchronously via callbacks

**Request Details**:
- Time range: From `range_start_time` to `min(slot_time, now)`
- Prevents future bars from being injected
- Non-blocking (fire-and-forget in background thread)

### 3. CSV (Lowest Precedence)

**Source**: CSV files from `data/snapshots/` directory  
**Precedence**: `2` (lowest)  
**When**: Loaded during pre-hydration in DRYRUN mode

**Characteristics**:
- Fallback/supplement source
- Used only in DRYRUN mode
- Lowest priority (replaced by BARSREQUEST or LIVE bars)

### Precedence Ladder

```
LIVE (0) > BARSREQUEST (1) > CSV (2)
```

**Rule**: When multiple sources provide the same bar (same timestamp), the source with **lower enum value** (higher precedence) wins.

**Example**:
- BarsRequest provides bar at 07:25:00
- CSV also provides bar at 07:25:00
- Live feed provides bar at 07:25:00 (when bar closes at 07:26:00)
- **Result**: LIVE bar wins, replacing both BARSREQUEST and CSV bars

---

## Bar Buffer and Deduplication

### Bar Buffer Structure

```csharp
private readonly List<Bar> _barBuffer = new();
private readonly Dictionary<DateTimeOffset, BarSource> _barSourceMap = new();
```

**Purpose**: Thread-safe storage of bars with source tracking for deduplication.

### Deduplication Logic

**Key**: Bars are deduplicated by `(instrument, barStartUtc)` - the bar's timestamp represents its **start time** (e.g., 07:25:00 = bar from 07:25 to 07:26).

**Process** (`AddBarToBuffer()`):

1. **Age Check**: Reject partial bars (< 1 minute old) for BARSREQUEST and CSV sources
   - LIVE bars bypass age check (liveness guarantee)
   - Closedness enforced by NinjaTrader `OnBarClose` configuration

2. **Duplicate Check**: Check if bar with same timestamp already exists

3. **Precedence Check**: If duplicate exists, compare source precedence:
   ```csharp
   if (source < existingSource)  // Lower enum value = higher precedence
   {
       // Replace existing bar with new bar
       _barBuffer[existingBarIndex] = bar;
       _barSourceMap[bar.TimestampUtc] = source;
       _dedupedBarCount++;
   }
   ```

4. **New Bar**: If no duplicate or higher precedence, add to buffer:
   ```csharp
   _barBuffer.Add(bar);
   _barSourceMap[bar.TimestampUtc] = source;
   ```

### Bar Source Tracking

**Counters**:
- `_historicalBarCount`: Bars from BARSREQUEST
- `_liveBarCount`: Bars from LIVE feed
- `_dedupedBarCount`: Bars that replaced existing bars (deduplication)

**Filtering Counters**:
- `_filteredFutureBarCount`: Bars rejected because timestamp > now
- `_filteredPartialBarCount`: Bars rejected because age < 1 minute

### Why Deduplication Matters

**Problem**: Multiple sources can provide the same bar:
- BarsRequest includes 07:25 bar
- CSV file includes 07:25 bar
- Live feed emits 07:25 bar when it closes at 07:26:00

**Solution**: Precedence ladder ensures deterministic behavior:
- LIVE bars always win (most current, vendor corrections)
- BARSREQUEST bars win over CSV (more authoritative)
- CSV bars are lowest priority (fallback)

**Historical vs Live OHLC Mismatch**:
- NinjaTrader historical bars and live bars may have different OHLC values
- Example: Historical high = 4952.25, Live high = 4952.50 for same minute
- **Without precedence**: Range values differ depending on startup timing (non-reproducible)
- **With precedence**: LIVE bars always win, ensuring consistent results

---

## Transition to ARMED

### Transition Conditions

The stream transitions from `PRE_HYDRATION` to `ARMED` when **any** of these conditions are met:

1. **Bar Count > 0**: At least one bar has been loaded
2. **Time Threshold**: Current time >= `RangeStartChicagoTime`
3. **Hard Timeout**: Current time >= `RangeStartChicagoTime + 1 minute` (liveness guarantee)

**Code**:
```csharp
if (barCount > 0 || nowChicago >= RangeStartChicagoTime || shouldForceTransition)
{
    // Transition to ARMED
    State = StreamState.ARMED;
}
```

### Hard Timeout (Liveness Guarantee)

**Purpose**: Ensure streams never get stuck in `PRE_HYDRATION` indefinitely.

**Rule**: If `nowChicago >= RangeStartChicagoTime + 1 minute`, force transition to `ARMED` even if no bars have arrived.

**Why**: Prevents deadlock if BarsRequest fails or CSV files are missing. The robot can still trade using live bars once the range window starts.

**Logging**: `PRE_HYDRATION_FORCED_TRANSITION` event is logged when hard timeout triggers.

### Transition Logging

**HYDRATION_SUMMARY Event**: Logged at every `PRE_HYDRATION` → `ARMED` transition, capturing:

- **Bar Statistics**:
  - `total_bars_in_buffer`: Current bar count
  - `historical_bar_count`: Bars from BARSREQUEST
  - `live_bar_count`: Bars from LIVE feed
  - `deduped_bar_count`: Bars that replaced existing bars
  - `filtered_future_bar_count`: Bars rejected (future timestamp)
  - `filtered_partial_bar_count`: Bars rejected (partial/in-progress)

- **Completeness Metrics**:
  - `expected_bars`: Expected bars based on time range
  - `expected_full_range_bars`: Expected bars for full range window
  - `loaded_bars`: Actual bars loaded
  - `completeness_pct`: Percentage of expected bars loaded

- **Timing Context**:
  - `now_chicago`: Current time (Chicago)
  - `range_start_chicago`: Range start time
  - `slot_time_chicago`: Slot time (range end)

- **Late-Start Handling**:
  - `late_start`: Whether starting after slot_time
  - `missed_breakout`: Whether breakout already occurred
  - `reconstructed_range_high/low`: Range computed from historical bars

---

## DRYRUN vs SIM Mode

### DRYRUN Mode

**Bar Sources**: CSV files only (no BarsRequest, no live feed)

**Pre-Hydration Process**:
1. `PerformPreHydration()` loads CSV files from `data/snapshots/`
2. Bars are parsed and added to buffer with `BarSource.CSV`
3. `_preHydrationComplete` set to `true` after CSV loading completes
4. Transition to `ARMED` when bars loaded or time threshold reached

**CSV File Location**:
- `data/snapshots/<instrument>/<trading_date>.csv`
- Example: `data/snapshots/MES/2026-01-28.csv`

**CSV Format**:
- Columns: `timestamp_utc,open,high,low,close`
- Timestamp represents bar **open time** (already converted by translator)

### SIM Mode

**Bar Sources**: BarsRequest only (no CSV files, live feed used after transition)

**Pre-Hydration Process**:
1. `RequestHistoricalBarsForPreHydration()` called during `DataLoaded` state
2. BarsRequest initiated in background thread (non-blocking)
3. `_preHydrationComplete` set to `true` immediately (bars arrive asynchronously)
4. Bars arrive via `OnBar()` callback with `isHistorical: true`
5. Bars marked as `BarSource.BARSREQUEST` and buffered
6. Transition to `ARMED` when bars loaded or time threshold reached

**BarsRequest Details**:
- **Time Range**: From `range_start_time` to `min(slot_time, now)`
- **Non-Blocking**: Fire-and-forget in background thread
- **Error Handling**: If BarsRequest fails, strategy continues with live bars only
- **Restart Behavior**: If restarting after slot_time, BarsRequest limited to slot_time (prevents range input set changes)

**Live Feed After Transition**:
- Once in `ARMED` state, live bars arrive via `OnBar()` callback
- Live bars marked as `BarSource.LIVE` and win over BARSREQUEST bars (precedence)

---

## Late-Start Handling

### What is Late-Start?

**Late-Start**: Strategy starts **after** `slot_time` (e.g., starting at 08:00 when slot_time is 07:30).

**Implications**:
- Range build window (`[range_start, slot_time)`) has already passed
- Breakout may have already occurred
- Cannot trade if breakout already happened

### Late-Start Detection

```csharp
bool isLateStart = nowChicago > SlotTimeChicagoTime;
```

### Range Reconstruction

**Process**:
1. Compute range from bars < `slot_time` (slot_time is **EXCLUSIVE**)
2. Use `ComputeRangeRetrospectively()` to build range from historical bars
3. Log reconstructed range in `HYDRATION_SUMMARY` event

**Code**:
```csharp
var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
if (rangeResult.Success)
{
    reconstructedRangeHigh = rangeResult.RangeHigh.Value;
    reconstructedRangeLow = rangeResult.RangeLow.Value;
}
```

### Missed Breakout Detection

**If Late-Start**: Check if breakout already occurred:

```csharp
if (isLateStart)
{
    var missedBreakoutResult = CheckMissedBreakout(utcNow, reconstructedRangeHigh.Value, reconstructedRangeLow.Value);
    missedBreakout = missedBreakoutResult.MissedBreakout;
    breakoutTimeUtc = missedBreakoutResult.BreakoutTimeUtc;
    breakoutPrice = missedBreakoutResult.BreakoutPrice;
    breakoutDirection = missedBreakoutResult.BreakoutDirection;
}
```

**Breakout Scan Window**: `[slot_time, now]` (only if late start)

**If Missed Breakout**:
- Log `LATE_START_MISSED_BREAKOUT` health event
- Commit with reason `NO_TRADE_LATE_START_MISSED_BREAKOUT`
- **Do NOT transition to ARMED** (trading blocked)

**If No Missed Breakout**:
- Transition to `ARMED` normally
- Range is reconstructed and ready for trading

### Boundary Contract

**Range Build Window**: `[RangeStartChicagoTime, SlotTimeChicagoTime)` - slot_time is **EXCLUSIVE**

**Missed Breakout Scan Window**: `[SlotTimeChicagoTime, nowChicago]` - only if `nowChicago > SlotTimeChicagoTime`

**Logging**: `HYDRATION_BOUNDARY_CONTRACT` event documents these boundaries to prevent regressions.

---

## Completeness Metrics

### Expected Bars Calculation

**Formula**:
```csharp
var hydrationEndChicago = nowChicago < SlotTimeChicagoTime ? nowChicago : SlotTimeChicagoTime;
var rangeDurationMinutes = (hydrationEndChicago - RangeStartChicagoTime).TotalMinutes;
expectedBars = Math.Max(0, (int)Math.Floor(rangeDurationMinutes));
```

**Example**:
- Range start: 02:00 CT
- Slot time: 07:30 CT
- Current time: 07:25 CT (before slot_time)
- Expected bars: `(07:25 - 02:00) = 325 minutes = 325 bars`

**Full Range Expected Bars**:
```csharp
var fullRangeDurationMinutes = (SlotTimeChicagoTime - RangeStartChicagoTime).TotalMinutes;
expectedFullRangeBars = Math.Max(0, (int)Math.Floor(fullRangeDurationMinutes));
```

### Completeness Percentage

**Formula**:
```csharp
completenessPct = expectedBars > 0 
    ? Math.Min(100.0, (loadedBars / (double)expectedBars) * 100.0) 
    : 0.0;
```

**Interpretation**:
- `100%`: All expected bars loaded (perfect)
- `50%`: Half of expected bars loaded (may indicate data feed issues)
- `0%`: No bars loaded (will rely on live bars)

**Use Cases**:
- **High completeness (>90%)**: Good data quality, range likely accurate
- **Low completeness (<50%)**: May indicate data feed issues, gaps, or late start
- **0% completeness**: No historical bars, will rely entirely on live bars (may miss early breakouts)

---

## Range Reconstruction

### What is Range Reconstruction?

**Range Reconstruction**: Computing the high/low range from historical bars that were loaded during pre-hydration.

**Purpose**: 
- Determine the trading range for breakout detection
- Handle late-start scenarios (reconstruct range from historical bars)
- Ensure deterministic range calculation

### When Range is Reconstructed

1. **Normal Start**: Range built incrementally as bars arrive during `RANGE_BUILDING` state
2. **Late Start**: Range reconstructed retrospectively from all bars < `slot_time` during `PRE_HYDRATION` → `ARMED` transition
3. **Restart**: Range recomputed from all available bars (historical + live) if restarting mid-session

### Range Computation Method

**`ComputeRangeRetrospectively()`**:
- Scans all bars in buffer with timestamp < `endTimeUtc`
- Computes `RangeHigh` = max(high) and `RangeLow` = min(low)
- Returns range values for breakout level calculation

**Code**:
```csharp
var rangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc);
if (rangeResult.Success && rangeResult.RangeHigh.HasValue && rangeResult.RangeLow.HasValue)
{
    reconstructedRangeHigh = rangeResult.RangeHigh.Value;
    reconstructedRangeLow = rangeResult.RangeLow.Value;
}
```

### Range Build Window

**Window**: `[RangeStartChicagoTime, SlotTimeChicagoTime)` - slot_time is **EXCLUSIVE**

**Why Exclusive**: 
- Slot time marks the end of range building
- Bars at exactly slot_time are not included in range calculation
- Ensures consistent range boundaries across restarts

**Example**:
- Range start: 02:00 CT
- Slot time: 07:30 CT
- Range includes bars: 02:00, 02:01, ..., 07:29
- Range excludes bars: 07:30 and later

---

## Restart Behavior

### Restart Policy: "Restart = Full Reconstruction"

**Principle**: When strategy restarts mid-session, the robot reconstructs state from scratch using all available bars (historical + live).

### BarsRequest on Restart

**Time Range Limitation**:
- If restarting **before** slot_time: Request bars from `range_start` to `now`
- If restarting **after** slot_time: Request bars from `range_start` to `slot_time` only

**Why Limit to Slot Time**:
- Prevents loading bars beyond slot_time, which would change the range input set
- Ensures deterministic reconstruction (same bars = same range)

**Code**:
```csharp
var endTimeChicago = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
    ? nowChicago.ToString("HH:mm")  // Before slot_time: use now
    : slotTimeChicago;               // After slot_time: use slot_time
```

### State Restoration

**What is Restored**:
- `_stopBracketsSubmittedAtLock`: Whether stop brackets were already submitted
- `_entryDetected`: Whether entry order was already filled
- `RangeHigh`/`RangeLow`: Recomputed from all available bars

**What is NOT Restored**:
- Stream state (always starts as `PRE_HYDRATION`, transitions naturally)
- Bar buffer (cleared and rebuilt from BarsRequest + live bars)

### Range Recomputation

**If LastState == "RANGE_LOCKED"**:
- Recompute breakout levels from restored `RangeHigh`/`RangeLow`
- Ensures breakout levels are ready when state transitions back to `RANGE_LOCKED`

**Code**:
```csharp
if (existing.LastState == "RANGE_LOCKED" && 
    (!_brkLongRounded.HasValue || !_brkShortRounded.HasValue) &&
    RangeHigh.HasValue && RangeLow.HasValue)
{
    // Recompute breakout levels deterministically
    ComputeBreakoutLevelsAndLog(utcNow);
}
```

### Idempotency

**Execution Journal**: Prevents duplicate order submissions
- Checks `IsIntentSubmitted()` before submitting entry orders
- Checks `IsBEModified()` before modifying stop to break-even
- Checks `HasEntryFillForStream()` to detect existing entries

**Result**: Restart is safe - robot remembers what it already did and won't duplicate actions.

---

## Summary

### Key Concepts

1. **Hydration**: Loading historical bars before trading begins
2. **Pre-Hydration State**: Initial state where bars are collected and buffered
3. **Bar Sources**: LIVE (highest), BARSREQUEST (medium), CSV (lowest)
4. **Deduplication**: Precedence ladder ensures deterministic bar selection
5. **Transition**: PRE_HYDRATION → ARMED when bars loaded or time threshold reached
6. **Late-Start**: Handle restarts after slot_time with range reconstruction
7. **Completeness**: Metrics track how many bars were loaded vs expected
8. **Range Reconstruction**: Compute range from historical bars for late-start scenarios

### Bottom Line

**The robot's hydration system ensures**:
- ✅ Deterministic bar selection (precedence ladder)
- ✅ Safe restarts (full reconstruction, idempotency)
- ✅ Late-start handling (range reconstruction, missed breakout detection)
- ✅ Comprehensive logging (HYDRATION_SUMMARY, completeness metrics)
- ✅ Liveness guarantees (hard timeout prevents deadlock)

**You can restart anytime** - the robot will reconstruct state from available bars and continue trading safely.
