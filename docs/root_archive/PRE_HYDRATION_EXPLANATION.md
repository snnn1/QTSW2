# Pre-Hydration Complete Explanation

**Date**: February 4, 2026  
**Purpose**: Comprehensive explanation of how pre-hydration works in the robot system

---

## What is Pre-Hydration?

**Pre-Hydration** is the process of loading historical bars into the stream's memory buffer **before** the stream can begin trading. It ensures the stream has the price history needed to:

1. **Compute the range**: Calculate high/low from bars in `[range_start, slot_time)`
2. **Detect breakouts**: Determine if price already broke out (late-start scenarios)
3. **Make trading decisions**: Calculate breakout levels and place orders

**State**: Streams start in `PRE_HYDRATION` state and must complete hydration before transitioning to `ARMED`.

---

## Pre-Hydration Lifecycle

### State Machine Flow

```
PRE_HYDRATION
    ↓ (hydration complete + transition conditions met)
ARMED
    ↓ (range start time reached)
RANGE_BUILDING
    ↓ (slot_time reached)
RANGE_LOCKED
```

### Key Flag: `_preHydrationComplete`

**Purpose**: Gate flag that must be `true` before transitioning to `ARMED`

**Set When**:
- **SIM Mode**: After BarsRequest completes (or not pending)
- **DRYRUN Mode**: After CSV file loaded (or file missing)

**Enforcement**: `HandleArmedState()` checks this flag (fail-closed if false)

---

## How Pre-Hydration Works

### Step 1: Stream Initialization

**When**: Stream is created (during `RobotEngine.Start()`)

**What Happens**:
- Stream starts in `PRE_HYDRATION` state
- `_preHydrationComplete = false` (gate closed)
- Bar buffer (`_barBuffer`) is empty

### Step 2: BarsRequest Initiation (SIM Mode)

**When**: During `OnStateChange(State.DataLoaded)` in `RobotSimStrategy`

**What Happens**:
1. **Get Time Range**: Calculate `[range_start, slot_time)` for all enabled streams
2. **Mark Pending**: Mark instrument as `BarsRequestPending` (synchronously)
3. **Queue Request**: Request historical bars in background thread (non-blocking)
4. **BarsRequest API**: Calls NinjaTrader's `BarsRequest` API with time range

**Time Range Calculation**:
- **Start**: Earliest `range_start` across all enabled streams for instrument
- **End**: Latest `slot_time` across all enabled streams (or `now` if restart)
- **Example**: RTY2 requests `[08:00, 09:30)` CT

### Step 3: Bars Arrive Asynchronously

**How Bars Arrive**:
- **BarsRequest Callback**: NinjaTrader calls back with historical bars
- **OnBar() Method**: Bars arrive via `OnBar()` callback with `isHistorical: true`
- **Bar Source**: Bars marked as `BarSource.BARSREQUEST`

**Bar Processing** (`AddBarToBuffer()`):
1. **Age Check**: Reject bars < 1 minute old (BARSREQUEST/CSV only, LIVE bypasses)
2. **Duplicate Check**: Check if bar with same timestamp exists
3. **Precedence**: If duplicate, higher precedence source wins (LIVE > BARSREQUEST > CSV)
4. **Add to Buffer**: Add bar to `_barBuffer` and track source

### Step 4: Pre-Hydration Completion Check

**When**: Every `Tick()` call while in `PRE_HYDRATION` state

**What Happens** (`HandlePreHydrationState()`):

**SIM Mode**:
```csharp
if (!_preHydrationComplete)
{
    // Check if BarsRequest is still pending
    var isPending = _engine.IsBarsRequestPending(CanonicalInstrument, utcNow) ||
                    _engine.IsBarsRequestPending(ExecutionInstrument, utcNow);
    
    if (isPending)
    {
        return; // Wait for BarsRequest to complete
    }
    
    // BarsRequest complete - mark pre-hydration complete
    _preHydrationComplete = true;
}
```

**DRYRUN Mode**:
```csharp
if (!_preHydrationComplete)
{
    // Load CSV files
    PerformPreHydration(utcNow);
    _preHydrationComplete = true; // Set after CSV loading
}
```

### Step 5: Transition to ARMED

**When**: `_preHydrationComplete == true` AND transition conditions met

**Transition Conditions** (ANY of these):
1. **Bar Count > 0**: At least one bar loaded
2. **Time Threshold**: `nowChicago >= RangeStartChicagoTime`
3. **Hard Timeout**: `nowChicago >= RangeStartChicagoTime + 1 minute` (liveness guarantee)

**Code**:
```csharp
if (_preHydrationComplete)
{
    var barCount = GetBarBufferCount();
    var nowChicago = _time.ConvertUtcToChicago(utcNow);
    
    if (barCount > 0 || nowChicago >= RangeStartChicagoTime || shouldForceTransition)
    {
        // Log HYDRATION_SUMMARY
        // Compute range retrospectively (if late-start)
        // Transition to ARMED
        Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE");
    }
}
```

---

## Bar Sources and Precedence

### Three Bar Sources

1. **LIVE** (Precedence: 0 - Highest)
   - Real-time bars from NinjaTrader live feed
   - Arrives via `OnBar()` callback
   - Always wins over historical sources
   - Used during `ARMED`, `RANGE_BUILDING`, `RANGE_LOCKED`

2. **BARSREQUEST** (Precedence: 1 - Medium)
   - Historical bars from NinjaTrader API
   - Requested during pre-hydration
   - More authoritative than CSV
   - Used for pre-hydration in SIM mode

3. **CSV** (Precedence: 2 - Lowest)
   - Historical bars from CSV files
   - Loaded during pre-hydration in DRYRUN mode
   - Fallback/supplement source

### Precedence Rule

**When duplicate bars exist** (same timestamp):
- Source with **lower enum value** (higher precedence) wins
- Example: LIVE bar replaces BARSREQUEST bar for same timestamp

---

## Range Computation During Pre-Hydration

### When Range is Computed

**During Transition**: When transitioning from `PRE_HYDRATION` to `ARMED`

**Method**: `ComputeRangeRetrospectively(utcNow, endTimeUtc: SlotTimeUtc)`

**What It Does**:
1. **Filter Bars**: Get all bars in `[RangeStartChicagoTime, SlotTimeChicagoTime)`
2. **Compute Range**: 
   - `RangeHigh = max(all_bar.High)`
   - `RangeLow = min(all_bar.Low)`
3. **Return Result**: Range values, bar count, first/last bar times

**Important**: Range is computed from **all bars in buffer**, regardless of source (LIVE, BARSREQUEST, CSV)

### Late-Start Handling

**If Late-Start** (`nowChicago > SlotTimeChicagoTime`):
1. Compute range from bars < `slot_time` (slot_time exclusive)
2. Check for missed breakout (price already broke out)
3. If breakout missed, block trading (log `LATE_START_MISSED_BREAKOUT`)

---

## Hard Timeout (Liveness Guarantee)

### Purpose

**Prevent Deadlock**: Ensure streams never get stuck in `PRE_HYDRATION` indefinitely

### Rule

**Hard Timeout**: `nowChicago >= RangeStartChicagoTime + 1 minute`

**What Happens**:
- Force transition to `ARMED` even if no bars loaded
- Log `PRE_HYDRATION_FORCED_TRANSITION` event
- Stream can still trade using live bars once range window starts

**Why Needed**:
- BarsRequest might fail or timeout
- CSV files might be missing
- Data feed might be disconnected
- System must continue operating despite missing historical data

---

## Completeness Metrics

### Metrics Logged in HYDRATION_SUMMARY

**Bar Statistics**:
- `total_bars_in_buffer`: Current bar count
- `historical_bar_count`: Bars from BARSREQUEST
- `live_bar_count`: Bars from LIVE feed
- `deduped_bar_count`: Bars that replaced existing bars
- `filtered_future_bar_count`: Bars rejected (future timestamp)
- `filtered_partial_bar_count`: Bars rejected (partial/in-progress)

**Completeness Metrics**:
- `expected_bars`: Expected bars based on time range
- `expected_full_range_bars`: Expected bars for full range window (90 for RTY2)
- `loaded_bars`: Actual bars loaded
- `completeness_pct`: Percentage of expected bars loaded

**Example (RTY2)**:
- Expected: 90 bars (08:00-09:30 = 90 minutes)
- Loaded: 57 bars (08:00-08:56)
- Completeness: 63.3% (57/90)

---

## DRYRUN vs SIM Mode

### DRYRUN Mode

**Bar Sources**: CSV files only

**Process**:
1. `PerformPreHydration()` loads CSV files from `data/snapshots/`
2. Bars parsed and added with `BarSource.CSV`
3. `_preHydrationComplete = true` after CSV loading
4. Transition when bars loaded or time threshold reached

### SIM Mode

**Bar Sources**: BarsRequest only (no CSV files)

**Process**:
1. `RequestHistoricalBarsForPreHydration()` called during `DataLoaded`
2. BarsRequest initiated in background thread (non-blocking)
3. `_preHydrationComplete = true` when BarsRequest not pending
4. Bars arrive asynchronously via `OnBar()` callback
5. Transition when bars loaded or time threshold reached

**Live Feed After Transition**:
- Once in `ARMED`, live bars arrive via `OnBar()` callback
- Live bars marked as `BarSource.LIVE` and win over BARSREQUEST bars

---

## Key Points

### 1. Pre-Hydration is Non-Blocking

**BarsRequest is asynchronous**:
- Requested in background thread
- Bars arrive via callbacks
- Stream doesn't wait for all bars before transitioning

**Transition happens when**:
- Bars loaded OR
- Time threshold reached OR
- Hard timeout forces transition

### 2. Bars Can Arrive After Transition

**BarsRequest bars can arrive after `ARMED`**:
- BarsRequest is asynchronous
- Bars continue arriving via `OnBar()` callback
- Range computation uses all bars in buffer (regardless of when they arrived)

### 3. Range Computation Uses All Available Bars

**Range computed from buffer**:
- Includes bars from all sources (LIVE, BARSREQUEST, CSV)
- Uses precedence rules (LIVE wins over duplicates)
- Computed at transition time (may not have all bars yet)

### 4. Missing Bars Are Expected

**BarsRequest limitations**:
- Can only retrieve bars that exist in NinjaTrader's database
- If bars were never recorded (data feed gap), they don't exist
- System continues with available bars (completeness metrics logged)

---

## Example: RTY2 Pre-Hydration (Feb 4, 2026)

### Timeline

1. **09:13:26 CT**: System started
2. **09:13:26 CT**: Stream initialized in `PRE_HYDRATION`
3. **09:13:26 CT**: BarsRequest requested `[08:00, 09:30)` CT
4. **09:13:26 CT**: BarsRequest returned 57 bars (08:00-08:56)
5. **09:13:26 CT**: `_preHydrationComplete = true` (BarsRequest not pending)
6. **09:13:26 CT**: Transition to `ARMED` (barCount > 0)
7. **09:13:26 CT**: Range computed: High=2674.6, Low=2652.5 (from 57 bars)

### Missing Bars

**Missing**: 27 bars from 08:57-09:13 CT

**Why Missing**:
- BarsRequest requested them
- But they don't exist in NinjaTrader's database (data feed gap)
- BarsRequest can only retrieve what exists

**Impact**:
- Range computed from 57 bars instead of 90 (63% completeness)
- Missing bars could have contained lower lows or higher highs

---

## Conclusion

**Pre-Hydration** is the process of loading historical bars before trading begins. It:

1. ✅ **Requests bars** via BarsRequest (SIM) or CSV files (DRYRUN)
2. ✅ **Buffers bars** with deduplication and precedence rules
3. ✅ **Transitions to ARMED** when bars loaded or time threshold reached
4. ✅ **Computes range** from all available bars at transition time
5. ✅ **Handles late-starts** by detecting missed breakouts
6. ✅ **Logs completeness** metrics for monitoring

**Key Limitation**: Pre-hydration can only retrieve bars that exist in the data source. If bars were never recorded (data feed gaps), they can't be retrieved.
