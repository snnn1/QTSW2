# Parquet Data Requirements for DRYRUN Stress Tests

## Directory Structure

DRYRUN replay mode expects parquet files in the following structure:

```
data/translated/
  {instrument}/
    1m/
      {YYYY}/
        {MM}/
          {instrument}_1m_{YYYY-MM-DD}.parquet
```

## Example

For ES instrument, date 2026-01-16:
```
data/translated/ES/1m/2026/01/ES_1m_2026-01-16.parquet
```

## File Naming Convention

- **Format**: `{INSTRUMENT}_1m_{YYYY-MM-DD}.parquet`
- **Case**: Instrument name is uppercase (ES, CL, GC, etc.)
- **Date**: YYYY-MM-DD format (e.g., 2026-01-16)
- **Bar Period**: Always `1m` (1-minute bars)

## Required Data for Stress Tests

### Test 1: Late-Start DRYRUN
**Required**: 
- `ES_1m_2026-01-16.parquet` (or any test date)

**Status**: ✅ You have this data (2026-01-16 exists)

### Test 2: Missing-Data DRYRUN
**Required**: 
- `ES_1m_2026-01-16.parquet` (or any test date)

**Note**: This test modifies CSV files, but DRYRUN replay uses parquet files. The test verifies robustness when data is imperfect.

### Test 3: Duplicate Bars DRYRUN
**Required**: 
- `ES_1m_2026-01-16.parquet` (or any test date)

**Note**: This test modifies CSV files, but DRYRUN replay uses parquet files. The test verifies deduplication logic.

### Test 4: Multi-Day Continuous DRYRUN
**Required**: 
- Multiple parquet files for consecutive trading days
- Example: `ES_1m_2026-01-13.parquet` through `ES_1m_2026-02-28.parquet`
- All trading days in the date range (skips weekends)

**Current Test Range**: 2026-01-13 to 2026-02-28 (~30 trading days)

## What You Currently Have

Based on directory listing, you have:
- ✅ ES parquet files for 2026-01-16
- ✅ Multiple instruments (ES, CL, GC, NG, NQ, RTY, YM)
- ✅ Data spanning 2024-2026
- ✅ Organized by year/month structure

## Parquet File Format

Each parquet file should contain:
- **Columns**: timestamp_utc, open, high, low, close, volume (optional)
- **Bar Period**: 1-minute bars
- **Timezone**: UTC timestamps
- **Date Range**: Full trading day (typically 00:00 UTC to 23:59 UTC, or session hours)

## How Tests Use Parquet Files

1. **Test Setup**: Tests copy parquet files from `data/translated/` to temporary test directory
2. **DRYRUN Execution**: `SnapshotParquetBarProvider` reads parquet files using Python script
3. **Bar Replay**: `HistoricalReplay` feeds bars chronologically to the engine
4. **Verification**: Tests check logs for range computation, errors, etc.

## Checking If You Have Required Data

Run this to check for a specific date:
```powershell
Get-ChildItem -Path "data\translated\ES\1m\2026\01" -Filter "ES_1m_2026-01-16.parquet"
```

Or check all dates in a month:
```powershell
Get-ChildItem -Path "data\translated\ES\1m\2026\01" -Filter "*.parquet" | Select-Object Name
```

## Summary

**For the stress tests to run, you need:**

1. ✅ **Single-day tests** (Tests 1-3): 
   - `ES_1m_2026-01-16.parquet` (or any date you want to test)
   - **Status**: You have this

2. ⚠️ **Multi-day test** (Test 4):
   - Multiple consecutive trading days from 2026-01-13 to 2026-02-28
   - **Status**: Check if you have all dates in this range

**All tests will work once parquet files exist for the test dates.**
