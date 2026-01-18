# Pre-Hydration (Rehydration) System Summary

## Overview

The pre-hydration system loads historical bars at strategy startup to fill the bar buffer before range computation. It supports two execution modes with different data sources.

## Architecture

### **Two Execution Modes**

#### **SIM Mode** (NinjaTrader)
- **Primary Source**: NinjaTrader BarsRequest API (historical bars)
- **Secondary Source**: File-based CSV (fallback/supplement)
- **Live Bars**: Buffered continuously via `OnBar()`
- **Deduplication**: Prefers live bars over historical bars

#### **DRYRUN Mode** (Backtesting)
- **Primary Source**: File-based CSV only
- **No BarsRequest**: Not available (no NinjaTrader context)
- **No Live Bars**: Simulated bars from files only

---

## Execution Flow

### **Step 1: Strategy Initialization**

**Location**: `RobotSimStrategy.OnStateChange(State.DataLoaded)`

```
1. Verify SIM account
2. Create RobotEngine instance
3. Start engine
4. Request historical bars from NinjaTrader (SIM mode only)
   → RequestHistoricalBarsForPreHydration()
```

### **Step 2: Engine Startup**

**Location**: `RobotEngine.Start()`

```
1. Load ParitySpec
2. Create TimeService
3. Load timetable and lock trading date
   → _activeTradingDate = timetable.trading_date
4. Create streams (all start in PRE_HYDRATION state)
5. Emit startup banner
```

### **Step 3: BarsRequest (SIM Mode Only)**

**Location**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`

```
1. Get trading date from engine
2. Parse trading date
3. Set time range:
   - rangeStartChicago = "02:00" (hardcoded - should come from spec)
   - slotTimeChicago = "07:30" (hardcoded - should come from spec)
4. Calculate end time:
   - endTimeChicago = min(nowChicago, slotTimeChicago)
   - Prevents loading future bars
5. Request bars via NinjaTraderBarRequest.RequestBarsForTradingDate()
6. Filter bars:
   - Remove future bars (timestamp > now)
   - Remove partial bars (age < 1 minute)
7. Feed to engine: _engine.LoadPreHydrationBars()
```

### **Step 4: Engine Processes BarsRequest Bars**

**Location**: `RobotEngine.LoadPreHydrationBars()`

```
1. Validate streams exist
2. Filter bars:
   - Future bars (timestamp > now)
   - Partial bars (age < 1 minute)
3. Record filtered counts per stream
4. Feed bars to matching streams:
   → stream.OnBar(bar, isHistorical: true)
```

### **Step 5: Stream Pre-Hydration State**

**Location**: `StreamStateMachine.HandlePreHydrationState()`

#### **SIM Mode Flow**:
```
1. Perform file-based pre-hydration (if not complete)
   → PerformPreHydration(utcNow)
   → Reads CSV files
   → Marks bars as historical
   → Adds to buffer with deduplication

2. After file-based completes:
   - Check bar count
   - If bars > 0 OR now >= range_start:
     → Transition to ARMED
   - Otherwise: Wait for more bars (BarsRequest or live)

3. BarsRequest bars arrive via OnBar(isHistorical: true)
   → Buffered in PRE_HYDRATION state
   → Deduplicated (prefer live over historical)
```

#### **DRYRUN Mode Flow**:
```
1. Perform file-based pre-hydration
   → PerformPreHydration(utcNow)
   → Reads CSV files
   → Marks bars as historical
   → Adds to buffer

2. After file-based completes:
   → Transition to ARMED immediately
```

### **Step 6: File-Based Pre-Hydration**

**Location**: `StreamStateMachine.PerformPreHydration()`

```
1. Compute hydration window:
   - Start: RangeStartChicagoTime (from spec)
   - End: min(nowChicago, SlotTimeChicagoTime)

2. Resolve file path:
   - data/raw/{instrument}/1m/{yyyy}/{MM}/{instrument}_1m_{yyyy-MM-dd}.csv
   - Example: data/raw/es/1m/2026/01/ES_1m_2026-01-16.csv

3. Read CSV file:
   - Format: timestamp_utc,open,high,low,close,volume
   - Filter by hydration window
   - Parse OHLCV

4. Add to buffer:
   - Check for duplicates
   - Deduplicate (prefer live over historical)
   - Track as historical bars
   - Sort chronologically
```

---

## Bar Source Tracking

### **Counters** (per stream):
- `_historicalBarCount`: Bars from BarsRequest/file-based pre-hydration
- `_liveBarCount`: Bars from live feed (`OnBar()`)
- `_dedupedBarCount`: Bars deduplicated (replaced existing)
- `_filteredFutureBarCount`: Bars filtered (future)
- `_filteredPartialBarCount`: Bars filtered (partial/in-progress)

### **Logging**:
Logged in `RANGE_COMPUTE_COMPLETE`:
```json
{
  "historical_bar_count": 330,
  "live_bar_count": 45,
  "deduped_bar_count": 2,
  "filtered_future_bar_count": 5,
  "filtered_partial_bar_count": 3,
  "total_bars_received": 377,
  "bar_count": 328
}
```

---

## Deduplication Rules

### **Deterministic Rule**: Prefer Live Bars Over Historical

**When duplicate timestamp detected**:
1. **Live bar replaces historical bar** (if live bar arrives after historical)
2. **Log replacement** if OHLC values differ
3. **Track deduplication count**

**Rationale**:
- Live bars are more current
- May include vendor corrections
- Represents real-time market data

**Exception**: File-based pre-hydration replaces existing bars (file is authoritative for that source)

---

## Filtering Rules

### **Future Bar Filtering**:
- **Rule**: Reject bars with `timestamp > now`
- **Location**: `RobotEngine.LoadPreHydrationBars()`
- **Purpose**: Prevent injecting future data

### **Partial Bar Filtering**:
- **Rule**: Reject bars with `age < 1 minute`
- **Location**: `RobotEngine.LoadPreHydrationBars()` and `StreamStateMachine.AddBarToBuffer()`
- **Purpose**: Only accept fully closed bars

### **Trading Date Filtering**:
- **Rule**: Only accept bars matching locked trading date
- **Location**: `RobotEngine.OnBar()` and `StreamStateMachine.OnBar()`
- **Purpose**: Ensure bars belong to correct trading day

---

## State Transitions

### **PRE_HYDRATION → ARMED**

**SIM Mode**:
- File-based pre-hydration completes AND
- (Bar count > 0 OR now >= range_start)

**DRYRUN Mode**:
- File-based pre-hydration completes

**BarsRequest bars** (SIM mode):
- Arrive via `OnBar(isHistorical: true)`
- Buffered in PRE_HYDRATION, ARMED, or RANGE_BUILDING states
- Deduplicated with existing bars

---

## Issues Found and Fixed

### **Issue 1: Hardcoded Time Range in BarsRequest**
**Problem**: `RequestHistoricalBarsForPreHydration()` uses hardcoded "02:00" and "07:30"
**Impact**: Doesn't adapt to different session configurations
**Status**: ⚠️ Should be fixed to read from spec

### **Issue 2: Inconsistent Deduplication Comments**
**Problem**: Comment says "file is authoritative" but rule says "prefer live bars"
**Impact**: Confusing documentation
**Status**: ✅ Fixed - clarified rule

### **Issue 3: Race Condition in SIM Mode**
**Problem**: Transition to ARMED might occur before BarsRequest bars arrive
**Impact**: BarsRequest bars still buffered, but transition happens early
**Status**: ✅ Acceptable - bars continue to buffer in ARMED state

### **Issue 4: Missing Bar Source Logging**
**Problem**: No visibility into bar sources
**Impact**: Hard to debug bar origin
**Status**: ✅ Fixed - added comprehensive logging

---

## Data Flow Diagram

```
SIM Mode:
┌─────────────────┐
│ NinjaTrader     │
│ BarsRequest     │──┐
└─────────────────┘  │
                      ├──► Filter (future/partial)
┌─────────────────┐  │    │
│ CSV Files       │──┘    │
└─────────────────┘       │
                          ▼
                    ┌─────────────┐
                    │ Bar Buffer  │
                    │ (deduped)   │
                    └─────────────┘
                          │
                          ▼
                    ┌─────────────┐
                    │ Live Bars   │──► Deduplicate
                    │ (OnBar)     │    (prefer live)
                    └─────────────┘
                          │
                          ▼
                    ┌─────────────┐
                    │ Range       │
                    │ Computation │
                    └─────────────┘

DRYRUN Mode:
┌─────────────────┐
│ CSV Files       │──► Filter (window)
└─────────────────┘    │
                       ▼
                 ┌─────────────┐
                 │ Bar Buffer  │
                 └─────────────┘
                       │
                       ▼
                 ┌─────────────┐
                 │ Range       │
                 │ Computation │
                 └─────────────┘
```

---

## Key Invariants

1. **Trading Date**: All bars must match locked trading date from timetable
2. **Bar Age**: Only fully closed bars (age >= 1 minute) accepted
3. **Future Bars**: No bars with timestamp > now accepted
4. **Deduplication**: (instrument, barStartUtc) must be unique in buffer
5. **Source Tracking**: All bars tracked by source (historical vs live)
6. **Deterministic**: Same inputs produce same results

---

## Logging Events

### **Pre-Hydration Events**:
- `PRE_HYDRATION_START`: File-based pre-hydration started
- `PRE_HYDRATION_COMPLETE`: File-based pre-hydration completed
- `PRE_HYDRATION_ZERO_BARS`: No bars loaded from file
- `PRE_HYDRATION_ERROR`: Pre-hydration failed

### **BarsRequest Events**:
- `PRE_HYDRATION_BARS_LOADED`: BarsRequest bars loaded
- `PRE_HYDRATION_BARS_FILTERED`: Bars filtered (future/partial)
- `PRE_HYDRATION_NO_BARS_AFTER_FILTER`: All bars filtered out
- `PRE_HYDRATION_BARS_SKIPPED`: Bars skipped (streams not created)

### **Range Computation Events**:
- `RANGE_COMPUTE_COMPLETE`: Includes bar source statistics
  - `historical_bar_count`
  - `live_bar_count`
  - `deduped_bar_count`
  - `filtered_future_bar_count`
  - `filtered_partial_bar_count`

---

## Summary

The pre-hydration system provides a robust mechanism for loading historical bars at startup, with support for both NinjaTrader BarsRequest (SIM mode) and file-based CSV (DRYRUN mode). It includes comprehensive filtering, deduplication, and source tracking to ensure data integrity and debugging transparency.
