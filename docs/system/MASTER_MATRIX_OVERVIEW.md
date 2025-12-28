# Master Matrix Overview

## What is the Master Matrix?

The **Master Matrix** is a unified, time-ordered table that merges all trades from all trading streams (ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, YM1, YM2, etc.) into a single sorted dataset. It represents **"all trades in order"** across the entire trading system.

## Core Purpose

The Master Matrix serves as the central data structure that:
1. **Unifies all streams** - Combines trades from multiple instruments and contract months
2. **Applies sequencer logic** - Selects one optimal trade per day per stream using a points-based system
3. **Enables analysis** - Provides a single source of truth for backtesting, filtering, and performance analysis
4. **Supports the dashboard** - Powers the frontend visualization and timetable engine

## Key Features

### 1. Sequencer Logic (Time Change System)
The Master Matrix uses sophisticated **sequencer logic** to select trades:

- **Points-based selection**: Each trade result is scored (Win = +2, Loss = -2, BE = 0, NoTrade = 0)
- **Rolling 13-trade history**: Maintains a rolling window of the last 13 trades per time slot
- **Time change on loss**: When a trade loses, the system automatically switches to a better-performing time slot
- **Session management**: Trades are organized into S1 (07:30, 08:00, 09:00) and S2 (09:30, 10:00, 10:30, 11:00) sessions

### 2. Stream Filtering
Per-stream filters can be applied:
- **Exclude days of week**: Block specific weekdays (e.g., exclude Fridays)
- **Exclude days of month**: Block specific calendar days (e.g., days 4, 16, 30 for "2" streams)
- **Exclude times**: Block specific entry times (e.g., exclude "07:30", "08:00")

### 3. Data Structure
Each trade in the Master Matrix contains:
- **Trade identification**: TradeID, global_trade_id, Stream, Instrument
- **Timing**: Date, Time, EntryTime, ExitTime, Session
- **Trade details**: Direction, EntryPrice, ExitPrice, Target, Range, SL
- **Results**: Result (Win/Loss/BE/NoTrade), Profit, R (risk-reward ratio)
- **Sequencer data**: Time Change, selected_time, time_bucket, rolling points
- **Filtering data**: day_of_month, dow, final_allowed, filter_reasons
- **Session data**: session_index, is_two_stream, dom_blocked

## Architecture

The Master Matrix module has been refactored into a clean, modular structure:

```
modules/matrix/
├── master_matrix.py          # Main orchestrator class
├── stream_manager.py         # Stream discovery & filter management
├── data_loader.py            # Data loading, file I/O, parallel loading
├── sequencer_logic.py        # Time change logic, slot history tracking
├── schema_normalizer.py      # Schema normalization & column management
├── filter_engine.py          # Filtering logic & global columns
├── statistics.py             # Statistics calculation & reporting
├── file_manager.py           # File saving, path management
└── utils.py                  # Utility functions (time normalization, etc.)
```

## Data Flow

1. **Input**: Reads from `data/analyzed/` directory (analyzer output files)
   - Files organized by stream (ES1, ES2, etc.) and year
   - Format: Parquet files with all trades per stream

2. **Processing**:
   - **Load all streams**: Parallel loading of all stream data
   - **Apply sequencer logic**: Select one trade per day per stream using points system
   - **Normalize schema**: Ensure consistent columns across all streams
   - **Apply filters**: Add global columns and apply per-stream filters
   - **Sort**: Order by trade_date, entry_time, Instrument, Stream

3. **Output**: Saves to `data/master_matrix/` directory
   - Format: Both JSON and Parquet files
   - Naming: `master_matrix_YYYYMMDD_HHMMSS.json` and `.parquet`

## Usage

### Building the Master Matrix

**Via Python:**
```python
from modules.matrix import MasterMatrix

# Initialize
matrix = MasterMatrix(analyzer_runs_dir="data/analyzed")

# Build full matrix
df = matrix.build_master_matrix(
    output_dir="data/master_matrix",
    stream_filters={
        "ES1": {
            "exclude_days_of_week": ["Friday"],
            "exclude_times": ["07:30"]
        }
    }
)

# Update incrementally (add new dates only)
updated_df, stats = matrix.update_master_matrix(
    output_dir="data/master_matrix"
)
```

**Via Dashboard:**
- Use the Dashboard UI to build/update the master matrix
- Configure stream filters through the interface
- View statistics and trade data

**Via Batch Script:**
```batch
RUN_MASTER_MATRIX.bat
```

### Accessing the Data

The Master Matrix can be accessed:
- **From Dashboard Backend**: REST API endpoints at `/api/matrix/*`
- **From Python**: Load existing matrix files or use `get_master_matrix()`
- **From Frontend**: React components consume the matrix data via API

## Key Statistics

The Master Matrix calculates and logs:
- **Total trades**: Count of all trades across all streams
- **Win/Loss breakdown**: Wins, Losses, Break-Even, No Trade counts
- **Win rate**: Percentage of wins vs losses (excluding BE)
- **Profit metrics**: Total profit, average profit per trade
- **Risk-Reward ratio**: Average win / Average loss
- **Filter stats**: Allowed vs blocked trades
- **Per-stream stats**: Individual statistics for each stream

## Integration Points

### Dashboard Backend
- **API Endpoints**: `/api/matrix/build`, `/api/matrix/list`, `/api/matrix/data`
- **Real-time updates**: Can rebuild matrix on demand
- **Filter configuration**: Accepts stream filters from frontend

### Timetable Engine
- Master Matrix powers the timetable visualization
- Shows selected trades per day per stream
- Displays time changes and session information

### Analyzer Pipeline
- Master Matrix reads from analyzer output
- Analyzer must run first to generate trade data
- Supports incremental updates when new analyzer runs complete

## File Locations

- **Source Code**: `modules/matrix/`
- **Input Data**: `data/analyzed/` (organized by stream/year)
- **Output Data**: `data/master_matrix/` (JSON and Parquet files)
- **Logs**: `logs/master_matrix.log`
- **Batch Scripts**: `batch/RUN_MASTER_MATRIX.bat`, `batch/TEST_MASTER_MATRIX.bat`

## Important Notes

1. **Sequencer Accuracy**: The sequencer logic requires full historical data to maintain accurate 13-trade rolling histories. When updating incrementally, it reprocesses the last full year of data per stream.

2. **One Trade Per Day**: The sequencer selects exactly one trade per day per stream based on the points system and time change logic.

3. **Filter Application**: Filters are applied after sequencer logic, so excluded times are removed before selection, but other filters (day of week, day of month) are applied as metadata.

4. **Schema Consistency**: The schema normalizer ensures all streams have consistent columns, even if some streams have different original schemas.

5. **Performance**: The module uses parallel loading for multiple streams and efficient Parquet file format for storage.

## Example Data Structure

```json
{
  "TradeID": "c4f0671d",
  "Date": "2018-01-02T00:00:00.000",
  "Time": "07:30",
  "Instrument": "CL",
  "Stream": "CL1",
  "Session": "S1",
  "Direction": "Short",
  "Result": "Loss",
  "Profit": -0.41,
  "Time Change": "",
  "selected_time": "07:30",
  "global_trade_id": 1,
  "day_of_month": 2,
  "dow": "Tue",
  "final_allowed": true,
  "filter_reasons": "dow_filter(friday,tuesday)"
}
```

## Maintenance

- **Full Rebuild**: Use `build_master_matrix()` to rebuild from scratch
- **Incremental Update**: Use `update_master_matrix()` to add only new dates
- **Partial Rebuild**: Can rebuild specific streams only
- **Logging**: All operations are logged to `logs/master_matrix.log`

## Related Documentation

- `modules/matrix/REFACTORING_ANALYSIS.md` - Technical refactoring details
- Dashboard API documentation for matrix endpoints
- Analyzer documentation for input data format


