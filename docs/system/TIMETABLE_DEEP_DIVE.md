# Timetable Engine Deep Dive

## Overview

The **Timetable Engine** generates the daily execution plan (`timetable_current.json`) that tells the Robot which trades to take today. It's the bridge between the Master Matrix (historical analysis) and the Robot (live execution).

## Core Purpose

The Timetable Engine:
1. **Reads Master Matrix** - Gets the latest date's sequencer decisions (which time slot each stream should use)
2. **Applies Filters** - Checks DOW/DOM/exclude_times filters to determine if trades are enabled
3. **Writes Execution Contract** - Creates `timetable_current.json` with all 14 streams (enabled or blocked)
4. **Robot Reads It** - NinjaTrader Robot reads this file to know what to trade

## Architecture

### Two Generation Paths

1. **Backend Path** (`timetable_engine.py`):
   - Called automatically when master matrix is saved (`file_manager.py` line 66)
   - Method: `write_execution_timetable_from_master_matrix()`
   - Source: Master matrix DataFrame (latest date)
   - Output: `data/timetable/timetable_current.json`

2. **Frontend Path** (`matrixWorker.js`):
   - Calculated on-demand in browser
   - Used for UI display only
   - Not used by Robot

### Key Components

- **RS Calculation** (`calculate_rs_for_stream`): Calculates rolling sum scores for time slot selection
- **Time Selection** (`select_best_time`): Chooses best time slot based on RS values
- **Filter Checking** (`check_filters`): Applies DOW/DOM/SCF filters
- **Execution Timetable Writing** (`write_execution_timetable`): Writes canonical JSON file

## Data Flow

```
Master Matrix (latest date)
    ↓
Extract streams, times, filters
    ↓
Apply filters (DOW/DOM/exclude_times/SCF)
    ↓
Build streams array (all 14 streams)
    ↓
Write timetable_current.json
    ↓
Robot reads and executes
```

## Issues Identified

### 1. Date Normalization Inconsistency (HIGH PRIORITY)

**Location**: `timetable_engine.py` lines 133, 291, 303, 334, 467, 470, 476, 480, 482

**Problem**:
- Uses `pd.to_datetime()` with `errors='coerce'` in some places (line 133)
- Uses `pd.to_datetime()` without error handling in others (lines 291, 303)
- Re-parses dates that should already be normalized from master matrix
- Violates single-ownership principle (DataLoader owns date normalization)

**Impact**:
- Inconsistent error handling (some places coerce NaT, others fail)
- Potential for NaT propagation downstream
- Redundant parsing overhead

**Current Code**:
```python
# Line 133: Uses errors='coerce' (salvage mode)
df['Date'] = pd.to_datetime(df['Date'], errors='coerce')

# Line 291: No error handling
df['Date'] = pd.to_datetime(df['Date'])

# Line 467: Assumes trade_date is already datetime
latest_date = pd.to_datetime(master_matrix_df['trade_date']).max()
```

**Recommendation**:
- Use `trade_date` column from master matrix (already normalized by DataLoader)
- Validate dtype/presence only (don't re-parse)
- Remove all `pd.to_datetime()` calls for dates that come from master matrix
- Use `errors='raise'` for new date parsing (fail-closed)

### 2. Missing trade_date Column Handling (MEDIUM PRIORITY)

**Location**: `timetable_engine.py` lines 466-474

**Problem**:
- Falls back to `Date` column if `trade_date` is missing
- Should fail-closed if canonical column is missing
- Logs error but continues (should be hard failure)

**Current Code**:
```python
if 'trade_date' in master_matrix_df.columns:
    latest_date = pd.to_datetime(master_matrix_df['trade_date']).max()
elif 'Date' in master_matrix_df.columns:
    latest_date = pd.to_datetime(master_matrix_df['Date']).max()
else:
    logger.error("Master matrix missing date columns...")
    return  # Soft failure
```

**Recommendation**:
- Require `trade_date` column (fail-fast)
- Don't fall back to `Date` (violates single-ownership)
- Raise `ValueError` if `trade_date` is missing

### 3. RS Calculation Date Parsing (MEDIUM PRIORITY)

**Location**: `timetable_engine.py` lines 87-194 (`calculate_rs_for_stream`)

**Problem**:
- Reads analyzer output files directly (bypasses DataLoader)
- Uses `errors='coerce'` for date parsing (line 133)
- Drops invalid dates silently (line 140)
- Should use DataLoader's normalization or validate contract

**Current Code**:
```python
df['Date'] = pd.to_datetime(df['Date'], errors='coerce')
valid_dates = df['Date'].notna()
if not valid_dates.any():
    logger.warning(f"File {file_path.name} has no valid dates, skipping")
    continue
df = df[valid_dates].copy()
```

**Recommendation**:
- Option A: Use DataLoader to load analyzer output (ensures normalization)
- Option B: Validate that analyzer output has `trade_date` column (contract enforcement)
- Use `errors='raise'` for date parsing (fail-closed)
- Log and fail if dates are invalid (don't silently skip)

### 4. SCF Value Lookup Performance (LOW PRIORITY)

**Location**: `timetable_engine.py` lines 260-316 (`get_scf_values`)

**Problem**:
- Searches for files by pattern matching
- Falls back to scanning all parquet files if pattern doesn't match
- No caching of file locations
- Could be optimized with better file discovery

**Current Code**:
```python
file_pattern = f"{stream_id}_an_{year}_{month:02d}.parquet"
file_path = stream_dir / str(year) / file_pattern

if not file_path.exists():
    # Try alternative patterns (using cache)
    parquet_files = self._get_parquet_files(stream_dir)
    for pf in parquet_files:
        # Scan all files...
```

**Recommendation**:
- Cache file-to-date mappings
- Use more efficient file discovery
- Consider using DataLoader's file discovery logic

### 5. Incomplete Stream Handling (MEDIUM PRIORITY)

**Location**: `timetable_engine.py` lines 625-642

**Problem**:
- Adds missing streams with default time slots
- Doesn't verify that default time is valid for the stream
- May select filtered times as defaults

**Current Code**:
```python
for stream_id in self.streams:
    if stream_id not in streams_dict:
        # Stream not in master matrix - add as blocked
        available_times = self.session_time_slots.get(session, [])
        default_time = available_times[0] if available_times else ""
```

**Recommendation**:
- Select first NON-FILTERED time slot as default
- Verify default time is valid for stream
- Log warning when stream is missing from master matrix

### 6. Time Change Column Parsing (LOW PRIORITY)

**Location**: `timetable_engine.py` lines 519-534

**Problem**:
- Handles both "old -> new" and "new" formats
- Logic is correct but could be clearer
- No validation that parsed time is valid

**Current Code**:
```python
if time_change and str(time_change).strip():
    time_change_str = str(time_change).strip()
    if '->' in time_change_str:
        # Backward compatibility: parse "old -> new" format
        parts = time_change_str.split('->')
        if len(parts) == 2:
            time = parts[1].strip()
    else:
        # Current format: Time Change is just the new time
        time = time_change_str
```

**Recommendation**:
- Validate that parsed time is in `session_time_slots`
- Normalize time using `normalize_time()` utility
- Log warning if time is invalid

### 7. CME Trading Date Rollover Logic (MEDIUM PRIORITY)

**Location**: `timetable_engine.py` lines 669-711

**Problem**:
- Computes trading_date based on Chicago time >= 17:00
- Has validation check but only logs warning
- Should fail-closed if validation fails

**Current Code**:
```python
if chicago_hour >= 17:
    trading_date = (chicago_date + timedelta(days=1)).isoformat()
else:
    trading_date = chicago_date.isoformat()

# Validation: Flag if as_of >= 17:00 but trading_date would be same day
if chicago_hour >= 17 and trading_date == chicago_date.isoformat():
    logger.warning("CME_TRADING_DATE_VALIDATION_FAILED...")
```

**Recommendation**:
- Raise `AssertionError` if validation fails (fail-closed)
- Add unit tests for rollover logic
- Document the CME rollover rule clearly

### 8. Atomic Write Implementation (LOW PRIORITY)

**Location**: `timetable_engine.py` lines 728-748

**Problem**:
- Uses `temp_file.replace(final_file)` for atomic write
- Good implementation but could add file locking
- No verification that write succeeded

**Current Code**:
```python
temp_file = output_dir / "timetable_current.tmp"
final_file = output_dir / "timetable_current.json"

with open(temp_file, 'w', encoding='utf-8') as f:
    json.dump(execution_timetable, f, indent=2, ensure_ascii=False)

temp_file.replace(final_file)
```

**Recommendation**:
- Verify file exists after rename
- Add file locking if Robot reads while writing
- Consider using `shutil.move()` for cross-platform compatibility

### 9. RS Calculation Lookback Window (LOW PRIORITY)

**Location**: `timetable_engine.py` lines 87-194

**Problem**:
- Uses hardcoded `lookback_days=13` default
- Loads last 10 files (hardcoded)
- May not get enough data for accurate RS calculation

**Current Code**:
```python
def calculate_rs_for_stream(self, stream_id: str, session: str, 
                           lookback_days: int = 13) -> Dict[str, float]:
    # ...
    for file_path in parquet_files[:10]:  # Load last 10 files
```

**Recommendation**:
- Make file count configurable
- Ensure enough files are loaded to cover lookback_days
- Log warning if insufficient data

### 10. Session Detection Logic (LOW PRIORITY)

**Location**: `timetable_engine.py` lines 516-517

**Problem**:
- Assumes stream_id ending in '1' = S1, '2' = S2
- Doesn't verify session from master matrix data
- Could be wrong if stream naming changes

**Current Code**:
```python
session = 'S1' if stream.endswith('1') else 'S2'
```

**Recommendation**:
- Use `Session` column from master matrix if available
- Fall back to stream_id pattern only if Session column missing
- Validate session is S1 or S2

## Performance Issues

### 1. Repeated File Scans

**Location**: Multiple methods

**Problem**:
- `_get_parquet_files()` caches results (good)
- But `get_scf_values()` scans files if pattern doesn't match
- Could cache file-to-date mappings

**Recommendation**:
- Cache file discovery results per stream
- Use DataLoader's file discovery if available

### 2. RS Calculation Redundancy

**Location**: `generate_timetable()` lines 340-344

**Problem**:
- Pre-loads SCF values (good optimization)
- But RS calculation happens per stream/session
- Could cache RS values if same stream/session is processed multiple times

**Recommendation**:
- Cache RS calculation results
- Only recalculate if data has changed

## Design Issues

### 1. Two Separate Timetable Generation Systems

**Problem**:
- Backend (`timetable_engine.py`) and Frontend (`matrixWorker.js`) have separate logic
- Risk of divergence
- Frontend fixes don't help Robot (which reads backend file)

**Recommendation**:
- Document that only backend path matters for Robot
- Consider making frontend call backend API instead of reimplementing

### 2. Filter Application Order

**Problem**:
- Filters applied in multiple places
- Order matters but not clearly documented
- Could have conflicts between different filter types

**Recommendation**:
- Document filter application order
- Centralize filter logic
- Add tests for filter combinations

## Summary of Issues

### High Priority
1. **Date Normalization Inconsistency** - Violates single-ownership, uses errors='coerce'

### Medium Priority
2. **Missing trade_date Column Handling** - Should fail-closed
3. **RS Calculation Date Parsing** - Should use DataLoader or validate contract
4. **Incomplete Stream Handling** - May select filtered times as defaults
5. **CME Trading Date Rollover Logic** - Should fail-closed on validation failure

### Low Priority
6. **Time Change Column Parsing** - Needs validation
7. **SCF Value Lookup Performance** - Could be optimized
8. **Atomic Write Implementation** - Could add verification
9. **RS Calculation Lookback Window** - Hardcoded limits
10. **Session Detection Logic** - Should use Session column

## Recommendations

1. **Align with Master Matrix Fixes**:
   - Use `trade_date` column exclusively (fail if missing)
   - Remove all `pd.to_datetime()` calls for master matrix dates
   - Validate dtype/presence only

2. **Fail-Closed Design**:
   - Raise errors instead of logging warnings
   - Don't silently skip invalid data
   - Validate all inputs before processing

3. **Single Ownership**:
   - DataLoader owns date normalization
   - Timetable Engine should validate, not re-parse
   - Use `trade_date` column from master matrix

4. **Performance Optimizations**:
   - Cache file discovery results
   - Cache RS calculation results
   - Batch load SCF values (already done)

5. **Testing**:
   - Add unit tests for filter logic
   - Test CME rollover edge cases
   - Test missing stream handling
   - Test time change parsing
