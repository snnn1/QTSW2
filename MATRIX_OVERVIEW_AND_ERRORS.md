# Master Matrix - Complete Overview & Error Analysis

## Table of Contents
1. [What is the Master Matrix?](#what-is-the-master-matrix)
2. [How It Works](#how-it-works)
3. [Data Flow](#data-flow)
4. [Key Features](#key-features)
5. [Possible Errors & Issues](#possible-errors--issues)
6. [Error Handling & Recovery](#error-handling--recovery)
7. [Troubleshooting Guide](#troubleshooting-guide)

---

## What is the Master Matrix?

The **Master Matrix** is a unified trading data table that merges all trades from all trading streams (ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, YM1, YM2) into a single, chronologically sorted dataset. It serves as the "single source of truth" for all trading decisions.

### Purpose
- **Unified View**: Combines all streams into one table sorted by date, time, symbol, and stream
- **Sequencer Logic**: Applies time-change logic to select one trade per day per stream (matching the Sequential Processor behavior)
- **Filtering**: Applies user-defined filters (day-of-week, day-of-month, time exclusions)
- **Global Columns**: Adds metadata like `global_trade_id`, `day_of_month`, `dow`, `session_index`, `is_two_stream`, `dom_blocked`, `final_allowed`

---

## How It Works

### 1. **Data Loading Phase**
- Scans `data/analyzer_runs/` directory for stream subdirectories (ES1, ES2, etc.)
- Auto-discovers streams by matching pattern: `[A-Z]{2}[12]` (e.g., ES1, GC2)
- Loads monthly consolidated Parquet files from year subdirectories (e.g., `ES1/2024/ES1_an_2024_11.parquet`)
- Skips daily temp files in date folders (YYYY-MM-DD/)

### 2. **Sequencer Logic Application**
- Processes ALL historical data to build accurate time slot histories (13-trade rolling window)
- For each stream, day by day:
  - Starts with first available time slot
  - Tracks rolling sums for each time slot (Win=+1, Loss=-2, BE=0)
  - On a **Loss**, compares current time slot's rolling sum with other slots in the same session
  - Switches to better time slot if its rolling sum is higher (decision applies to NEXT day)
  - Selects **one trade per day per stream** based on current time slot

### 3. **Schema Normalization**
- Ensures all streams have the same columns
- Adds missing required columns with defaults:
  - `Date`, `Time`, `Target`, `Peak`, `Direction`, `Result`, `Range`, `Stream`, `Instrument`, `Session`, `Profit`, `SL`
- Adds optional columns: `scf_s1`, `scf_s2`, `onr`, `onr_high`, `onr_low`
- Creates derived columns: `entry_time`, `exit_time`, `entry_price`, `exit_price`, `R`, `pnl`, `rs_value`, `selected_time`, `time_bucket`, `trade_date`

### 4. **Global Columns Addition**
- `global_trade_id`: Unique sequential ID
- `day_of_month`: 1-31
- `dow`: Day of week (Mon-Fri)
- `month`: 1-12
- `session_index`: 1 for S1, 2 for S2
- `is_two_stream`: True for *2 streams (ES2, GC2, etc.)
- `dom_blocked`: True if day is 4/16/30 and stream is a "2"
- `filter_reasons`: Comma-separated list of filter reasons
- `final_allowed`: Boolean after all filters applied

### 5. **Filtering**
- Applies per-stream filters:
  - `exclude_days_of_week`: Block specific days (e.g., ["Wednesday"])
  - `exclude_days_of_month`: Block specific days (e.g., [4, 16, 30])
  - `exclude_times`: Block specific times (e.g., ["07:30", "08:00"])
- Filters are applied during sequencer logic (excluded times are completely removed)
- Final `final_allowed` flag indicates if trade passes all filters

### 6. **Output**
- Saves as Parquet: `master_matrix_YYYYMMDD_HHMMSS.parquet` (full backtest) or `master_matrix_today_YYYYMMDD.parquet` (single day)
- Saves as JSON: `master_matrix_YYYYMMDD_HHMMSS.json` (for inspection)
- Sorted by: `trade_date`, `entry_time`, `Instrument`, `Stream`

---

## Data Flow

```
Analyzer Output Files (data/analyzer_runs/)
    ├── ES1/2024/ES1_an_2024_11.parquet
    ├── ES2/2024/ES2_an_2024_11.parquet
    ├── GC1/2024/GC1_an_2024_11.parquet
    └── ...
    ↓
Master Matrix Builder
    ├── Load all streams
    ├── Apply sequencer logic (select one trade per day per stream)
    ├── Normalize schema
    ├── Add global columns
    └── Apply filters
    ↓
Master Matrix Files (data/master_matrix/)
    ├── master_matrix_20241127_143022.parquet
    └── master_matrix_20241127_143022.json
    ↓
Frontend/Backend API
    └── Display in React app (matrix_timetable_app)
```

---

## Key Features

### Time Slot Selection Logic
- **Rolling Sum Calculation**: Last 13 trades per time slot
- **Time Change Trigger**: Only on Loss
- **Comparison**: Compares rolling sums across time slots in same session
- **Session Configuration**:
  - S1: ["07:30", "08:00", "09:00"]
  - S2: ["09:30", "10:00", "10:30", "11:00"]

### Stop Loss (SL) Calculation
- Formula: `SL = 3 × Target`, capped at `Range`
- Applied to each trade during sequencer logic

### Time Change Display
- Format: `"old_time→new_time"` (e.g., "09:00→10:00")
- Shown on the day the change takes effect

### Rolling Sum Columns
- For each available time slot: `"{time} Rolling"` and `"{time} Points"`
- Only includes columns for available (non-excluded) times

---

## Possible Errors & Issues

### 1. **Data Loading Errors**

#### Error: No streams discovered
- **Symptoms**: `"No streams discovered! Check analyzer_runs directory."`
- **Causes**:
  - `data/analyzer_runs/` directory doesn't exist
  - No stream subdirectories (ES1, ES2, etc.) found
  - Directory permissions issue
- **Location**: `_discover_streams()` method
- **Error Code**: Returns empty list, logs warning

#### Error: Stream directory not found
- **Symptoms**: `"Stream directory not found: {stream_dir}"`
- **Causes**:
  - Stream subdirectory missing (e.g., `ES1/` doesn't exist)
  - Path resolution issue
- **Location**: `load_stream()` function
- **Error Code**: Stream added to `streams_failed` dict

#### Error: No monthly consolidated files found
- **Symptoms**: `"No monthly consolidated files found for stream: {stream_id}"`
- **Causes**:
  - No Parquet files matching pattern `{stream_id}_an_*.parquet` in year subdirectories
  - Files exist but in wrong location (e.g., in date folders instead of year folders)
  - Files not yet generated by analyzer
- **Location**: `load_stream()` function
- **Error Code**: Stream added to `streams_failed` dict

#### Error: Error loading Parquet file
- **Symptoms**: `"Error loading {file_path}: {e}"`
- **Causes**:
  - Corrupted Parquet file
  - File locked by another process
  - Insufficient memory
  - Invalid Parquet format
- **Location**: `load_stream()` function, inside file reading loop
- **Error Code**: Logs error, continues to next file

#### Error: No trade data found
- **Symptoms**: `"No trade data found!"`
- **Causes**:
  - All files empty
  - Date filters too restrictive
  - All trades filtered out
- **Location**: `load_all_streams()` method
- **Error Code**: Returns empty DataFrame

---

### 2. **Sequencer Logic Errors**

#### Error: Stream filters empty
- **Symptoms**: `"self.stream_filters is empty! Filters may not be applied!"`
- **Causes**:
  - Filters not initialized before calling `_apply_sequencer_logic()`
  - Filters cleared/reset unexpectedly
- **Location**: `_apply_sequencer_logic()` method
- **Error Code**: Logs warning, continues (may cause incorrect filtering)

#### Error: No available times after filtering
- **Symptoms**: `"Stream {stream_id}: No available times after filtering. Excluded: {exclude_times_str}"`
- **Causes**:
  - All time slots excluded via `exclude_times` filter
  - Stream has no valid time slots left
- **Location**: `_apply_sequencer_logic()` method
- **Error Code**: Stream added to `streams_skipped` list, no trades selected

#### Error: Excluded trades still present after filtering
- **Symptoms**: `"[ERROR] Stream {stream_id} {date}: {count} excluded trades still present after filtering!"`
- **Causes**:
  - Time string comparison mismatch (format differences)
  - Filtering logic bug
- **Location**: `_apply_sequencer_logic()` method, safety check
- **Error Code**: Logs error, continues (data integrity issue)

#### Error: Selected trade at excluded time
- **Symptoms**: `"[ERROR] Stream {stream_id} {date}: Selected trade at excluded time '{time}'! Skipping this day."`
- **Causes**:
  - Time slot selection logic bug
  - Time format mismatch
- **Location**: `_apply_sequencer_logic()` method, safety check
- **Error Code**: Logs error, skips the day

#### Error: About to add trade with excluded time
- **Symptoms**: `"[ERROR] Stream {stream_id} {date}: About to add trade with excluded time '{time}'! Skipping."`
- **Causes**:
  - Final safety check before adding trade to chosen_trades
  - Time format normalization issue
- **Location**: `_apply_sequencer_logic()` method, final check
- **Error Code**: Logs error, skips trade

#### Error: Excluded times still present in result
- **Symptoms**: `"[ERROR] Excluded times still present in result: {times}"`
- **Causes**:
  - Final cleanup didn't work
  - Time string comparison issue
- **Location**: `_apply_sequencer_logic()` method, final cleanup check
- **Error Code**: Logs error (data integrity issue)

#### Error: No chosen trades after sequencer logic
- **Symptoms**: `"[WARNING] No chosen trades after sequencer logic!"`
- **Causes**:
  - All streams skipped (no available times)
  - All trades filtered out
  - Date range has no data
- **Location**: `_apply_sequencer_logic()` method
- **Error Code**: Returns empty DataFrame

---

### 3. **Schema Normalization Errors**

#### Error: Date column invalid
- **Symptoms**: `"[WARNING] Stream {stream_id} {date}: Date column was invalid, using loop date instead"`
- **Causes**:
  - Date column contains non-date values
  - Date format unrecognized
  - Missing Date column
- **Location**: `_apply_sequencer_logic()` method, when setting `trade_date`
- **Error Code**: Uses loop date as fallback, logs warning

#### Error: Invalid trade_date in final DataFrame
- **Symptoms**: `"Found {count} rows with invalid trade_date out of {total} total rows - filtering them out"`
- **Causes**:
  - Date conversion failed for some rows
  - Missing Date column
- **Location**: `build_master_matrix()` method, before sorting
- **Error Code**: Filters out invalid rows, logs warning

---

### 4. **File I/O Errors**

#### Error: Could not load existing master matrix
- **Symptoms**: `"Could not load existing master matrix: {e}"`
- **Causes**:
  - File corrupted
  - File locked
  - Path doesn't exist
- **Location**: `_rebuild_partial()` method
- **Error Code**: Logs warning, continues with empty existing_df

#### Error: Failed to save Parquet file
- **Symptoms**: Exception during `df.to_parquet()`
- **Causes**:
  - Disk full
  - Permission denied
  - Path doesn't exist
- **Location**: `build_master_matrix()` method
- **Error Code**: Exception bubbles up

#### Error: Failed to save JSON file
- **Symptoms**: Exception during `df.to_json()`
- **Causes**:
  - Disk full
  - Permission denied
  - Data too large for JSON
- **Location**: `build_master_matrix()` method
- **Error Code**: Exception bubbles up

---

### 5. **API/Backend Errors**

#### Error: Failed to load MasterMatrix module
- **Symptoms**: `"ERROR reloading MasterMatrix module: {e}"`
- **Causes**:
  - Module file missing
  - Syntax error in module
  - Import dependency missing
- **Location**: `/api/matrix/build` endpoint
- **Error Code**: HTTP 500, returns error detail

#### Error: TypeError creating MasterMatrix
- **Symptoms**: `"TypeError creating MasterMatrix: {error_str}"`
- **Causes**:
  - Backend using cached/old version of module
  - Parameter mismatch
- **Location**: `/api/matrix/build` endpoint
- **Error Code**: HTTP 500, suggests restarting backend

#### Error: Master matrix built but is empty
- **Symptoms**: `"Master matrix built but is empty"`
- **Causes**:
  - No data in date range
  - All trades filtered out
  - All streams failed to load
- **Location**: `/api/matrix/build` endpoint
- **Error Code**: HTTP 200 with warning message

#### Error: Failed to build master matrix
- **Symptoms**: `"Failed to build master matrix: {str(e)}"`
- **Causes**:
  - Any exception during build process
  - Memory error
  - Data corruption
- **Location**: `/api/matrix/build` endpoint
- **Error Code**: HTTP 500, returns exception message

#### Error: No master matrix files found
- **Symptoms**: `"No master matrix files found. Checked: {dir1} and {dir2}. Build the matrix first."`
- **Causes**:
  - Matrix never built
  - Files deleted
  - Wrong directory path
- **Location**: `/api/matrix/data` endpoint
- **Error Code**: Returns empty data with error message

#### Error: Failed to load matrix data
- **Symptoms**: `"Failed to load matrix data: {str(e)}"`
- **Causes**:
  - File corrupted
  - Memory error
  - Path doesn't exist
- **Location**: `/api/matrix/data` endpoint
- **Error Code**: HTTP 500, returns exception with traceback

---

### 6. **Frontend Errors**

#### Error: App initialization error
- **Symptoms**: React error boundary catches error
- **Causes**:
  - JavaScript syntax error
  - Missing dependency
  - State initialization error
- **Location**: `App.jsx` component
- **Error Code**: Displays error UI with stack trace

#### Error: Backend not ready
- **Symptoms**: `"Failed to load master matrix"` in UI
- **Causes**:
  - Backend not running
  - Backend on wrong port
  - CORS issue
- **Location**: `loadMasterMatrix()` function
- **Error Code**: Sets `masterError` state, shows retry button

#### Error: Worker error
- **Symptoms**: `workerError` state set
- **Causes**:
  - Web Worker crashed
  - Data too large for worker
  - Memory error in worker
- **Location**: `useMatrixWorker` hook
- **Error Code**: Displays error in UI

---

### 7. **Data Integrity Errors**

#### Error: Missing required columns
- **Symptoms**: Schema normalization adds defaults
- **Causes**:
  - Analyzer output missing columns
  - Column name mismatch
- **Location**: `normalize_schema()` method
- **Error Code**: Adds missing columns with defaults (NaN, empty string, etc.)

#### Error: SL column missing
- **Symptoms**: `"SL column missing, adding with 0 values"`
- **Causes**:
  - SL calculation failed
  - Column dropped accidentally
- **Location**: `build_master_matrix()` method
- **Error Code**: Adds SL column with 0 values

#### Error: Time Change column missing
- **Symptoms**: `"Time Change column missing, adding with empty values"`
- **Causes**:
  - Sequencer logic didn't add column
  - Column dropped accidentally
- **Location**: `build_master_matrix()` method
- **Error Code**: Adds Time Change column with empty strings

---

### 8. **Performance Issues**

#### Issue: Slow loading for large datasets
- **Symptoms**: Long wait times, high memory usage
- **Causes**:
  - Too many files to load
  - Large Parquet files
  - Insufficient RAM
- **Mitigation**: 
  - Use date filters to limit data
  - Process streams separately
  - Increase system RAM

#### Issue: Memory error
- **Symptoms**: `MemoryError` exception
- **Causes**:
  - Dataset too large for available RAM
  - Memory leak in processing
- **Mitigation**:
  - Use date range filters
  - Process in chunks
  - Close file handles properly

---

## Error Handling & Recovery

### Retry Logic
- **Stream Loading**: Retries failed streams up to 3 times (configurable via `max_retries`)
- **Retry Delay**: 2 seconds between retries (configurable via `retry_delay_seconds`)
- **Wait for Streams**: Can disable retries with `wait_for_streams=False`

### Error Reporting
- **Logging**: All errors logged to `logs/master_matrix.log`
- **Console Output**: Errors also printed to stderr
- **API Responses**: Backend returns HTTP status codes and error messages
- **Frontend Display**: Errors shown in UI with retry options

### Graceful Degradation
- **Missing Streams**: Continues with available streams, reports failed ones
- **Missing Columns**: Adds defaults instead of failing
- **Invalid Dates**: Filters out invalid rows, continues processing
- **Empty Results**: Returns empty DataFrame instead of crashing

---

## Troubleshooting Guide

### Problem: No data in master matrix

**Check:**
1. Verify `data/analyzer_runs/` directory exists
2. Check stream subdirectories exist (ES1, ES2, etc.)
3. Verify monthly Parquet files exist in year subdirectories
4. Check date filters aren't too restrictive
5. Review `logs/master_matrix.log` for errors

**Solution:**
- Run analyzer to generate data first
- Adjust date filters
- Check file permissions

---

### Problem: Streams not loading

**Check:**
1. Stream directory exists and is readable
2. Monthly files match pattern `{stream}_an_*.parquet`
3. Files are in year subdirectories (not date folders)
4. Files are not corrupted

**Solution:**
- Verify analyzer output structure
- Check file permissions
- Re-run analyzer if needed

---

### Problem: All trades filtered out

**Check:**
1. Review `exclude_times` filters (may have excluded all times)
2. Check `exclude_days_of_week` filters
3. Check `exclude_days_of_month` filters
4. Verify `final_allowed` column values

**Solution:**
- Adjust filter settings
- Check filter logic in `add_global_columns()`
- Review `filter_reasons` column for details

---

### Problem: Time slot selection incorrect

**Check:**
1. Verify sequencer logic is processing all historical data
2. Check rolling sum calculations (13-trade window)
3. Verify time change logic (only on Loss)
4. Review `Time Change` column values

**Solution:**
- Ensure all historical data is loaded (don't filter by date too early)
- Check time slot histories are building correctly
- Verify session configuration matches expected times

---

### Problem: Backend API errors

**Check:**
1. Backend is running on port 8000
2. Master Matrix module loads correctly
3. No syntax errors in `master_matrix.py`
4. Dependencies installed (pandas, pyarrow, etc.)

**Solution:**
- Restart backend completely (clears module cache)
- Check backend logs: `logs/backend_debug.log`
- Verify Python environment has all dependencies

---

### Problem: Frontend can't load data

**Check:**
1. Backend is running and accessible
2. CORS configured correctly
3. API endpoint returns data
4. Browser console for errors

**Solution:**
- Check backend status: `http://localhost:8000/api/matrix/test`
- Verify CORS settings in `main.py`
- Check network tab in browser dev tools
- Review frontend console for errors

---

### Problem: Performance issues

**Check:**
1. Dataset size (number of trades, date range)
2. Available system RAM
3. Number of streams being processed
4. File I/O speed

**Solution:**
- Use date range filters to limit data
- Process streams separately (partial rebuild)
- Increase system RAM
- Use faster storage (SSD)

---

## Summary

The Master Matrix is a critical component that:
- **Merges** all trading streams into one unified table
- **Applies** sequencer logic to select one trade per day per stream
- **Filters** trades based on user-defined rules
- **Normalizes** schema across all streams
- **Adds** global metadata columns

**Common Error Categories:**
1. Data loading failures (missing files, corrupted data)
2. Sequencer logic errors (filtering issues, time selection bugs)
3. Schema normalization issues (missing columns, invalid dates)
4. File I/O problems (permissions, disk space)
5. API/Backend errors (module loading, parameter mismatches)
6. Frontend errors (backend connectivity, worker crashes)
7. Data integrity issues (missing columns, invalid values)
8. Performance problems (memory, speed)

**Error Handling Strategy:**
- Comprehensive logging to `logs/master_matrix.log`
- Graceful degradation (continues with available data)
- Retry logic for transient failures
- Clear error messages in API responses
- User-friendly error display in frontend

For detailed error investigation, always check:
1. `logs/master_matrix.log` - Main matrix processing logs
2. `logs/backend_debug.log` - Backend API logs
3. Browser console - Frontend errors
4. API response messages - Backend error details



