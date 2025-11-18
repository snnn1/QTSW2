# Output File Structures Documentation

## SECTION 1 — Daily Analyzer Output

### File Structure

The analyzer writes daily output files with the following structure:

#### Columns (Required for each trade/stream/level):
- **Date** (string): Date in format `YYYY-MM-DD`
- **Time** (string): Time slot in format `HH:MM` (e.g., `07:30`, `08:00`, `09:00`)
- **Target** (integer/float): Target points for the trade
- **Peak** (float): Maximum favorable excursion (MFE) reached
- **Direction** (string): Trade direction (`Long`, `Short`, or `NA` for NoTrade)
- **Result** (string): Trade result (`Win`, `Loss`, `BE`, `NoTrade`, `TIME`)
- **Range** (float): Range size detected for the time slot
- **Stream** (string): Stream identifier (e.g., `ES1`, `ES2`, `NQ1`, `NQ2`)
- **Instrument** (string): Instrument symbol (e.g., `ES`, `NQ`, `YM`, `CL`, `NG`, `GC`)
- **Session** (string): Session identifier (`S1` or `S2`)
- **Profit** (float): Profit/loss in points

#### File Format:
- **CLI Script (`run_data_processed.py`)**: 
  - **ONLY Parquet** (`.parquet`) - compressed with Snappy compression
  - No CSV output option in CLI script
  - Line 181 & 224: `res.to_parquet(out_path, index=False, compression='snappy')`
  
- **GUI App (`analyzer_app/app.py`)**:
  - **Parquet** (`.parquet`) - ALWAYS saved (primary format)
  - **CSV** (`.csv`) - Optional/user choice (saved if `save_csv` checkbox is enabled)
  - Line 648: `final_copy.to_parquet(parquet_path, index=False)` - always executed
  - Line 651-667: CSV saved only if `save_csv` is True

#### File Organization:
- **Location**: `data/analyzer_runs/`
- **Naming Pattern**: `breakout_{instrument}_{date_range}_{total_trades}trades_{win_rate}winrate_{total_profit}profit.parquet`
  - Example: `breakout_ES_2025-11-14_to_2025-11-14_7trades_57winrate_39profit.parquet`
- **Archive Folder**: `data/analyzer_runs/archived/` (files moved here after processing)
- **Summary Files**: `data/analyzer_runs/summaries/{filename}_SUMMARY.txt`

#### File Per Instrument/Stream:
- **One file per analyzer run** - Contains all streams and instruments analyzed in that run
- **NOT one file per stream** - All streams are combined in a single file
- **NOT one file per instrument** - All instruments are combined in a single file (if multiple instruments analyzed)

#### Example Output Structure:
```python
{
    'Date': '2025-11-14',
    'Time': '07:30',
    'Target': 10,
    'Peak': 10.5,
    'Direction': 'Short',
    'Result': 'Win',
    'Range': 73.5,
    'Stream': 'ES1',
    'Instrument': 'ES',
    'Session': 'S1',
    'Profit': 10.0
}
```

---

## SECTION 2 — Daily Sequencer Output

### File Structure

The sequencer writes daily output files with the following structure:

#### Core Columns (Required for each stream and time window):
- **Date** (string): Date in format `YYYY-MM-DD`
- **Day of Week** (string): Day abbreviation (`Mon`, `Tue`, `Wed`, `Thu`, `Fri`)
- **Stream** (string): Stream identifier (e.g., `ES1`, `ES2`)
- **Time** (string): Time slot in format `HH:MM`
- **Target** (integer/float): Target points for the trade
- **Range** (float): Range size detected
- **SL** (float): Stop loss value (3x target, capped at Range)
- **Profit** (float): Profit/loss in points
- **Peak** (float): Maximum favorable excursion (MFE)
- **Direction** (string): Trade direction
- **Result** (string): Trade result (`Win`, `Loss`, `BE`, `NoTrade`, `TIME`)
- **Revised Score** (string): Result after applying day-of-week/month exclusions and loss recovery mode
- **Day** (integer): Sequential day number
- **Time Reason** (string): Explanation for time slot selection/stay
- **Time Change** (string): Time change notation (e.g., `08:00→09:00` or empty if no change)
- **Profit ($)** (float): Profit in dollars (points × contract value)
- **Revised Profit ($)** (float): Revised profit in dollars

#### Dynamic Time Slot Columns:
For each available time slot in the data, the sequencer adds:
- **`{TIME} Points`** (float): Daily points score for that time slot (e.g., `09:00 Points`)
- **`{TIME} Rolling`** (float): Rolling 13-trade sum for that time slot (e.g., `09:00 Rolling`)

Example: If data contains times `07:30`, `08:00`, `09:00`, `09:30`, `10:00`, `10:30`, `11:00`, columns would include:
- `07:30 Points`, `07:30 Rolling`
- `08:00 Points`, `08:00 Rolling`
- `09:00 Points`, `09:00 Rolling`
- `09:30 Points`, `09:30 Rolling`
- `10:00 Points`, `10:00 Rolling`
- `10:30 Points`, `10:30 Rolling`
- `11:00 Points`, `11:00 Rolling`

#### Time Windows Representation:
- **7 Time Windows**: The sequencer tracks performance across all available time slots dynamically
- **Time windows are represented** as separate columns for each time slot (`{TIME} Points` and `{TIME} Rolling`)
- **NOT stored as a single "7 windows" column** - each window is a separate column pair

#### Best 2 Times Storage:
- **Best times are NOT explicitly stored** in separate columns
- **Best times are calculated** from the rolling sums (`{TIME} Rolling` columns)
- **The sequencer selects the best performing time slot** based on highest rolling sum when a time change occurs
- **Current time slot** is indicated by the `Time` column value
- **Time change logic** compares current slot's rolling sum vs. best other slot's rolling sum

#### File Format:
- **CLI Script (`sequential_processor.py`)**: 
  - **Parquet** (`.parquet`) - Primary format, compressed with Snappy compression
  - **CSV** (`.csv`) - Also saved for compatibility
  - Line 1236+: `results.to_parquet(parquet_filename, index=False, compression='snappy')`
  - Line 1240+: `results.to_csv(csv_filename, index=False)`
  
- **GUI App (`sequential_processor_app.py`)**:
  - **Parquet** (`.parquet`) - Primary format, always saved
  - **CSV** (`.csv`) - Also saved for compatibility
  - Both formats saved when user clicks "Save Results"
  
- **Location**: `data/sequencer_runs/`
- **Naming Pattern**: `sequential_run_{timestamp}.parquet` and `sequential_run_{timestamp}.csv`
  - Example: `sequential_run_20250120_123456.parquet` and `sequential_run_20250120_123456.csv`

#### File Organization:
- **One file per sequencer run** - Contains all streams processed in that run
- **NOT one file per stream** - All streams are combined in a single file
- **NOT one file per instrument** - All instruments are combined in a single file (if multiple instruments processed)
- **Archive Folder**: `data/sequencer_runs/archived/` (files moved here after processing)

---

## SECTION 3 — File & Folder Organization

### Folder Structure for Temporary Daily Analyzer Output

```
data/
├── analyzer_runs/                    # Daily analyzer output (temporary)
│   ├── breakout_{instrument}_{details}.parquet
│   ├── breakout_{instrument}_{details}.csv        # Optional
│   ├── summaries/                   # Summary files
│   │   └── {filename}_SUMMARY.txt
│   └── archived/                   # Archived after processing
│       └── breakout_{instrument}_{details}.parquet
│
├── sequencer_runs/                  # Daily sequencer output (temporary)
│   ├── sequential_run_{timestamp}.csv
│   └── archived/                   # Archived after processing
│       └── sequential_run_{timestamp}.csv
│
├── processed/                       # Processed market data (input to analyzer)
│   ├── {instrument}_{year}.parquet
│   └── archived/                   # Archived after processing
│
└── raw/                             # Raw data from NinjaTrader
    ├── MinuteDataExport_{instrument}_{timestamp}_UTC.csv
    ├── logs/                        # Signal files
    └── archived/                    # Archived after processing
```

### Temporary vs Permanent Storage

#### Analyzer Output (`data/analyzer_runs/`):
- **Purpose**: Temporary daily output files
- **Lifecycle**: 
  1. Created during daily analyzer run
  2. Used as input to sequencer (sequencer reads Parquet files)
  3. Archived to `archived/` subfolder after sequencer completes
- **File Types**: 
  - **CLI**: Parquet only (no CSV option)
  - **GUI**: Parquet (always) + CSV (optional/user choice) + Summary TXT files

#### Sequencer Output (`data/sequencer_runs/`):
- **Purpose**: Temporary daily output files
- **Lifecycle**:
  1. Created during daily sequencer run
  2. Used for analysis/reporting
  3. Archived to `archived/` subfolder after analysis completes
- **File Types**: Parquet (primary) + CSV (for compatibility)
- **Consistency**: Now matches analyzer output format (Parquet primary)

### File Naming Conventions

#### Analyzer Files:
```
breakout_{instrument}_{date_range}_{total_trades}trades_{win_rate}winrate_{total_profit}profit.parquet
```
- Example: `breakout_ES_2025-11-14_to_2025-11-14_7trades_57winrate_39profit.parquet`

#### Sequencer Files:
```
sequential_run_{YYYYMMDD}_{HHMMSS}.parquet
sequential_run_{YYYYMMDD}_{HHMMSS}.csv
```
- Example: `sequential_run_20250120_123456.parquet` (primary)
- Example: `sequential_run_20250120_123456.csv` (compatibility)

### Archive Strategy

- **When**: After downstream processing completes
- **Where**: Move to `archived/` subfolder within same directory
- **Why**: Keep recent files accessible, archive old files for storage management
- **Automation**: Pipeline scheduler handles archiving automatically

---

## Notes & Clarifications

### Missing Information

The following concepts mentioned in the questions were not found in the current codebase:

1. **"7 time windows"**: The sequencer dynamically tracks all available time slots (not fixed to 7). Each time slot gets its own columns (`{TIME} Points` and `{TIME} Rolling`).

2. **"Best 2 times"**: The sequencer calculates the best performing time slot dynamically but doesn't explicitly store "best 2 times" as separate columns. The best time is determined by comparing rolling sums.

### Recommendations

If you need explicit "best 2 times" storage, consider:
- Adding columns: `Best_Time_1`, `Best_Time_2`, `Best_Time_1_Rolling`, `Best_Time_2_Rolling`
- Or storing as JSON in a metadata column

If you need fixed "7 time windows", consider:
- Standardizing to specific 7 time slots
- Storing as structured columns: `Window_1_Time`, `Window_1_Points`, `Window_1_Rolling`, etc.

