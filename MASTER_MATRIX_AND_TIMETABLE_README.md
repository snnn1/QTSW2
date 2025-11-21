# Master Matrix & Timetable Engine

## Overview

This system provides two key components:

1. **Master Matrix** - Merges all trades from all streams into one unified table sorted by time
2. **Timetable Engine** - Determines which trades to take today based on RS/time selection and filters

## Master Matrix

### Purpose

The Master Matrix creates a "single source of truth" by merging all trade files from all streams (ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, YM1, YM2) into one master table sorted by:
- `trade_date`
- `entry_time`
- `symbol` (Instrument)
- `stream_id`

### Features

- **Loads all streams**: Automatically finds and loads trade files from `data/analyzer_runs/`
- **Normalizes schema**: Ensures all streams have the same columns
- **Adds global columns**:
  - `global_trade_id` - Unique ID for each trade
  - `day_of_month` - Day of month (1-31)
  - `dow` - Day of week (Mon-Fri)
  - `session_index` - 1 for S1, 2 for S2
  - `is_two_stream` - True for *2 streams
  - `dom_blocked` - True if day is 4/16/30 and stream is a "2"
  - `final_allowed` - Boolean after all filters applied
- **Applies filters**: Day-of-month blocking, SCF filters, etc.

### Usage

#### Command Line

```bash
# Build master matrix for all data
python -m master_matrix.master_matrix

# Build for specific date range
python -m master_matrix.master_matrix --start-date 2024-01-01 --end-date 2024-12-31

# Build for "today" (specific date)
python -m master_matrix.master_matrix --today 2024-11-21

# Custom directories
python -m master_matrix.master_matrix --analyzer-runs-dir data/analyzer_runs --output-dir data/master_matrix
```

#### Batch Script

```batch
REM Build master matrix
batch\RUN_MASTER_MATRIX.bat

REM Build for specific date
batch\RUN_MASTER_MATRIX.bat --today 2024-11-21
```

#### Python API

```python
from master_matrix import MasterMatrix

# Initialize
matrix = MasterMatrix(analyzer_runs_dir="data/analyzer_runs")

# Build master matrix
master_df = matrix.build_master_matrix(
    start_date="2024-01-01",
    end_date="2024-12-31",
    output_dir="data/master_matrix"
)

# Or for "today"
master_df = matrix.build_master_matrix(
    specific_date="2024-11-21",
    output_dir="data/master_matrix"
)

# Access the DataFrame
print(master_df.head())
```

### Output Files

- `master_matrix_YYYYMMDD_HHMMSS.parquet` - Full backtest data
- `master_matrix_today_YYYYMMDD.parquet` - Single day data
- `master_matrix_YYYYMMDD_HHMMSS.json` - JSON version for inspection

## Timetable Engine

### Purpose

The Timetable Engine generates a simple list showing which trades to take today:
- For each stream, for each session, which time (if any) should I trade today?

### Features

- **RS-based time selection**: Uses Rolling Sum (RS) values to select best time slot
  - Win = +1 point
  - Loss = -2 points
  - BE = 0 points
  - Rolling sum over last 13 trades
- **Applies filters**:
  - Day-of-month blocking: GC2/CL2/ES2/NQ2/NG2 skip when day_of_month in {4, 16, 30}
  - SCF filters: S1 blocked if scf_s1 >= threshold, S2 blocked if scf_s2 >= threshold
  - Other global filters (configurable)
- **Outputs timetable**: Shows symbol, stream, session, selected_time, allowed status, and reason

### Usage

#### Command Line

```bash
# Generate timetable for today
python -m timetable_engine.timetable_engine

# Generate for specific date
python -m timetable_engine.timetable_engine --date 2024-11-21

# Custom SCF threshold
python -m timetable_engine.timetable_engine --date 2024-11-21 --scf-threshold 0.6
```

#### Batch Script

```batch
REM Generate timetable for today
batch\RUN_TIMETABLE_ENGINE.bat

REM Generate for specific date
batch\RUN_TIMETABLE_ENGINE.bat --date 2024-11-21
```

#### Python API

```python
from timetable_engine import TimetableEngine

# Initialize
engine = TimetableEngine(
    master_matrix_dir="data/master_matrix",
    analyzer_runs_dir="data/analyzer_runs"
)
engine.scf_threshold = 0.5  # Optional: customize threshold

# Generate timetable
timetable_df = engine.generate_timetable(trade_date="2024-11-21")

# Save timetable
parquet_file, json_file = engine.save_timetable(
    timetable_df, 
    output_dir="data/timetable"
)

# View timetable
print(timetable_df[['symbol', 'stream_id', 'session', 'selected_time', 
                   'allowed', 'reason']])
```

### Output Format

The timetable DataFrame contains:

| Column | Description |
|--------|-------------|
| `trade_date` | Trading date (YYYY-MM-DD) |
| `symbol` | Instrument symbol (ES, GC, CL, NQ, NG, YM) |
| `stream_id` | Stream identifier (ES1, ES2, GC2, etc.) |
| `session` | Session (S1 or S2) |
| `selected_time` | Selected time slot (e.g., 07:30, 09:00, 09:30, 10:00) |
| `reason` | Reason for selection/blocking (e.g., "RS_best_time", "dom_blocked_30", "scf_blocked") |
| `allowed` | True if trade is allowed, False if blocked |
| `scf_s1` | SCF value for S1 (if available) |
| `scf_s2` | SCF value for S2 (if available) |
| `day_of_month` | Day of month (1-31) |
| `dow` | Day of week (Mon-Fri) |

### Example Output

```
trade_date  symbol  stream_id  session  selected_time  allowed  reason
2024-11-21  ES      ES1        S1       09:00          True     RS_best_time
2024-11-21  ES      ES2        S2       10:00          False    dom_blocked_30
2024-11-21  GC      GC2        S2       09:30          False    dom_blocked_16
2024-11-21  NQ      NQ2        S2       10:30          True     RS_best_time
```

## Combined Usage

### Run Both Together

```bash
# Build matrix and generate timetable for specific date
python run_matrix_and_timetable.py --date 2024-11-21

# Build matrix only
python run_matrix_and_timetable.py --matrix-only --start-date 2024-01-01 --end-date 2024-12-31

# Generate timetable only
python run_matrix_and_timetable.py --timetable-only --date 2024-11-21
```

### Batch Script

```batch
REM Run both
batch\RUN_MATRIX_AND_TIMETABLE.bat --date 2024-11-21
```

## Configuration

### SCF Threshold

Default SCF threshold is `0.5`. Trades are blocked if:
- S1: `scf_s1 >= threshold`
- S2: `scf_s2 >= threshold`

To customize:

```python
engine.scf_threshold = 0.6  # Higher threshold = fewer blocks
```

### Day-of-Month Blocking

Default blocked days for "2" streams: `{4, 16, 30}`

To customize:

```python
matrix.dom_blocked_days = {4, 16, 30}  # Modify in MasterMatrix class
```

## File Structure

```
QTSW2/
├── master_matrix/
│   ├── __init__.py
│   └── master_matrix.py
├── timetable_engine/
│   ├── __init__.py
│   └── timetable_engine.py
├── run_matrix_and_timetable.py
├── batch/
│   ├── RUN_MASTER_MATRIX.bat
│   ├── RUN_TIMETABLE_ENGINE.bat
│   └── RUN_MATRIX_AND_TIMETABLE.bat
├── data/
│   ├── analyzer_runs/          # Input: Analyzer output files
│   │   ├── ES1/
│   │   ├── ES2/
│   │   ├── GC1/
│   │   └── ...
│   ├── master_matrix/          # Output: Master matrix files
│   └── timetable/              # Output: Timetable files
```

## Data Flow

```
Analyzer Output Files (data/analyzer_runs/)
    ↓
Master Matrix (merges all streams)
    ↓
Master Matrix Files (data/master_matrix/)
    ↓
Timetable Engine (calculates RS, applies filters)
    ↓
Timetable Files (data/timetable/)
```

## Notes

- **RS Calculation**: The Timetable Engine calculates RS values by loading recent analyzer output files and computing rolling sums. This matches the logic from the Sequential Processor.
- **Missing Fields**: Some fields mentioned in requirements (like `entry_price`, `exit_price`, `rs_value`, `time_bucket`) are not in the current analyzer output. Placeholder columns are created where needed.
- **Performance**: For large backtests, building the master matrix may take a few minutes. The timetable generation is fast (< 1 second).

## Troubleshooting

### No data found

- Check that analyzer output files exist in `data/analyzer_runs/`
- Verify stream directories exist (ES1, ES2, GC1, etc.)
- Check that parquet files are present in year subdirectories

### RS values are zero

- Ensure analyzer output files contain recent trade data
- Check that `Result` column contains "Win", "Loss", "BE", etc.
- Verify that `Session` column matches "S1" or "S2"

### Timetable shows all trades blocked

- Check SCF threshold setting (may be too low)
- Verify day-of-month blocking logic
- Check that filters are configured correctly



