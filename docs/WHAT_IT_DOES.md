# 📊 Raw Data Translator - What It Does

## 🎯 **Purpose**

The Raw Data Translator converts **messy, raw trading data files** (from NinjaTrader or other sources) into **clean, organized, standardized files** that your trading system can use.

Think of it as a **data cleaning and organizing tool** - it takes whatever format you export from your trading platform and converts it into a consistent, usable format.

---

## 📥 **INPUT: What You Give It**

### Raw Data Files (from `data_raw/` folder):
- **CSV files** (.csv) with trading data exports
- **Text files** (.txt) with historical data
- **Data files** (.dat) from various sources

### Common Formats It Handles:

#### Format 1: Headered CSV
```
Date,Time,Open,High,Low,Close,Volume,Instrument
2024-01-15,09:30,4825.00,4826.50,4824.25,4825.75,1250000,ES
2024-01-15,09:31,4825.75,4826.00,4825.50,4825.50,980000,ES
```

#### Format 2: No Header (Raw Timestamp Format)
```
20240115 093000,4825.00,4826.50,4824.25,4825.75,1250000
20240115 093100,4825.75,4826.00,4825.50,4825.50,980000
```

#### Format 3: Various Separators
- Comma-separated (`,`)
- Semicolon-separated (`;`)
- Tab-separated

---

## 🔄 **PROCESSING: What It Does**

### Step 1: **Auto-Detection**
- ✅ Detects if file has headers or not
- ✅ Detects separator type (comma, semicolon, etc.)
- ✅ Automatically handles different formats

### Step 2: **Data Loading**
- Reads all files from your input folder
- Parses date/time columns correctly
- Handles multiple file formats simultaneously

### Step 3: **Timezone Conversion**
- **Converts UTC → Chicago Time (America/Chicago)**
- Ensures all timestamps are in trading session timezone
- Critical for accurate trading session calculations

### Step 4: **Data Cleaning**
- ✅ Removes duplicate rows
- ✅ Validates numeric columns (open, high, low, close, volume)
- ✅ Filters out invalid timestamps
- ✅ Standardizes column names (Open → open, High → high, etc.)

### Step 5: **Instrument Detection**
- Extracts instrument symbol from filename or data
- Examples:
  - `MinuteDataExport_ES_2024.csv` → `ES`
  - `NQ_Futures_Data.csv` → `NQ`
  - `YM_Mini.csv` → `YM`

### Step 6: **Data Organization**
Sorts by timestamp and groups by instrument

---

## 📤 **OUTPUT: What You Get**

### Standardized Data Format:
Every output file has this structure:
```python
timestamp          open     high     low      close    volume   instrument  contract
2024-01-15 09:30   4825.00  4826.50  4824.25  4825.75  1250000  ES          ES_Mar2024
2024-01-15 09:31   4825.75  4826.00  4825.50  4825.50  980000   ES          ES_Mar2024
```

### Output Options:

#### Option 1: **Separated by Year & Instrument**
```
data_processed/
├── ES_2024.parquet    (ES data for 2024 only)
├── ES_2025.parquet    (ES data for 2025 only)
├── NQ_2024.parquet    (NQ data for 2024 only)
├── NQ_2025.parquet    (NQ data for 2025 only)
└── ...
```

#### Option 2: **Complete Merged File**
```
data_processed/
├── ES_NQ_2024-2025.parquet  (All ES and NQ data, 2024-2025)
└── ES_NQ_2024-2025.csv      (Same data in CSV format)
```

### File Formats:
- **Parquet** (`.parquet`) - Fast, compressed, best for analysis
- **CSV** (`.csv`) - Human-readable, compatible with everything
- **Both** - Creates both formats

---

## 🎯 **Why This Matters**

### Before Translation:
- ❌ Files in different formats
- ❌ Mixed timezones (UTC, local, etc.)
- ❌ Inconsistent column names
- ❌ Duplicate data
- ❌ Hard to process programmatically

### After Translation:
- ✅ All files in same format
- ✅ All timestamps in Chicago time
- ✅ Standardized column names
- ✅ No duplicates
- ✅ Ready for your trading system to use

---

## 📊 **Real-World Example**

### Scenario:
You export 5 years of ES futures data from NinjaTrader in 3 different formats:
- `ES_2020-2022.csv` (header format)
- `MinuteDataExport_ES_2023.txt` (no header)
- `ES_Data_2024.dat` (semicolon separated)

### What the Translator Does:

1. **Reads all 3 files** automatically
2. **Detects each format** correctly
3. **Converts timestamps** from UTC to Chicago time
4. **Removes duplicates** (if any overlap between files)
5. **Organizes by year**: Creates `ES_2020.parquet`, `ES_2021.parquet`, etc.
6. **Also creates merged file**: `ES_2020-2024.parquet`

### Result:
Your trading system can now easily:
- Load specific years: `pd.read_parquet("ES_2024.parquet")`
- Load everything: `pd.read_parquet("ES_2020-2024.parquet")`
- Analyze with confidence (correct timezones, clean data)

---

## 🔧 **Features**

### 1. **Smart Format Detection**
- No need to specify format - it figures it out automatically
- Handles multiple formats in one batch

### 2. **Year Selection** (GUI App)
- Process only specific years (e.g., 2024, 2025)
- Useful for large datasets (6M+ rows)

### 3. **File Selection** (GUI App)
- Choose which files to process
- Skip files you don't need

### 4. **Progress Tracking**
- Shows real-time progress
- Estimates completion time
- Displays file-by-file status

### 5. **Data Preview**
- Preview data before processing
- Verify format detection
- Check date ranges and instruments

---

## 🚀 **Two Ways to Use It**

### Method 1: **GUI App** (Easiest)
1. Double-click `Data Translator App.bat`
2. Web browser opens with interactive interface
3. Select files, configure options, click "Start Processing"
4. Watch progress in real-time

### Method 2: **Command Line** (Advanced)
```bash
python tools/translate_raw.py --input data_raw --output data_processed --separate-years --format parquet
```

---

## 📋 **Summary**

**The Raw Data Translator:**
- 🔄 Converts raw trading data → clean, organized files
- 🌍 Fixes timezone issues (UTC → Chicago)
- 🧹 Removes duplicates and invalid data
- 📊 Standardizes format across all files
- 📁 Organizes by instrument and year
- ✅ Makes data ready for your trading system

**It's the first step** in your data pipeline - preparing raw exports for analysis and backtesting.

