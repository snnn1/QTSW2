# Range Building Process Walkthrough

## Overview

The range building process is the core mechanism that determines the high/low price range for a trading session. This document explains the complete flow from initialization to range lock, including all state transitions, data collection, and computation steps.

## State Machine Flow

```
PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE
```

### 1. PRE_HYDRATION State

**Purpose**: Load historical bars before the range window starts.

**What Happens**:
- **DRYRUN Mode**: Loads bars from CSV files
- **SIM Mode**: Waits for BarsRequest to load historical bars from NinjaTrader
- Bars are buffered in `_barBuffer` as they arrive
- Tracks bar sources: `_historicalBarCount`, `_liveBarCount`, `_dedupedBarCount`

**Transition Conditions**:
- Pre-hydration marked complete (`_preHydrationComplete = true`)
- Has bars in buffer OR past range start time
- Transitions to `ARMED` state

**Key Logs**:
- `HYDRATION_SUMMARY`: Comprehensive bar source statistics at transition
- `PRE_HYDRATION_TIMEOUT_NO_BARS`: If transitioning without bars

---

### 2. ARMED State

**Purpose**: Wait for range start time, then begin range building.

**What Happens**:
- Waits until `RangeStartChicagoTime` is reached
- Monitors bar buffer for incoming bars
- Can compute initial range from available bars (incremental computation)
- Tracks gap violations as bars arrive

**Transition Conditions**:
- Current time >= `RangeStartChicagoTime`
- Transitions to `RANGE_BUILDING` state

**Key Logs**:
- `RANGE_BUILD_START`: When entering RANGE_BUILDING
- `RANGE_INITIALIZED`: If initial range computed from history
- `RANGE_INITIALIZATION_FAILED`: If initial computation fails

---

### 3. RANGE_BUILDING State

**Purpose**: Collect bars and compute the final range until slot time.

**What Happens**:

#### A) Bar Collection
- Bars arrive via `OnBar()` method
- Each bar is added to `_barBuffer` with source tracking
- Gap tolerance checking occurs on each bar:
  - Max single gap: 3.0 minutes
  - Max total gaps: 6.0 minutes
  - Max gap in last 10 minutes: 2.0 minutes
- If gap violation detected → `_rangeInvalidated = true`

#### B) Incremental Range Updates (Optional)
- Can compute range incrementally as bars arrive
- Updates `RangeHigh` and `RangeLow` in real-time
- Uses `ComputeRangeRetrospectively()` with `endTimeUtc: utcNow`
- Logs `RANGE_INITIALIZED` when first computed

#### C) Heartbeat Monitoring
- Logs heartbeat every 5 minutes (`HEARTBEAT_INTERVAL_MINUTES`)
- Reports: state, bar count, gap status, range invalidation status

#### D) Data Feed Stall Detection
- Warns if no bars received for 5+ minutes (`DATA_FEED_STALL_THRESHOLD_MINUTES`)
- Logs `DATA_FEED_STALL` warning

#### E) Slot Gate Evaluation (Diagnostic)
- Checks if `utcNow >= SlotTimeUtc && !_rangeComputed`
- Logs `SLOT_GATE_DIAGNOSTIC` when diagnostic logs enabled
- Rate-limited to prevent spam

#### F) Final Range Computation (At Slot Time)
When `utcNow >= SlotTimeUtc`:

1. **Check Range Invalidation**:
   - If `_rangeInvalidated == true` → Commit with `RANGE_INVALIDATED`
   - Prevents trading for this stream

2. **Compute Final Range**:
   - If `!_rangeComputed`: Compute retrospectively from all bars
   - Uses `ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc)`
   - Filters bars: `RangeStartChicagoTime <= bar < SlotTimeChicagoTime`
   - Finds min/max OHLC values in window
   - Sets `FreezeClose` = close price of last bar before slot time

3. **Range Computation Result**:
   - **Success**: Sets `RangeHigh`, `RangeLow`, `FreezeClose`, `_rangeComputed = true`
   - **Failure**: Logs `RANGE_COMPUTE_FAILED`, checks for stuck state

4. **Logging**:
   - `RANGE_COMPUTE_COMPLETE`: Comprehensive range computation details
   - `RANGE_LOCK_ASSERT`: Invariant checks (first bar >= range start, last bar < slot time)
   - `RANGE_LOCKED_INCREMENTAL`: If range was already computed incrementally

5. **Transition to RANGE_LOCKED**:
   - Only if `!_rangeInvalidated`
   - Calls `Transition()` to change state
   - Logs `RANGE_LOCK_SNAPSHOT`
   - Computes breakout levels

**Key Logs**:
- `HEARTBEAT`: Periodic status updates
- `DATA_FEED_STALL`: No bars received warning
- `SLOT_GATE_DIAGNOSTIC`: Gate evaluation (diagnostic only)
- `RANGE_COMPUTE_COMPLETE`: Final range computation details
- `RANGE_LOCK_ASSERT`: Invariant verification
- `RANGE_LOCK_SNAPSHOT`: Range lock confirmation
- `RANGE_COMPUTE_FAILED`: Computation failure details

---

### 4. RANGE_LOCKED State

**Purpose**: Range is fixed, monitor for breakout signals.

**What Happens**:
- Range values are immutable (`RangeHigh`, `RangeLow`, `FreezeClose`)
- Monitors price for breakout above/below range
- Places bracket orders when breakout detected
- Manages trade lifecycle until exit

**Transition Conditions**:
- Slot ends → `DONE` state
- Trade completed → `DONE` state

---

## Range Computation Details

### `ComputeRangeRetrospectively()` Method

**Purpose**: Compute range from all buffered bars within a time window.

**Process**:
1. **Filter Bars**:
   - `bar.OpenChicago >= RangeStartChicagoTime`
   - `bar.OpenChicago < endTimeChicago` (default: `SlotTimeChicagoTime`)
   - Excludes bars outside the range window

2. **Find Range**:
   - `RangeHigh` = maximum of all `High` values
   - `RangeLow` = minimum of all `Low` values
   - `FreezeClose` = `Close` of last bar before slot time

3. **Validation**:
   - Requires at least 1 bar in window
   - Returns success/failure with reason

**Returns**:
```csharp
(bool Success, 
 decimal? RangeHigh, 
 decimal? RangeLow, 
 decimal? FreezeClose, 
 string FreezeCloseSource, 
 int BarCount, 
 string? Reason,
 DateTimeOffset? FirstBarUtc,
 DateTimeOffset? LastBarUtc,
 DateTimeOffset? FirstBarChicago,
 DateTimeOffset? LastBarChicago)
```

---

## Gap Tolerance System

**Purpose**: Detect data gaps that could invalidate range computation.

**Rules**:
- **Max Single Gap**: 3.0 minutes
- **Max Total Gaps**: 6.0 minutes  
- **Max Gap in Last 10 Minutes**: 2.0 minutes

**Process**:
- Tracks `_largestSingleGapMinutes` and `_totalGapMinutes`
- Checks on each bar arrival
- If violation → `_rangeInvalidated = true`
- Logs `GAP_TOLERANCE_VIOLATION` with details
- Prevents trading for invalidated streams

---

## Bar Source Tracking

**Purpose**: Track where bars came from for debugging and auditability.

**Sources**:
- `LIVE`: Bars from live feed (`OnBar()`)
- `BARSREQUEST`: Historical bars from NinjaTrader API
- `CSV`: Bars from file-based pre-hydration (DRYRUN only)

**Precedence**: LIVE > BARSREQUEST > CSV (for deduplication)

**Counters**:
- `_historicalBarCount`: Bars from BarsRequest/CSV
- `_liveBarCount`: Bars from live feed
- `_dedupedBarCount`: Duplicate bars filtered
- `_filteredFutureBarCount`: Future bars filtered
- `_filteredPartialBarCount`: Partial bars filtered

---

## Key Time Points

1. **RangeStartChicagoTime**: When range window begins (e.g., 07:30 for NG1)
2. **SlotTimeChicagoTime**: When range locks (e.g., 09:00 for NG1)
3. **MarketCloseUtc**: End of trading session

**Example (NG1, S1)**:
- Range Start: 07:30 Chicago
- Slot Time: 09:00 Chicago
- Range Window: 07:30 - 09:00 (90 minutes)

---

## Failure Modes

### 1. No Bars in Window
- **Symptom**: `RANGE_COMPUTE_FAILED` with reason "NO_BARS_IN_WINDOW"
- **Cause**: Insufficient historical data or data feed issues
- **Result**: Stream stuck in RANGE_BUILDING, cannot trade

### 2. Gap Violation
- **Symptom**: `GAP_TOLERANCE_VIOLATION` logged
- **Cause**: Missing bars exceed tolerance limits
- **Result**: `_rangeInvalidated = true`, stream commits with `RANGE_INVALIDATED`

### 3. Late Slot Time
- **Symptom**: `RANGE_COMPUTE_MISSED_SLOT_TIME`
- **Cause**: Range not computed before slot time
- **Result**: Attempts late computation, may succeed with partial data

### 4. Data Feed Stall
- **Symptom**: `DATA_FEED_STALL` warning
- **Cause**: No bars received for 5+ minutes
- **Result**: Warning only, doesn't invalidate range

---

## Current Logging Coverage

### ✅ Well-Logged Events:
- State transitions (`STREAM_STATE_TRANSITION`)
- Range computation (`RANGE_COMPUTE_COMPLETE`)
- Gap violations (`GAP_TOLERANCE_VIOLATION`)
- Bar source statistics (`HYDRATION_SUMMARY`)
- Heartbeats (`HEARTBEAT`)

### ⚠️ Areas Needing More Visibility:
- Bar arrival during RANGE_BUILDING (no per-bar logs)
- Incremental range updates (only logged once)
- Bar filtering details (counters but not per-bar)
- Time progression toward slot time (only diagnostic logs)
- Range computation progress (only final result logged)

---

## Recommended Logging Enhancements

See `RANGE_BUILDING_LOGGING_ENHANCEMENTS.md` for detailed suggestions.
