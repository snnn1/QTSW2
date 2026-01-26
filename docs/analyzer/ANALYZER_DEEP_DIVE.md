# Analyzer Deep Dive - Complete System Analysis

## Table of Contents
1. [System Overview](#system-overview)
2. [Architecture](#architecture)
3. [Core Components](#core-components)
4. [Data Flow](#data-flow)
5. [Trading Strategy Logic](#trading-strategy-logic)
6. [Entry Detection](#entry-detection)
7. [Trade Execution & Price Tracking](#trade-execution--price-tracking)
8. [Result Processing](#result-processing)
9. [Configuration & Instrument Management](#configuration--instrument-management)
10. [Optimizations](#optimizations)
11. [User Interface](#user-interface)
12. [Integration Points](#integration-points)
13. [Key Algorithms](#key-algorithms)
14. [Known Issues & Limitations](#known-issues--limitations)

---

## System Overview

The **Analyzer** is a comprehensive breakout trading strategy backtesting system that:
- Analyzes historical market data (OHLC bars) to simulate trading strategies
- Implements a range-based breakout strategy with multiple time slots
- Calculates Maximum Favorable Excursion (MFE) and trade outcomes
- Supports multiple instruments (ES, NQ, YM, CL, NG, GC, and micro-futures)
- Provides detailed trade analysis with break-even logic and stop-loss management

### Key Features
- **Modular Architecture**: Separated into logical components (range detection, entry logic, price tracking, etc.)
- **Multi-Session Support**: Handles S1 (overnight) and S2 (regular hours) sessions
- **Multiple Time Slots**: Analyzes trades at 07:30, 08:00, 09:00, 09:30, 10:00, 10:30, 11:00
- **Break-Even Logic**: T1 trigger at 65% of target moves stop to break-even
- **MFE Calculation**: Tracks maximum favorable movement until next day same slot
- **Realistic Execution**: Uses actual historical data to determine which level (target/stop) hits first

---

## Architecture

### High-Level Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Streamlit UI (app.py)                     │
│  - File selection, slot selection, year filtering            │
│  - Results display, data inspection, validation             │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                    Engine (engine.py)                        │
│  - Main orchestration: run_strategy()                        │
│  - Coordinates all logic modules                            │
└──────────────────────┬──────────────────────────────────────┘
                       │
        ┌──────────────┼──────────────┐
        ▼              ▼              ▼
┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│ Range Logic  │ │ Entry Logic │ │ Price Track  │
│              │ │              │ │              │
│ - Calculate  │ │ - Detect     │ │ - Execute    │
│   ranges     │ │   breakouts  │ │   trades     │
│ - Build slot │ │ - Determine  │ │ - Track MFE  │
│   ranges     │ │   direction  │ │ - Break-even │
└──────────────┘ └──────────────┘ └──────────────┘
        │              │              │
        └──────────────┼──────────────┘
                       ▼
┌─────────────────────────────────────────────────────────────┐
│                    Result Processing                         │
│  - Format results, calculate profits                         │
│  - Add NoTrade entries, sort and deduplicate                 │
└─────────────────────────────────────────────────────────────┘
```

### Module Structure

```
modules/analyzer/
├── analyzer_app/
│   └── app.py                    # Streamlit UI
├── breakout_core/
│   ├── engine.py                  # Main strategy runner
│   ├── integrated_engine.py       # Alternative modular engine
│   ├── config.py                  # Configuration constants
│   └── utils.py                   # Utility functions
├── logic/
│   ├── config_logic.py           # Configuration management
│   ├── range_logic.py             # Range detection
│   ├── entry_logic.py             # Entry detection
│   ├── price_tracking_logic.py    # Trade execution & MFE
│   ├── result_logic.py            # Result processing
│   ├── loss_logic.py              # Stop loss management
│   ├── instrument_logic.py        # Instrument-specific logic
│   ├── time_logic.py              # Time calculations
│   ├── validation_logic.py        # Data validation
│   └── debug_logic.py             # Debug output
└── optimizations/
    ├── optimized_engine_integration.py
    ├── algorithm_optimizer.py
    ├── caching_optimizer.py
    └── data_processing_optimizer.py
```

---

## Core Components

### 1. Engine (`breakout_core/engine.py`)

**Main Function**: `run_strategy(df, rp, debug=False)`

**Responsibilities**:
- Orchestrates the entire analysis process
- Initializes all logic managers
- Builds slot ranges for all enabled sessions/time slots
- Processes each range to generate trade results
- Handles NoTrade entries for days with no trades

**Key Flow**:
1. Validate inputs (dataframe and run parameters)
2. Filter data by instrument
3. Build slot ranges using `RangeDetector`
4. For each range:
   - Detect entry using `EntryDetector`
   - Execute trade using `PriceTracker`
   - Calculate profit and create result row
5. Process results and add NoTrade entries
6. Return sorted DataFrame

### 2. Range Detection (`logic/range_logic.py`)

**Class**: `RangeDetector`

**Key Methods**:
- `calculate_range()`: Calculates high/low/freeze_close for a specific date/slot
- `build_slot_ranges()`: Builds all slot ranges for enabled sessions

**Range Calculation Logic**:
- **Session S1**: Range from 02:00 to slot end time (07:30, 08:00, or 09:00)
- **Session S2**: Range from 08:00 to slot end time (09:30, 10:00, 10:30, or 11:00)
- **Range High**: Maximum `high` in the range period
- **Range Low**: Minimum `low` in the range period
- **Freeze Close**: Last bar's `close` price at the slot end time

**Timezone Handling**:
- Data is expected to be in `America/Chicago` timezone
- Handles timezone-aware and naive timestamps
- Normalizes timezones for consistent comparison

### 3. Entry Detection (`logic/entry_logic.py`)

**Class**: `EntryDetector`

**Key Method**: `detect_entry()`

**Entry Logic**:
1. **Immediate Entry Check**:
   - If `freeze_close >= brk_long` → Immediate Long entry
   - If `freeze_close <= brk_short` → Immediate Short entry
   - If both → Choose closer breakout level

2. **Post-Range Breakout Detection**:
   - Find first bar where `high >= brk_long` (Long breakout)
   - Find first bar where `low <= brk_short` (Short breakout)
   - Filter out breakouts after market close (16:00)
   - First valid breakout wins

3. **Breakout Levels**:
   - `brk_long = range_high + tick_size` (1 tick above range)
   - `brk_short = range_low - tick_size` (1 tick below range)

**Return Value**: `EntryResult` with:
- `entry_direction`: "Long", "Short", "NoTrade", or None
- `entry_price`: Breakout level price
- `entry_time`: Timestamp of entry
- `immediate_entry`: Boolean flag

### 4. Price Tracking & Trade Execution (`logic/price_tracking_logic.py`)

**Class**: `PriceTracker`

**Key Method**: `execute_trade()`

**Execution Flow**:

1. **Initialize Tracking**:
   - Set initial stop loss (3× target max, or range-based)
   - Calculate T1 threshold (65% of target)
   - Get MFE end time (next day same slot)

2. **MFE Calculation**:
   - Track maximum favorable movement until:
     - Next day same slot, OR
     - Original stop loss is hit (stops MFE tracking)
   - Update peak when new maximum is reached

3. **Price Tracking Loop**:
   - For each bar after entry:
     - Check T1 trigger (65% of target reached)
     - If T1 triggered: Move stop to break-even (1 tick below entry)
     - Use `_simulate_intra_bar_execution()` to determine which level hits first
     - Check time expiry (next day same slot + 1 minute)

4. **Intra-Bar Execution** (`_simulate_intra_bar_execution()`):
   - Uses actual historical OHLC data
   - Determines if target or stop hits first within a bar
   - For both possible: Uses close price proximity to determine winner
   - Returns execution result with exit price and reason

5. **Result Classification**:
   - **Win**: Target hit → Full target profit
   - **BE**: T1 triggered + stop hit → 1 tick loss
   - **Loss**: Stop hit without T1 → Actual loss
   - **TIME**: Time expired AND trade closed → Actual PnL at expiry
   - **OPEN**: Trade still open (exit_time = NaT) → Current PnL

**MFE End Time Calculation**:
- Regular day: Next day same slot
- Friday: Monday same slot (3 days ahead)
- MFE tracking continues even after trade exits

### 5. Stop Loss Management (`logic/loss_logic.py`)

**Class**: `LossManager`

**Initial Stop Loss**:
- **Range-Based** (preferred):
  - Long: `range_low`
  - Short: `range_high`
  - Capped at 3× target maximum
- **Fallback**: 3× target distance from entry

**T1 Adjustment**:
- When 65% of target is reached:
  - Move stop to break-even (1 tick below entry for Long, 1 tick above for Short)
  - Result: If stop hit after T1 → BE (1 tick loss)

**Stop Loss Types**:
- `INITIAL`: Original stop loss
- `T1_ADJUSTED`: Break-even stop after T1 trigger
- `TRAILING`: (Not currently used)
- `BREAK_EVEN`: (Same as T1_ADJUSTED)

### 6. Result Processing (`logic/result_logic.py`)

**Class**: `ResultProcessor`

**Key Methods**:
- `create_result_row()`: Creates standardized result dictionary
- `process_results()`: Processes list of results into DataFrame

**Result Columns**:
- `Date`, `Time`, `EntryTime`, `ExitTime`
- `Target`, `Peak`, `Direction`, `Result`
- `Range`, `Stream`, `Instrument`, `Session`, `Profit`

**Processing Steps**:
1. Create DataFrame from result rows
2. Convert Date to datetime for sorting
3. Add ranking for deduplication (Win > BE > Loss > TIME)
4. Sort by Date, Time, Target, Rank, Peak
5. Deduplicate (keep first occurrence)
6. Add NoTrade entries for missing combinations
7. Final sort and cleanup

---

## Data Flow

### Input Data Format

**Expected DataFrame Columns**:
- `timestamp`: Datetime (timezone-aware, America/Chicago)
- `open`, `high`, `low`, `close`: Numeric prices
- `instrument`: String (ES, NQ, YM, etc.)

**Data Source**:
- Parquet files in `data/processed/` directory
- Generated by Translator from raw CSV files
- Already in correct timezone (Chicago)

### Processing Flow

```
1. Load Parquet File
   ↓
2. Filter by Instrument
   ↓
3. Build Slot Ranges
   ├─ For each date in data
   ├─ For each enabled session (S1/S2)
   └─ For each enabled slot time
       └─ Calculate range (high/low/freeze_close)
   ↓
4. Process Each Range
   ├─ Detect Entry (breakout or immediate)
   ├─ Calculate Target & Stop Loss
   ├─ Execute Trade
   │  ├─ Track MFE (until next day same slot)
   │  ├─ Check T1 trigger (65% of target)
   │  ├─ Adjust stop to break-even if T1 triggered
   │  └─ Determine exit (target/stop/time)
   └─ Calculate Profit
   ↓
5. Process Results
   ├─ Format result rows
   ├─ Add NoTrade entries
   └─ Sort and deduplicate
   ↓
6. Output DataFrame
   └─ Save to parquet/CSV
```

### Output Format

**Result DataFrame**:
- One row per trade/slot combination
- Includes NoTrade entries (if enabled)
- Sorted by Date and Time (earliest first)
- Descriptive filename: `{instrument}_{date_range}_{trades}trades_{winrate}winrate_{profit}profit_{timestamp}`

---

## Trading Strategy Logic

### Strategy Overview

**Breakout Strategy**:
1. Calculate range from session start to slot end time
2. Set breakout levels: 1 tick above/below range
3. Enter on first breakout (Long or Short)
4. Target: Base target for instrument (e.g., 10 pts for ES)
5. Stop Loss: Range-based, capped at 3× target
6. T1 Trigger: At 65% of target → Move stop to break-even
7. Exit: Target hit, stop hit, or time expiry

### Session Definitions

**S1 (Overnight Session)**:
- Start: 02:00 Chicago time
- Slots: 07:30, 08:00, 09:00
- Range: 02:00 to slot end time

**S2 (Regular Hours Session)**:
- Start: 08:00 Chicago time
- Slots: 09:30, 10:00, 10:30, 11:00
- Range: 08:00 to slot end time

### Trade Days

- Default: Monday-Friday (0-4)
- Configurable via `RunParams.trade_days`
- Weekend data is skipped

### Market Close Handling

- Market close: 16:00 Chicago time
- Breakouts after 16:00 are ignored (NoTrade)
- Trades can continue after market close for expiry/MFE calculation

---

## Entry Detection

### Immediate Entry Conditions

If the freeze close price (last bar at slot end) already breaks out:
- `freeze_close >= brk_long` → Immediate Long entry at `brk_long`
- `freeze_close <= brk_short` → Immediate Short entry at `brk_short`
- Both conditions → Choose closer breakout level

### Post-Range Breakout

After the slot end time, scan for first breakout:
1. Find first bar where `high >= brk_long` (Long)
2. Find first bar where `low <= brk_short` (Short)
3. Filter out breakouts after 16:00 (market close)
4. First valid breakout wins
5. If no valid breakouts → NoTrade

### Breakout Level Calculation

```python
tick_size = instrument_manager.get_tick_size(instrument)
brk_long = range_high + tick_size   # 1 tick above range
brk_short = range_low - tick_size    # 1 tick below range
```

---

## Trade Execution & Price Tracking

### Execution Method

Uses **Method 1: Actual Historical Data Analysis**:
- Analyzes real OHLC bar data
- Determines which level (target/stop) is hit first
- Most accurate representation without tick data

### Intra-Bar Execution Logic

When both target and stop are possible in the same bar:

1. **Check Open Price**:
   - If open >= target (Long) or open <= target (Short) → Target hits immediately
   - If open <= stop (Long) or open >= stop (Short) → Stop hits immediately

2. **Both Possible**:
   - Calculate distance from close to target and stop
   - Closer distance wins (more likely hit first)

3. **Only One Possible**:
   - That level is hit

### T1 Trigger Logic

**T1 Threshold**: 65% of target (always)

**Example** (ES with 10 pt target):
- T1 threshold = 6.5 points
- When favorable movement >= 6.5 points:
  - Move stop to break-even (1 tick below entry)
  - If stop hit after T1 → BE result (1 tick loss)

**T1 Detection**:
- Uses price tracking (not just peak)
- Checks each bar for favorable movement >= T1 threshold
- Adjusts stop loss immediately when triggered

### MFE Calculation

**MFE End Time**:
- Regular day: Next day same slot
- Friday: Monday same slot (3 days ahead)

**MFE Tracking**:
- Tracks maximum favorable movement from entry
- Stops tracking when original stop loss is hit
- Continues until MFE end time (even after trade exits)

**Peak Calculation**:
- For Long: `peak = max(high - entry_price)` across all bars
- For Short: `peak = max(entry_price - low)` across all bars
- Updated whenever new maximum is reached

### Profit Calculation

**Win** (Target Hit):
- Profit = Full target points (e.g., 10 pts for ES)

**BE** (T1 Triggered + Stop Hit):
- Profit = -1 tick (e.g., -0.25 for ES)

**Loss** (Stop Hit Without T1):
- Profit = Actual PnL (entry_price - exit_price for Long)

**TIME** (Time Expired):
- Profit = Actual PnL at expiry close price

**Micro-Futures Scaling**:
- Micro-futures (MES, MNQ, etc.) are 1/10th size
- Profit is scaled accordingly
- Display shows as if base instrument (for comparison)

---

## Result Processing

### Result Classification

**Result Types**:
- `Win`: Target hit → Full target profit
- `BE`: T1 triggered + stop hit → 1 tick loss
- `Loss`: Stop hit without T1 → Actual loss
- `TIME`: Time expired AND trade closed (exit_time != NaT) → Actual PnL at expiry
- `OPEN`: Trade still open (exit_time = NaT) → Current PnL
- `NoTrade`: No entry occurred

### NoTrade Entries

**When Added**:
- For each slot range that had no trade entry
- Only if `write_no_trade_rows=True` in RunParams

**NoTrade Row**:
- Date, Time, Target, Direction="NA", Result="NoTrade"
- Peak=0, Profit=0, Range=actual_range_size

### Deduplication

**Deduplication Logic**:
- Groups by: Date, Time, Target, Direction, Session, Instrument
- Keeps first occurrence (highest rank: Win > BE > Loss > TIME > OPEN)
- Removes duplicates from same slot/date combination

### Sorting

**Primary Sort**:
1. Date (earliest first)
2. Time (earliest first)
3. Target (ascending)
4. Rank (Win > BE > Loss > TIME)
5. Peak (descending)

---

## Configuration & Instrument Management

### Instrument Configuration

**Supported Instruments**:
- Regular: ES, NQ, YM, CL, NG, GC
- Micro: MES, MNQ, MYM, MCL, MNG, MGC
- Special: MINUTEDATAEXPORT (treated as ES)

**Instrument Properties**:

| Instrument | Tick Size | Base Target | Target Ladder |
|------------|-----------|-------------|---------------|
| ES         | 0.25      | 10          | 10,15,20,25,30,35,40 |
| NQ         | 0.25      | 50          | 50,75,100,125,150,175,200 |
| YM         | 1.0       | 100         | 100,150,200,250,300,350,400 |
| CL         | 0.01      | 0.5         | 0.5,0.75,1.0,1.25,1.5,1.75,2.0 |
| NG         | 0.001     | 0.05        | 0.05,0.075,0.10,0.125,0.15,0.175,0.20 |
| GC         | 0.1       | 5           | 5,7.5,10,12.5,15,17.5,20 |

**Micro-Futures**:
- Same target ladder as base instrument
- Profit scaled by 1/10th
- Display shows as base instrument equivalent

### Run Parameters (`RunParams`)

**Key Parameters**:
- `instrument`: Trading instrument
- `enabled_sessions`: List of sessions (["S1", "S2"])
- `enabled_slots`: Dict of slots per session
  - Example: `{"S1": ["07:30", "08:00"], "S2": ["09:30", "10:00"]}`
- `trade_days`: List of day indices (0=Mon, 4=Fri)
- `write_no_trade_rows`: Include NoTrade entries (default: True)
- `write_setup_rows`: Include Setup entries (default: False)
- `same_bar_priority`: "STOP_FIRST" or "TP_FIRST" (legacy)

### Configuration Manager (`ConfigManager`)

**Responsibilities**:
- Provides instrument-specific configurations
- Validates run parameters
- Manages slot configurations
- Calculates target profits (with micro-futures scaling)

---

## Optimizations

### Optimization Modules

Located in `modules/analyzer/optimizations/`:

1. **Optimized Engine Integration**:
   - Parallel processing
   - Caching for repeated calculations
   - Algorithm optimizations

2. **Algorithm Optimizer**:
   - Vectorized operations
   - Efficient data structures
   - Reduced computational complexity

3. **Caching Optimizer**:
   - Cache range calculations
   - Cache instrument configurations
   - Reduce redundant computations

4. **Data Processing Optimizer**:
   - Optimized DataFrame operations
   - Memory-efficient data types
   - Batch processing

### UI Performance Monitor

- Tracks data loading time
- Monitors processing time
- Memory usage tracking
- Performance dashboard (optional)

---

## User Interface

### Streamlit App (`analyzer_app/app.py`)

**Features**:
1. **File Selection**:
   - Lists parquet files in `data/processed/`
   - Extracts instrument from filename

2. **Year Filtering**:
   - Multi-select years to analyze
   - Filters data before processing

3. **Slot Selection**:
   - Checkboxes for each time slot
   - "Select All" option
   - Session-aware (shows S1/S2 labels)

4. **Configuration**:
   - Debug Mode: Detailed output
   - Inspect Data: View raw data for specific date/time
   - Validate Data: Run data quality checks
   - Export Options: Save CSV file

5. **Results Display**:
   - Summary metrics (total trades, win rate, profit)
   - Results by level (target breakdown)
   - Detailed results table (first 100 rows)
   - Download CSV button

6. **Data Inspection**:
   - Select date and time slot
   - View raw OHLC data
   - See range calculation details
   - Check breakout levels

7. **Data Validation**:
   - Basic statistics
   - Missing values check
   - Duplicate timestamps
   - OHLC relationship validation
   - Range analysis for specific dates

### Output Files

**Location**: `data/manual_analyzer_runs/{date}/`

**Filename Format**:
```
{instruments}_{date_range}_{trades}trades_{winrate}winrate_{profit}profit_{timestamp}
```

**Example**:
```
ES_2025-01-02_to_2025-11-27_1633trades_45winrate_-9profit_20251128_163300.parquet
```

**File Types**:
- Parquet: Always saved (binary, efficient)
- CSV: Optional (with descriptive header)

---

## Integration Points

### Data Pipeline Integration

**Input**:
- Parquet files from Translator (`data/processed/`)
- Format: OHLC bars with timestamp and instrument

**Output**:
- Parquet/CSV files to `data/manual_analyzer_runs/` (manual runs)
- Or `data/analyzer_temp/` (automated runs)

### Automation Integration

**Scheduler Integration** (`automation/run_pipeline_standalone.py` → orchestrator):
- Runs analyzer after translator completes
- Processes all instruments in parallel
- Monitors for completion
- Logs events to structured event log

**Script Integration**:
- `tools/run_analyzer_parallel.py`: Parallel processing script
- `modules/analyzer/scripts/run_data_processed.py`: CLI runner

### Data Merger Integration

**After Analyzer**:
- Data Merger combines analyzer results
- Creates master matrix with all instruments
- Aggregates statistics

---

## Key Algorithms

### 1. Range Calculation Algorithm

```python
def calculate_range(df, date, time_label, session):
    # Get session start time
    start_time = session_start[session]  # "02:00" or "08:00"
    end_time = time_label  # e.g., "08:00"
    
    # Filter data for range period
    range_data = df[(df["timestamp"] >= start_ts) & (df["timestamp"] < end_ts)]
    
    # Calculate range
    range_high = range_data["high"].max()
    range_low = range_data["low"].min()
    range_size = range_high - range_low
    freeze_close = range_data.iloc[-1]["close"]
    
    return RangeResult(range_high, range_low, range_size, freeze_close, ...)
```

### 2. Entry Detection Algorithm

```python
def detect_entry(df, range_result, brk_long, brk_short, freeze_close, end_ts):
    # Check immediate entry
    if freeze_close >= brk_long:
        return EntryResult("Long", brk_long, end_ts, immediate=True)
    if freeze_close <= brk_short:
        return EntryResult("Short", brk_short, end_ts, immediate=True)
    
    # Find first breakout after range period
    post = df[df["timestamp"] >= end_ts]
    market_close = end_ts.replace(hour=16, minute=0)
    
    long_breakout = post[post["high"] >= brk_long]
    short_breakout = post[post["low"] <= brk_short]
    
    # Filter out after market close
    long_time = long_breakout["timestamp"].min() if not long_breakout.empty else None
    if long_time and long_time > market_close:
        long_time = None
    
    # First valid breakout wins
    if long_time and (not short_time or long_time < short_time):
        return EntryResult("Long", brk_long, long_time, immediate=False)
    # ... similar for short
```

### 3. Intra-Bar Execution Algorithm

```python
def _simulate_intra_bar_execution(bar, entry_price, direction, target_level, stop_loss):
    high, low, open_price, close_price = bar["high"], bar["low"], bar["open"], bar["close"]
    
    # Check if both are possible
    if direction == "Long":
        target_possible = high >= target_level
        stop_possible = low <= stop_loss
    else:
        target_possible = low <= target_level
        stop_possible = high >= stop_loss
    
    # If both possible, use close price proximity
    if target_possible and stop_possible:
        target_distance = abs(close_price - target_level)
        stop_distance = abs(close_price - stop_loss)
        
        if target_distance <= stop_distance:
            return {"target_hit": True, "exit_price": target_level}
        else:
            return {"stop_hit": True, "exit_price": stop_loss}
    
    # Only one possible
    if target_possible:
        return {"target_hit": True, "exit_price": target_level}
    if stop_possible:
        return {"stop_hit": True, "exit_price": stop_loss}
    
    return None  # Neither hit
```

### 4. MFE Calculation Algorithm

```python
def calculate_mfe(mfe_bars, entry_time, entry_price, direction, original_stop_loss):
    max_favorable = 0.0
    peak_time = entry_time
    peak_price = entry_price
    
    for bar in mfe_bars:
        high, low = bar["high"], bar["low"]
        
        # Check if original stop loss hit (stops MFE tracking)
        if direction == "Long" and low <= original_stop_loss:
            break
        if direction == "Short" and high >= original_stop_loss:
            break
        
        # Calculate favorable movement
        if direction == "Long":
            current_favorable = high - entry_price
        else:
            current_favorable = entry_price - low
        
        # Update peak
        if current_favorable > max_favorable:
            max_favorable = current_favorable
            peak_time = bar["timestamp"]
            peak_price = high if direction == "Long" else low
    
    return {"peak": max_favorable, "peak_time": peak_time, "peak_price": peak_price}
```

### 5. T1 Trigger Algorithm

```python
def check_t1_trigger(current_favorable, target_pts):
    t1_threshold = target_pts * 0.65  # 65% of target
    
    if current_favorable >= t1_threshold:
        # Adjust stop to break-even
        tick_size = get_tick_size(instrument)
        if direction == "Long":
            new_stop = entry_price - tick_size  # 1 tick below entry
        else:
            new_stop = entry_price + tick_size  # 1 tick above entry
        return True, new_stop
    
    return False, original_stop
```

---

## Summary

The Analyzer is a sophisticated breakout trading strategy backtesting system with:

- **Modular Architecture**: Separated into logical components for maintainability
- **Realistic Execution**: Uses actual historical data to determine trade outcomes
- **Comprehensive Tracking**: MFE calculation, break-even logic, stop-loss management
- **Multi-Instrument Support**: Handles regular and micro-futures with proper scaling
- **Flexible Configuration**: Multiple sessions, time slots, and trade day filters
- **User-Friendly UI**: Streamlit interface with data inspection and validation
- **Production Ready**: Integrated with automation pipeline, logging, and error handling

The system provides accurate backtesting results by simulating realistic trade execution based on actual historical price movements, making it a valuable tool for strategy development and analysis.

---

## Design Decisions

### Intentionally Excluded Features

**Slot Switching**:
- Not included in analyzer by design
- All enabled slots are processed independently
- Keeps analyzer simple and focused on core strategy analysis

**Dynamic Target Changes**:
- Not included in analyzer by design
- All trades use the base target (first level of target ladder)
- Maintains consistent analysis across all trades

**Note**: These features are intentionally excluded to keep the analyzer focused on core breakout strategy analysis. For advanced features, use the sequential processor.

### Timezone Handling

- **Timezone is handled by the translator** - data arrives in correct format (America/Chicago)
- Analyzer includes defensive normalization to handle edge cases
- No timezone conversion needed in normal operation

### Performance Considerations

- Large datasets should be filtered by year before processing
- Memory usage can be high for very large datasets
- Consider using optimizations module for parallel processing

