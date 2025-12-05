# Translator Data Transformations - Complete List

## Overview

This document lists **every change** the translator makes to your raw CSV data.

---

## Complete Transformation List

### 1. **Column Structure Changes**

#### Before (Raw CSV):
```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
2024-11-28,23:00:00,6266.00,6266.25,6265.75,6266.25,106.0,ES
```

#### After (Processed):
```
timestamp,open,high,low,close,volume,instrument,source,interval,synthetic
2024-11-28 17:00:00-06:00,6266.00,6266.25,6265.75,6266.25,106.0,ES,translator:DataExport_ES_...csv,1min,False
```

**Changes:**
- ✅ Combined `Date` + `Time` → single `timestamp` column
- ✅ Lowercased all column names (`Open` → `open`)
- ✅ Removed original `Date` and `Time` columns
- ✅ Added 3 new schema columns: `source`, `interval`, `synthetic`

---

### 2. **Timestamp Changes**

#### Timezone Conversion:
- ✅ **UTC → Chicago**: `23:00:00` (UTC) → `17:00:00-06:00` (Chicago)
- ✅ **Timezone-aware**: Timestamps now have timezone information (`-06:00`)
- ✅ **Preserves moment**: Same actual time, just different representation

#### Format Changes:
- ✅ **Combined format**: `Date,Time` → single `timestamp` column
- ✅ **ISO8601 format**: `2024-11-28 17:00:00-06:00` (when saved to CSV)

---

### 3. **Data Type Changes**

#### Numeric Columns:
- ✅ **Type enforcement**: All OHLC/volume converted to `float64`
- ✅ **Validation**: Invalid numbers become `NaN` (and rows removed)

#### Timestamp Column:
- ✅ **Type**: String in CSV → datetime64[ns] (timezone-aware)
- ✅ **Format**: Changes based on output format (Parquet vs CSV)

#### Instrument Column:
- ✅ **Type**: Converted to `string` type
- ✅ **Overridden**: Uses filename instead of CSV value

---

### 4. **Data Filtering & Cleaning**

#### Rows Removed:
- ✅ **Invalid timestamps**: Rows where timestamp parsing fails
- ✅ **Missing timestamps**: Rows with `NaN` timestamps
- ✅ **Invalid numbers**: Rows with non-numeric OHLC data (becomes NaN → removed)

#### Data Sorting:
- ✅ **Always sorted**: Data sorted by timestamp (ascending)
- ✅ **Deterministic**: Same input → same output order

---

### 5. **Columns Added**

#### New Schema Columns:
1. **`source`** - Always `"translator:{filename}"`
   - Example: `"translator:DataExport_ES_20251129_195234_UTC.csv"`
   - Tracks where data came from

2. **`interval`** - Always `"1min"`
   - Indicates bar frequency
   - Always 1-minute bars

3. **`synthetic`** - Always `False`
   - Indicates if bars are real or reconstructed
   - Always `False` (real bars)

#### Metadata (not in schema):
- `frequency` = `'1min'` (stored in DataFrame attributes)
- `data_type` = `'minute'` (stored in DataFrame attributes)

---

### 6. **Columns Removed**

#### Removed from Output:
- ❌ `Date` column (combined into `timestamp`)
- ❌ `Time` column (combined into `timestamp`)
- ❌ `contract` column (removed during schema enforcement)
- ❌ Any extra columns from CSV (filtered out)

---

### 7. **Instrument Symbol Changes**

#### Overridden by Filename:
- ✅ **CSV Instrument column**: Ignored
- ✅ **Filename used instead**: Extracts from filename
- ✅ **Examples:**
  - `DataExport_ES_*.csv` → `"ES"`
  - `DataExport_CL_*.csv` → `"CL"`
  - `DataExport_NQ_*.csv` → `"NQ"`

---

### 8. **Data Validation & Enforcement**

#### Schema Enforcement:
- ✅ **Exact column order**: Always 10 columns in specific order
- ✅ **Type checking**: Validates all types match schema
- ✅ **Required columns**: Adds missing schema columns
- ✅ **Removes extra columns**: Only schema columns allowed

#### Validation Rules:
- ✅ **Timestamp**: Must be valid datetime
- ✅ **OHLC**: Must be numeric (float64)
- ✅ **Instrument**: Must be non-empty string
- ✅ **All schema columns**: Must exist and match types

---

### 9. **File Organization Changes**

#### If `separate_years=True`:
- ✅ **Split by year**: One file per year
- ✅ **Filename format**: `ES_2024_DataExport_ES_...parquet`
- ✅ **Filtered**: Can filter specific years only

#### Output Format:
- ✅ **Parquet**: Native datetime types, compressed
- ✅ **CSV**: Timestamp formatted as ISO8601 string
- ✅ **Both**: Creates both formats

---

### 10. **Timestamp Format Changes (CSV Export)**

#### In Parquet:
- ✅ **Native datetime**: Timezone-aware datetime64 type

#### In CSV:
- ✅ **ISO8601 string**: `2024-11-28 17:00:00-06:00`
- ✅ **Timezone offset**: Includes `-06:00` (or `-05:00` in summer)

---

## Complete Before/After Example

### INPUT (Raw CSV):
```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
2024-11-28,23:00:00,6266.00,6266.25,6265.75,6266.25,106.0,ES
2024-11-28,23:01:00,6266.25,6266.50,6265.50,6265.50,293.0,ES
```

### OUTPUT (Processed Parquet/CSV):
```
timestamp                  open     high     low      close    volume  instrument  source                                      interval  synthetic
2024-11-28 17:00:00-06:00  6266.00  6266.25  6265.75  6266.25  106.0   ES          translator:DataExport_ES_20251129_195234_UTC.csv  1min      False
2024-11-28 17:01:00-06:00  6266.25  6266.50  6265.50  6265.50  293.0   ES          translator:DataExport_ES_20251129_195234_UTC.csv  1min      False
```

### All Changes Made:
1. ✅ Combined Date+Time → timestamp
2. ✅ Converted UTC → Chicago timezone
3. ✅ Lowercased column names
4. ✅ Added source, interval, synthetic columns
5. ✅ Overrode instrument with filename value
6. ✅ Converted to float64 types
7. ✅ Sorted by timestamp
8. ✅ Validated schema

---

## Summary: What Changes

### Columns:
- **Combined**: Date + Time → timestamp
- **Renamed**: Open → open, High → high, etc.
- **Added**: source, interval, synthetic
- **Removed**: Date, Time, contract (if present)

### Data:
- **Timezone**: UTC → Chicago (timezone-aware)
- **Types**: String → float64 for prices, datetime for timestamps
- **Format**: Standardized to exact schema
- **Order**: Sorted by timestamp

### Structure:
- **Schema**: Exact 10-column schema enforced
- **Validation**: All data validated before export
- **Organization**: Can split by year if requested

---

## What Does NOT Change

### Preserved:
- ✅ **OHLC values**: Prices stay the same
- ✅ **Volume**: Volume stays the same
- ✅ **Row count**: All valid rows preserved (only invalid removed)
- ✅ **Actual time**: Moment in time preserved (just timezone labeled)
- ✅ **Original files**: Input files never modified

### Never Changes:
- ❌ **Price values**: OHLC are never modified
- ❌ **Volume**: Never modified
- ❌ **Row order**: Only sorted, never reordered randomly
- ❌ **Data values**: Only structure/format changes

---

## Data Integrity

All transformations are:
- ✅ **Deterministic**: Same input → same output
- ✅ **Reversible**: Can trace back to original CSV
- ✅ **Validated**: Schema validation ensures correctness
- ✅ **Safe**: Original files never modified






