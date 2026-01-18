# Pre-Hydration Stress Test Suite

## Overview

This stress test suite validates the pre-hydration functionality under various edge cases and failure scenarios. Pre-hydration loads historical bars from CSV files before the range window starts, and these tests ensure the system handles errors gracefully.

## Test Scenarios

### 1. `missing_file`
**Description**: Tests behavior when CSV file is missing  
**Expected**: `PRE_HYDRATION_ZERO_BARS` event (WARN before range start, ERROR after)

### 2. `empty_file`
**Description**: Tests behavior with empty CSV file (only header)  
**Expected**: `PRE_HYDRATION_ZERO_BARS` event

### 3. `corrupted_format`
**Description**: Tests behavior with corrupted CSV (invalid format, missing columns)  
**Expected**: `PRE_HYDRATION_COMPLETE` - should skip invalid lines

### 4. `wrong_date`
**Description**: Tests behavior with CSV from wrong trading date  
**Expected**: `PRE_HYDRATION_COMPLETE` - should filter out wrong date bars

### 5. `out_of_order`
**Description**: Tests behavior with out-of-order bars  
**Expected**: `PRE_HYDRATION_COMPLETE` - should sort correctly

### 6. `large_volume`
**Description**: Tests behavior with very large CSV file (15k+ bars)  
**Expected**: `PRE_HYDRATION_COMPLETE`

### 7. `data_gaps`
**Description**: Tests behavior with gaps in data  
**Expected**: `PRE_HYDRATION_COMPLETE`

### 8. `future_bars`
**Description**: Tests behavior with future bars in CSV  
**Expected**: `PRE_HYDRATION_COMPLETE` - should filter future bars

### 9. `invalid_timestamps`
**Description**: Tests behavior with invalid timestamps  
**Expected**: `PRE_HYDRATION_COMPLETE` - should skip invalid lines

### 10. `invalid_ohlc`
**Description**: Tests behavior with invalid OHLC values  
**Expected**: `PRE_HYDRATION_COMPLETE` - should skip invalid lines

### 11. `dst_transition`
**Description**: Tests behavior around DST transition  
**Expected**: `PRE_HYDRATION_COMPLETE`

### 12. `multiple_streams`
**Description**: Tests multiple streams loading simultaneously  
**Expected**: `PRE_HYDRATION_COMPLETE`

## Usage

### Run a single test:
```bash
python stress_test_prehydration.py <scenario_name>
```

Example:
```bash
python stress_test_prehydration.py missing_file
```

### Run all tests:
```bash
python stress_test_prehydration.py
```

## How It Works

1. **Test Setup**: Creates a temporary test directory with:
   - CSV files (or missing files) based on scenario
   - Minimal timetable with ES1 stream enabled
   - Copied spec file

2. **Execution**: Runs DRYRUN with `QTSW2_PROJECT_ROOT` environment variable pointing to test directory

3. **Verification**: Checks logs for expected pre-hydration events

4. **Cleanup**: Removes temporary test directory

## CSV File Format

Pre-hydration expects CSV files at:
```
data/raw/{instrument}/1m/{yyyy}/{MM}/{INSTRUMENT}_1m_{yyyy-MM-dd}.csv
```

Format:
```csv
timestamp_utc,open,high,low,close,volume
2026-01-16T08:00:00Z,7000,7005,6995,7002,1000
2026-01-16T08:01:00Z,7002,7007,6997,7004,1000
...
```

## Expected Behavior

- **Missing files**: System should log warning/error and continue (degraded operation)
- **Invalid data**: System should skip invalid lines and continue
- **Wrong dates**: System should filter bars outside hydration window
- **Future bars**: System should filter bars in the future
- **Out-of-order**: System should sort bars chronologically
- **Large volumes**: System should handle large files efficiently

## Notes

- Tests use temporary directories to avoid polluting the main project
- Each test runs independently
- Logs are checked for expected events to verify correct behavior
- Tests timeout after 60 seconds to prevent hanging
