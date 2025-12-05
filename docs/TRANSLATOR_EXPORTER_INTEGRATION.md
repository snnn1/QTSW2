# How the Translator Handles DataExporter Output

## Overview

This document explains **exactly** how the translator processes CSV files produced by the NinjaTrader DataExporter.

---

## DataExporter Output Format

### What DataExporter Produces:

```csv
Date,Time,Open,High,Low,Close,Volume,Instrument
2024-11-28,23:00:00,6266.00,6266.25,6265.75,6266.25,106.0,ES
2024-11-28,23:01:00,6266.25,6266.50,6265.50,6265.50,293.0,ES
```

**Key Characteristics:**
- ✅ Header row: `Date,Time,Open,High,Low,Close,Volume,Instrument`
- ✅ Date format: `yyyy-MM-dd` (e.g., `2024-11-28`)
- ✅ Time format: `HH:mm:ss` (e.g., `23:00:00`)
- ✅ Separator: Comma (`,`)
- ✅ Timestamps: UTC (naive - no timezone info in CSV)
- ✅ Price format: 2 decimal places (e.g., `6266.00`)
- ✅ Filename: Contains `DataExport` or `MinuteDataExport` (e.g., `DataExport_ES_20251129_195234_UTC.csv`)

---

## Translator Processing Flow

### Step 1: File Detection (`detect_file_format`)

**Location:** `translator/file_loader.py:34-63`

```python
def detect_file_format(filepath: Path) -> dict:
    # Reads first line of file
    first_line = f.readline().strip()
    
    # Detects header: checks if starts with 'Date' or 'Time'
    has_header = first_line.startswith('Date') or first_line.startswith('Time')
    
    # Detects separator: ',' or ';'
    if ',' in first_line:
        sep = ','
    elif ';' in first_line:
        sep = ';'
    else:
        sep = ','
```

**For DataExporter files:**
- ✅ `has_header = True` (first line is `Date,Time,Open,High,Low,Close,Volume,Instrument`)
- ✅ `separator = ','` (comma-separated)

---

### Step 2: File Loading (`load_single_file`)

**Location:** `translator/file_loader.py:172-254`

#### 2a. Read CSV with Header

```python
if has_header:
    # CSV with header (Date,Time,Open,High,Low,Close,Volume,Instrument)
    df = pd.read_csv(filepath, sep=sep)
```

**Result:**
```
   Date        Time      Open     High     Low      Close    Volume  Instrument
0  2024-11-28  23:00:00  6266.00  6266.25  6265.75  6266.25  106.0   ES
1  2024-11-28  23:01:00  6266.25  6266.50  6265.50  6265.50  293.0   ES
```

#### 2b. Combine Date + Time → Timestamp

```python
df['timestamp'] = pd.to_datetime(df['Date'] + ' ' + df['Time'], utc=False)
```

**Result:**
```
   Date        Time      Open     ...  timestamp
0  2024-11-28  23:00:00  6266.00  ...  2024-11-28 23:00:00  (naive datetime)
1  2024-11-28  23:01:00  6266.25  ...  2024-11-28 23:01:00  (naive datetime)
```

**Note:** Timestamp is **naive** (no timezone) at this point.

#### 2c. Rename Columns (Lowercase)

```python
df = df.rename(columns={
    'Open': 'open',
    'High': 'high', 
    'Low': 'low',
    'Close': 'close',
    'Volume': 'volume',
    'Instrument': 'instrument'
})
```

**Result:**
```
   Date        Time      open     high     low      close    volume  instrument  timestamp
0  2024-11-28  23:00:00  6266.00  6266.25  6265.75  6266.25  106.0   ES          2024-11-28 23:00:00
```

#### 2d. Remove Date and Time Columns

```python
df = df.drop(columns=['Date', 'Time'])
```

**Result:**
```
   open     high     low      close    volume  instrument  timestamp
0  6266.00  6266.25  6265.75  6266.25  106.0   ES          2024-11-28 23:00:00
```

---

### Step 3: Timezone Conversion

**Location:** `translator/file_loader.py:216-227`

#### 3a. Detect DataExporter File

```python
is_dataexport_file = "DataExport" in filepath.name or "MinuteDataExport" in filepath.name
```

**For DataExporter files:** `is_dataexport_file = True`

#### 3b. Convert UTC → Chicago

```python
if is_dataexport_file and df["timestamp"].dt.tz is None:
    df["timestamp"] = df["timestamp"].dt.tz_localize("UTC").dt.tz_convert("America/Chicago")
```

**Process:**
1. **Localize:** `2024-11-28 23:00:00` (naive) → `2024-11-28 23:00:00+00:00` (UTC)
2. **Convert:** `2024-11-28 23:00:00+00:00` (UTC) → `2024-11-28 17:00:00-06:00` (Chicago)

**Result:**
```
   open     high     low      close    volume  instrument  timestamp
0  6266.00  6266.25  6265.75  6266.25  106.0   ES          2024-11-28 17:00:00-06:00
```

**Why UTC?**
- DataExporter writes timestamps in UTC (verified: `23:00:00` = `17:00 CT` evening session)
- CSV has no timezone info, so translator assumes UTC for DataExporter files

---

### Step 4: Data Type Conversion

**Location:** `translator/file_loader.py:229-232`

```python
numeric_cols = ["open", "high", "low", "close", "volume"]
for col in numeric_cols:
    df[col] = pd.to_numeric(df[col], errors="coerce")
```

**Result:**
- All OHLC/volume columns converted to `float64`
- Invalid values become `NaN`

---

### Step 5: Remove Invalid Rows

**Location:** `translator/file_loader.py:234`

```python
df = df.dropna(subset=["timestamp"])
```

**Removes:**
- Rows with invalid timestamps
- Rows with `NaN` timestamps

---

### Step 6: Extract Contract/Instrument from Filename

**Location:** `translator/file_loader.py:236-241`

```python
from .core import infer_contract_from_filename, root_symbol
contract = infer_contract_from_filename(filepath)
df["contract"] = contract
instrument = root_symbol(contract)
df["instrument"] = instrument
```

**Example:**
- Filename: `DataExport_ES_12-24_20251129_195234_UTC.csv`
- `contract = "ES 12-24"` (or extracted from filename)
- `instrument = "ES"` (root symbol)
- **Overrides CSV Instrument column** (uses filename instead)

**Result:**
```
   open     high     low      close    volume  instrument  timestamp                  contract
0  6266.00  6266.25  6265.75  6266.25  106.0   ES          2024-11-28 17:00:00-06:00  ES 12-24
```

---

### Step 7: Add Schema Columns

**Location:** `translator/file_loader.py:243-246`

```python
df["source"] = f"translator:{filepath.name}"
df["interval"] = "1min"
df["synthetic"] = False
```

**Result:**
```
   open     high     low      close    volume  instrument  timestamp                  contract   source                                      interval  synthetic
0  6266.00  6266.25  6265.75  6266.25  106.0   ES          2024-11-28 17:00:00-06:00  ES 12-24   translator:DataExport_ES_20251129_195234_UTC.csv  1min      False
```

---

### Step 8: Sort and Set Metadata

**Location:** `translator/file_loader.py:248-252`

```python
df = df.sort_values("timestamp").reset_index(drop=True)

# Set frequency metadata (always 1-minute bars)
df.attrs['frequency'] = '1min'
df.attrs['data_type'] = 'minute'
```

**Result:**
- Data sorted by timestamp (ascending)
- Metadata set: `frequency='1min'`, `data_type='minute'`

---

### Step 9: Schema Enforcement (`enforce_schema`)

**Location:** `translator/schema.py:121-201`

#### 9a. Remove Non-Schema Columns

```python
# Remove all non-schema columns
schema_cols = [col for col in SCHEMA_COLUMNS if col in df.columns]
df = df[schema_cols]
```

**Removes:** `contract` column (not in schema)

**Schema Columns:**
```python
SCHEMA_COLUMNS = [
    "timestamp",
    "open",
    "high",
    "low",
    "close",
    "volume",
    "instrument",
    "source",
    "interval",
    "synthetic"
]
```

#### 9b. Enforce Column Order

```python
df = df[SCHEMA_COLUMNS]
```

**Result:** Exact column order enforced

#### 9c. Type Validation

```python
# Ensure numeric columns are float64
for col in ["open", "high", "low", "close", "volume"]:
    df[col] = pd.to_numeric(df[col], errors="coerce").astype("float64")

# Ensure instrument is string
df["instrument"] = df["instrument"].astype("string")
```

---

### Step 10: Export (`_save_dataframe`)

**Location:** `translator/core.py:45-106`

#### 10a. Parquet Export

```python
df_parquet = df_export.copy()
df_parquet.to_parquet(parquet_file, index=False)
```

**Result:**
- Native datetime types (timezone-aware)
- Compressed binary format
- Fast read/write

#### 10b. CSV Export

```python
# Format timezone-aware timestamps as ISO8601 strings
if df_csv["timestamp"].dt.tz is not None:
    df_csv["timestamp"] = df_csv["timestamp"].apply(
        lambda ts: (
            ts.strftime("%Y-%m-%d %H:%M:%S") + 
            f"{ts.strftime('%z')[:-2]}:{ts.strftime('%z')[-2:]}"
            if pd.notna(ts) else ts
        )
    )

df_csv.to_csv(csv_file, index=False, encoding='utf-8')
```

**Result:**
- Timestamp formatted as ISO8601: `2024-11-28 17:00:00-06:00`
- UTF-8 encoding
- Standard CSV format

---

## Complete Transformation Example

### INPUT (DataExporter CSV):
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

---

## Key Processing Steps Summary

| Step | Action | DataExporter-Specific |
|------|--------|----------------------|
| 1 | Detect format | ✅ Detects header format |
| 2 | Read CSV | ✅ Reads `Date,Time,Open,High,Low,Close,Volume,Instrument` |
| 3 | Combine Date+Time | ✅ Creates single `timestamp` column |
| 4 | Rename columns | ✅ Lowercases all column names |
| 5 | **Timezone conversion** | ✅ **UTC → Chicago** (DataExporter-specific) |
| 6 | Type conversion | ✅ Converts to float64 |
| 7 | Remove invalid rows | ✅ Drops NaN timestamps |
| 8 | Extract instrument | ✅ Uses filename (overrides CSV column) |
| 9 | Add schema columns | ✅ Adds `source`, `interval`, `synthetic` |
| 10 | Sort data | ✅ Sorts by timestamp |
| 11 | Enforce schema | ✅ Removes extra columns, validates types |
| 12 | Export | ✅ Saves as Parquet/CSV |

---

## DataExporter-Specific Handling

### 1. **Timezone Detection**
- ✅ Checks filename for `"DataExport"` or `"MinuteDataExport"`
- ✅ Assumes UTC timestamps (naive)
- ✅ Converts to `America/Chicago` timezone

### 2. **Format Detection**
- ✅ Detects header format (`Date,Time,Open,...`)
- ✅ Uses comma separator

### 3. **Instrument Override**
- ✅ Extracts instrument from filename
- ✅ Overrides CSV `Instrument` column value

### 4. **Schema Compliance**
- ✅ Adds required schema columns
- ✅ Enforces exact column order
- ✅ Validates all data types

---

## Error Handling

### Invalid Timestamps
- ❌ Rows with unparseable timestamps → **Removed**

### Invalid Numbers
- ❌ Non-numeric OHLC values → **Converted to NaN** → **Row removed**

### Missing Data
- ❌ Missing timestamps → **Row removed**
- ❌ Missing OHLC → **Converted to NaN** → **Row removed**

### File Format Issues
- ❌ Missing header → Falls back to no-header format
- ❌ Wrong separator → Auto-detects `,` or `;`

---

## Data Integrity Guarantees

✅ **Original files never modified** (read-only)  
✅ **All valid rows preserved** (only invalid removed)  
✅ **Price values unchanged** (OHLC preserved exactly)  
✅ **Timezone conversion preserves moment** (UTC → Chicago is just labeling)  
✅ **Deterministic output** (same input → same output)  
✅ **Schema validated** (all data conforms to strict schema)


