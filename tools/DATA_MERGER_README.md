# Data Merger / Consolidator

## Overview

The Data Merger consolidates daily analyzer and sequencer output files into monthly Parquet files organized by instrument and year.

## Purpose

- **Consolidate daily files**: Merge daily output files into monthly archives
- **Organize by instrument**: Group files by instrument and year
- **Remove duplicates**: Automatically deduplicate data
- **Idempotent**: Safe to run multiple times without double-writing data
- **Clean up**: Automatically deletes processed daily temp folders

## Directory Structure

### Input Directories (Daily Temp Files)
```
data/
├── analyzer_temp/
│   └── YYYY-MM-DD/          # Daily analyzer output folders
│       └── *.parquet         # Daily analyzer files
└── sequencer_temp/
    └── YYYY-MM-DD/           # Daily sequencer output folders
        └── *.parquet         # Daily sequencer files
```

### Output Directories (Monthly Files)
```
data/
├── analyzer_runs/
│   └── <instrument>/
│       └── <year>/
│           └── <instrument>_an_<year>_<month>.parquet
└── sequencer_runs/
    └── <instrument>/
        └── <year>/
            └── <instrument>_seq_<year>_<month>.parquet
```

## File Naming Convention

- **Analyzer monthly files**: `<instrument>_an_<year>_<month>.parquet`
  - Example: `ES_an_2025_01.parquet` (ES analyzer data for January 2025)
  
- **Sequencer monthly files**: `<instrument>_seq_<year>_<month>.parquet`
  - Example: `NQ_seq_2025_01.parquet` (NQ sequencer data for January 2025)

## Usage

### Command Line
```bash
python tools/data_merger.py
```

### Windows Batch File
```batch
batch\RUN_DATA_MERGER.bat
```

## How It Works

1. **Scans temp directories**: Finds all daily folders (YYYY-MM-DD format)
2. **Checks processed log**: Skips folders that have already been processed
3. **Reads daily files**: Loads all Parquet files from each daily folder
4. **Detects instruments**: Automatically detects instrument from filename or file content
5. **Merges by instrument**: Groups files by instrument
6. **Removes duplicates**:
   - Analyzer: Uses `[Date, Time, Target, Direction, Session, Instrument]` as duplicate key
   - Sequencer: Uses `[Date, Time, Target]` (or `[Day, Date, Time, Target]` if Day exists)
7. **Sorts data**:
   - Analyzer: Sorts by `[Date, Time]`
   - Sequencer: Sorts by `[Date, Time]` (or `[Day, Date, Time]` if Day exists)
8. **Merges with existing monthly file**: Appends to existing monthly file if it exists
9. **Writes monthly file**: Saves consolidated monthly Parquet file
10. **Marks as processed**: Records folder in processed log
11. **Deletes temp folder**: Removes daily temp folder after successful merge

## Idempotency

The merger is **idempotent** - safe to run multiple times:

- **Processed log**: Tracks which daily folders have been processed
- **Duplicate detection**: Prevents duplicate rows even if re-run
- **Atomic writes**: Uses temporary files and atomic rename for safety
- **Skip already processed**: Automatically skips folders already in processed log

## Error Handling

- **Corrupted files**: Skips corrupted Parquet files with error logging
- **Missing columns**: Handles missing columns gracefully
- **Empty files**: Skips empty files
- **Invalid dates**: Skips folders with invalid date format
- **Write failures**: Logs errors but continues processing other files

## Logging

The merger creates a log file: `data_merger.log`

Logs include:
- Processing status for each folder
- Number of files processed
- Duplicate removal counts
- Errors and warnings
- File write confirmations

## Processed Log

The merger maintains a JSON log file: `data/merger_processed.json`

This file tracks:
- Which analyzer folders have been processed
- Which sequencer folders have been processed

**Note**: If you need to reprocess a folder, you can manually edit this file or delete it to start fresh.

## Schema Details

### Analyzer Schema
- **Columns**: `Date`, `Time`, `Target`, `Peak`, `Direction`, `Result`, `Range`, `Stream`, `Instrument`, `Session`, `Profit`
- **Duplicate Key**: `[Date, Time, Target, Direction, Session, Instrument]`
- **Sort Key**: `[Date, Time]`

### Sequencer Schema
- **Base Columns**: `Day`, `Date`, `Time`, `Target`, `Peak`, `Result`, `Time Change`, `Profit`
- **Dynamic Columns**: Time slot columns (e.g., `09:00 Points`, `09:00 Rolling`)
- **Duplicate Key**: `[Date, Time, Target]` (or `[Day, Date, Time, Target]` if Day exists)
- **Sort Key**: `[Date, Time]` (or `[Day, Date, Time]` if Day exists)

## Examples

### Example 1: Process Single Day
If you have:
```
data/analyzer_temp/2025-01-15/
  ├── ES_breakout_20250115.parquet
  └── NQ_breakout_20250115.parquet
```

After running the merger:
```
data/analyzer_runs/
  ├── ES/2025/ES_an_2025_01.parquet  (contains Jan 15 data)
  └── NQ/2025/NQ_an_2025_01.parquet  (contains Jan 15 data)
```

### Example 2: Multiple Days in Same Month
If you process multiple days in January:
- Day 1: Creates `ES_an_2025_01.parquet`
- Day 2: Appends to `ES_an_2025_01.parquet` (removes duplicates)
- Day 3: Appends to `ES_an_2025_01.parquet` (removes duplicates)

## Integration with Pipeline

The data merger can be integrated into your daily pipeline:

1. **After analyzer runs**: Analyzer outputs to `data/analyzer_temp/YYYY-MM-DD/`
2. **After sequencer runs**: Sequencer outputs to `data/sequencer_temp/YYYY-MM-DD/`
3. **Run merger**: Consolidates daily files into monthly files
4. **Clean up**: Daily temp folders are automatically deleted

## Troubleshooting

### Folder Not Being Processed
- Check if folder name matches `YYYY-MM-DD` format
- Check if folder is already in `data/merger_processed.json`
- Check log file for errors

### Duplicate Data
- The merger automatically removes duplicates
- If you see duplicates, check the duplicate key columns exist in your data

### Missing Instruments
- Check if instrument can be detected from filename
- Check if `Instrument` column exists in Parquet files
- Check log file for warnings

### Corrupted Files
- Corrupted files are skipped automatically
- Check log file for specific error messages
- Manually verify the corrupted file

