# NinjaTrader Minute Data Exporter

## Overview

This is a **C# NinjaTrader indicator** that exports minute data to CSV format. It's used in NinjaTrader to export historical minute bar data with proper formatting and validation.

## Language
**C# (C-Sharp)** - NinjaTrader's scripting language

## Purpose

The `MinuteDataExporter` indicator exports historical minute bar data to CSV files with:
- Proper timestamp handling (bar time convention fix)
- Timezone conversion (Central Time → UTC)
- Data validation and gap detection
- Error handling and performance optimization

## Output Format

The script exports CSV files with the following header:
```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
```

**Example output line:**
```csv
2008-07-20,22:00:00,174.13,174.57,174.04,174.53,43.0,CL
```

## Filename Convention

Exports are saved to the Documents folder with the naming pattern:
```
MinuteDataExport_{InstrumentName}_{timestamp}_UTC.csv
```

**Example:**
- `MinuteDataExport_CL_20250920_082412_UTC.csv`
- `MinuteDataExport_ES_20250920_082412_UTC.csv`

## Key Features

### 1. Bar Time Convention Fix ⚠️ Critical
NinjaTrader timestamps bars with **CLOSE time**, but most platforms use **OPEN time**. The script subtracts 1 minute to get the bar's actual open time:

```csharp
// CRITICAL: NinjaTrader timestamps bars with CLOSE time, but most platforms use OPEN time
// We need to subtract 1 minute to get the bar's OPEN time
DateTime barOpenTime = Time[0].AddMinutes(-1);
```

### 2. Timezone Conversion
Converts Central Time to UTC for export:
```csharp
TimeZoneInfo centralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
exportTime = TimeZoneInfo.ConvertTimeToUtc(barOpenTime, centralTimeZone);
```

### 3. Data Validation
- Validates OHLCV data for NaN values
- Validates OHLC relationships (High ≥ Low, etc.)
- Detects and reports data gaps
- Skips invalid data with warnings

### 4. Performance Optimizations
- Manual flush every 10,000 records
- Progress reporting every 100,000 records
- File size monitoring (500MB limit)

## Usage in NinjaTrader

1. Load the indicator on a 1-minute chart
2. Run on historical data
3. Exports automatically to Documents folder
4. Shows progress and statistics in NinjaTrader output window

## Compatibility with QTSW2 Translator

The translator (`translator/file_loader.py`) is already designed to handle this format:

- ✅ Recognizes `MinuteDataExport_*` filename pattern
- ✅ Parses `Date,Time,Open,High,Low,Close,Volume,Instrument` columns
- ✅ Combines Date + Time into timestamp
- ✅ Handles timezone conversion (UTC → Chicago)
- ✅ Extracts instrument symbol from filename

## Important Notes

### Bar Time Fix
The translator in QTSW2 should be aware that the NinjaTrader export already applies the bar time fix (subtracting 1 minute), so the exported timestamps represent the **open time** of the bar, not the close time.

### Timezone
- **Export format**: UTC (as indicated by filename `_UTC.csv`)
- **NinjaTrader internal**: Central Time
- **Translator converts**: UTC → Chicago (America/Chicago) for processing

### Data Quality
The exporter includes validation:
- Detects data gaps (prints warnings)
- Validates OHLC relationships
- Skips invalid/Nan data
- Reports statistics at completion

## Statistics Reported

When export completes, the script reports:
- Total bars processed
- Gaps detected
- Invalid data skipped
- Final file size

## Error Handling

The script includes comprehensive error handling for:
- File creation failures
- Write errors
- Timezone conversion failures
- Invalid data conditions

## Source Code Location

The original C# source code is available in:
- `QTSW2/data exporter.txt` (text format)
- Or as a NinjaTrader indicator file (`.cs`)

---

**Note**: This script is used to export data from NinjaTrader, which is then processed by the QTSW2 Data Translator to create standardized Parquet files for analysis.




