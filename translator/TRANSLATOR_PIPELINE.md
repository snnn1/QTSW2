# Translator Pipeline - Complete Flow

## Overview

The translator transforms raw NinjaTrader data exports into strict, quant-grade standardized format with full schema enforcement, timezone handling, and validation.

## Complete Processing Pipeline

### Step 1: File Discovery (`get_data_files`)
- Scans input folder for data files
- Finds: `*.csv`, `*.txt`, `*.dat`
- Returns sorted list of file paths

### Step 2: Format Detection (`detect_file_format`)
- Auto-detects file format:
  - Has header? (checks for "Date" or "Time" in first line)
  - Separator type? (comma, semicolon)
  - Tick vs minute data? (from filename pattern)

### Step 3: Data Loading (`load_single_file`)
- Reads CSV file based on detected format
- Parses timestamps from Date/Time columns
- Standardizes column names (Open→open, High→high, etc.)

### Step 4: **Timezone Processing (NEW - Your Changes)**

#### 4a. Timezone Detection
- Checks filename for "_UTC" pattern:
  - `DataExport_ES_20251127_212335_UTC.csv` → detected as UTC source
  - `DataExport_CL_20251127_212335.csv` → detected as Chicago source

#### 4b. Timezone Localization
- **If timestamps are naive (no timezone):**
  - Files with "_UTC" → localize to UTC, then convert to Chicago
  - Files without "_UTC" → localize directly to America/Chicago
  
- **If timestamps are already timezone-aware:**
  - Detect current timezone
  - Convert mixed timezones → all to Chicago
  - Convert any timezone → Chicago (standard timezone)

#### 4c. Timezone Normalization (Optional)
- After Chicago conversion, can optionally normalize to target timezone:
  - Default: Keep in Chicago
  - Can convert: Chicago → UTC (if `normalize_timezone="UTC"`)
  - Never uses OS/system timezone - deterministic!

### Step 5: Data Cleaning
- Converts OHLC/volume to numeric (float64)
- Removes rows with invalid timestamps
- Extracts instrument from filename
- Adds schema columns:
  - `source` = "translator:filename.csv"
  - `interval` = "1min"
  - `synthetic` = False (bars are real, not reconstructed)

### Step 6: Instrument Detection
- Extracts instrument symbol from filename:
  - `DataExport_ES_*.csv` → "ES"
  - `MinuteDataExport_CL_*.csv` → "CL"
- Overrides any Instrument column in CSV (more reliable)

### Step 7: **Schema Enforcement (NEW)**
- Removes all non-schema columns:
  - ❌ `contract` (removed)
  - ❌ `frequency` (removed)
  - ❌ Any vendor metadata (removed)
  
- Adds required schema columns if missing:
  - ✅ `source` (pipeline origin)
  - ✅ `interval` (always "1min")
  - ✅ `synthetic` (boolean, False for real bars)

### Step 8: Schema Validation
- Validates exact column order:
  ```
  timestamp, open, high, low, close, volume, instrument, source, interval, synthetic
  ```
- Validates types:
  - timestamp: timezone-aware, America/Chicago
  - OHLC/volume: float64
  - instrument: string (non-empty)
  - source: string
  - interval: "1min"
  - synthetic: boolean
- Raises `SchemaValidationError` if validation fails

### Step 9: Sorting & Deterministic Ordering
- Sorts rows by timestamp (ascending)
- Resets index for deterministic output
- No randomness - bit-for-bit reproducible

### Step 10: Export

#### For Parquet:
- Keeps timestamp as native datetime type (timezone-aware)
- Saves with exact schema columns
- Optimized for performance

#### For CSV:
- Converts timestamp to ISO8601 string format:
  ```
  2025-01-01 17:00:00-06:00
  ```
- All columns in exact schema order
- UTF-8 encoding

## Example: Complete Transformation

### Input (Raw CSV)
```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
2025-01-01,17:00:00,64.66,64.67,64.65,64.65,16.0,DATAEXPORT
```

### Processing Steps
1. Load → Parse timestamp (naive)
2. Detect filename: `DataExport_CL_20251127_212335_UTC.csv`
3. Timezone: Localize UTC → Convert to Chicago → `2025-01-01 17:00:00-06:00`
4. Extract instrument: "CL" (from filename, not CSV column)
5. Add schema: `source="translator:DataExport_CL_...csv"`, `interval="1min"`, `synthetic=False`
6. Remove non-schema: `contract` column removed

### Output (Schema-Compliant)
```csv
timestamp,open,high,low,close,volume,instrument,source,interval,synthetic
2025-01-01 17:00:00-06:00,64.66,64.67,64.65,64.65,16.0,CL,translator:DataExport_CL_20251127_212335_UTC.csv,1min,False
```

## Key Features

### ✅ Timezone Handling (Your Addition)
- **Deterministic**: Never uses OS/system timezone
- **Smart Detection**: Filename pattern determines source timezone
- **Standard Output**: All data ends up in America/Chicago (trading timezone)
- **Optional Normalization**: Can convert Chicago → UTC if needed

### ✅ Strict Schema Enforcement
- **Exact Column Order**: Always same 10 columns in same order
- **Type Enforcement**: All types strictly enforced (float64, string, boolean)
- **No Metadata Leakage**: Vendor filenames, contract info removed
- **Validation**: Fails fast on invalid data

### ✅ Deterministic & Reproducible
- **No Randomness**: Same input → same output (bit-for-bit)
- **Consistent Formatting**: ISO8601 timestamps, consistent types
- **Sorted Rows**: Always sorted by timestamp

### ✅ Backward Compatible
- Existing code still works
- Schema enforcement is automatic
- Old files processed correctly with new schema

## Processing Options

### Separate by Year
- Creates separate files: `CL_2024_filename.parquet`, `CL_2025_filename.parquet`
- Filters by selected years if specified

### Output Formats
- `parquet`: Fast, compressed, datetime types
- `csv`: Human-readable, ISO8601 strings
- `both`: Creates both formats

### File Processing
- Always processes files **separately** (no merging)
- Each file becomes separate output file(s)
- Maintains traceability back to source file

## Error Handling

### Validation Errors
- `SchemaValidationError`: Raised if data doesn't conform
- Export prevented until fixed
- Detailed error messages

### Processing Errors
- Invalid timestamps → row removed
- Missing required columns → error
- Wrong types → converted or error

## Summary

The translator is now a **strict, quant-grade data pipeline** that:
1. ✅ Handles timezones deterministically (your new logic)
2. ✅ Enforces strict schema with exact column order
3. ✅ Validates all data before export
4. ✅ Produces deterministic, reproducible output
5. ✅ Removes all non-schema metadata
6. ✅ Maintains backward compatibility

Result: **Clean, standardized, validated data ready for quantitative analysis** 🎯









