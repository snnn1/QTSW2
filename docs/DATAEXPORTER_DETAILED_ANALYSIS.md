# DataExporter.cs - Detailed Code Analysis

## Overview
The `DataExporter` is a **NinjaTrader 8 indicator** that exports 1-minute OHLCV bar data to CSV files. It's designed to run during historical data processing and integrates with the QTSW2 pipeline via signal files.

---

## Core Functionality

### 1. **Data Export Format**
- **Output**: CSV files with format: `DataExport_{INSTRUMENT}_{TIMESTAMP}.csv`
- **Location**: `C:\Users\jakej\QTSW2\data\raw\`
- **Header**: `Date,Time,Open,High,Low,Close,Volume,Instrument`
- **Data Row Format**: `YYYY-MM-DD,HH:mm:ss,Open,High,Low,Close,Volume,Instrument`

### 2. **Time Convention Fix** (Critical)
```csharp
// Line 464-466: NinjaTrader timestamps bars with CLOSE time
// We need OPEN time, so subtract 1 minute
DateTime exportTime = Time[0].AddMinutes(-1);
```
- **NinjaTrader Behavior**: Bars are timestamped with their **close time**
- **Export Behavior**: Exports the **open time** (subtracts 1 minute)
- **Example**: A bar closing at `09:31:00` is exported as `09:30:00`

---

## Export Triggers

### 1. **Trigger On Open** (Default: Enabled)
- **Property**: `TriggerOnOpen` (line 31, 564-568)
- **Behavior**: Automatically starts export when historical data loads (line 70-74)
- **State**: Must be in `State.Historical`

### 2. **Manual Export Trigger**
- **Property**: `ManualExportTrigger` (line 29, 556-561)
- **Behavior**: User checks checkbox in indicator properties, export starts on next bar update (line 370-375)
- **Reset**: Automatically resets after trigger (line 377-380)

### 3. **External File Trigger**
- **File**: `C:\Users\jakej\QTSW2\data\raw\export_trigger.txt`
- **Behavior**: Checks for this file on every bar update (line 382, 172-211)
- **Action**: Deletes trigger file and starts export if in historical state
- **Use Case**: Allows external scripts (like scheduler) to trigger exports

---

## Data Validation

### 1. **NaN Detection** (Line 406-413)
```csharp
if (double.IsNaN(Open[0]) || double.IsNaN(High[0]) || ...)
```
- Checks all OHLCV values for NaN
- Skips invalid bars
- Tracks count in `invalidDataSkipped`
- Prints warning (first 10 occurrences)

### 2. **OHLC Relationship Validation** (Line 416-423)
```csharp
if (High[0] < Low[0] || High[0] < Open[0] || ...)
```
- Validates: `High >= Low`, `High >= Open`, `High >= Close`
- Validates: `Low <= Open`, `Low <= Close`
- Skips invalid bars
- Prints warning with actual values (first 10 occurrences)

### 3. **Gap Detection** (Line 426-446)
- **Gap Threshold**: > 1.5 minutes between consecutive bars
- **Behavior**: 
  - Detects gaps and increments `gapsDetected`
  - Prints gap details (first 20 gaps)
  - Shows: last exported time, next bar time, minutes missing
- **Small Time Difference Warning**: < 0.5 minutes (line 442-445)

---

## Duplicate Export Prevention

### 1. **Progress File Check** (Line 268-286)
- **Pattern**: `export_progress_DataExport_{INSTRUMENT}_*.json`
- **Location**: `data/raw/logs/`
- **Logic**: If progress file updated < 300 seconds ago, skip export
- **Purpose**: Prevents duplicate exports if indicator reloads

### 2. **Recent File Check** (Line 288-303)
- **Pattern**: `DataExport_{INSTRUMENT}_*.csv`
- **Logic**: If file created < 120 seconds ago, skip export
- **Purpose**: Prevents duplicate exports if multiple instances run

---

## File Management

### 1. **File Size Limit**
- **Max Size**: 500 MB (line 20)
- **Warning**: Prints warning when approaching limit (line 504-507)
- **Note**: No automatic file splitting - just warns

### 2. **Flushing Strategy**
- **Every 10,000 records**: Flushes stream to disk (line 484-494)
- **Purpose**: Ensures data is written even if process crashes

### 3. **Progress Updates**
- **Every 100,000 records**: Updates progress JSON file (line 497-553)
- **Progress File**: `export_progress_{FILENAME}.json`
- **Contains**: Status, file path, bars processed, gaps, file size, last bar time

---

## Signal Files (Integration with Pipeline)

### 1. **Start Signal** (Line 318-338)
- **File**: `export_start_{TIMESTAMP}.json`
- **Location**: `data/raw/logs/`
- **Created**: When export begins
- **Contents**:
  ```json
  {
    "status": "started",
    "instrument": "ES",
    "dataType": "Minute",
    "startedAt": "2025-11-06T07:00:00.000Z"
  }
  ```

### 2. **Progress Signal** (Line 496-553)
- **File**: `export_progress_{CSV_FILENAME}.json`
- **Location**: `data/raw/logs/`
- **Updated**: Every 100,000 records
- **Contents**:
  ```json
  {
    "status": "in_progress",
    "filePath": "...",
    "fileName": "...",
    "dataType": "Minute",
    "instrument": "ES",
    "totalBarsProcessed": 500000,
    "gapsDetected": 2,
    "invalidDataSkipped": 0,
    "fileSizeBytes": 52428800,
    "fileSizeMB": 50.0,
    "lastUpdateTime": "2025-11-06T07:15:30.000Z",
    "lastBarTime": "2025-11-06T07:15:00"
  }
  ```

### 3. **Completion Signal** (Line 106-140)
- **File**: `export_complete_{CSV_FILENAME}.json`
- **Location**: `data/raw/logs/`
- **Created**: When export finishes (in `State.Terminated`)
- **Contents**:
  ```json
  {
    "status": "complete",
    "filePath": "...",
    "fileName": "...",
    "dataType": "Minute",
    "instrument": "ES",
    "totalBarsProcessed": 1000000,
    "gapsDetected": 5,
    "invalidDataSkipped": 2,
    "fileSizeBytes": 104857600,
    "fileSizeMB": 100.0,
    "completedAt": "2025-11-06T08:00:00.000Z"
  }
  ```

### 4. **Progress File Cleanup** (Line 142-156)
- **Behavior**: Deletes progress file when export completes
- **Purpose**: Clean up temporary files

---

## State Management

### 1. **Export States**
- `exportInProgress`: True when actively exporting (line 24)
- `exportStarted`: True once export has begun (line 26)
- `exportCompleted`: True when export finished (line 25)

### 2. **State Transitions**
- **SetDefaults**: Initializes properties (line 35-43)
- **Active**: Prints instructions (line 48-59)
- **Historical**: 
  - Validates 1-minute chart (line 62-68)
  - Auto-triggers if `TriggerOnOpen` enabled (line 70-74)
- **Terminated**: 
  - Closes file stream (line 82-88)
  - Creates completion signal (line 106-140)
  - Prints summary statistics (line 90-104)

---

## Statistics Tracking

### Counters (Line 17-19)
- `totalBarsProcessed`: Total bars written to file
- `gapsDetected`: Number of time gaps > 1.5 minutes
- `invalidDataSkipped`: Bars skipped due to validation failures

### Final Summary (Line 90-104)
Printed when export completes:
```
=== EXPORT COMPLETE ===
File: {filePath}
Data Type: Minute
Total bars processed: {totalBarsProcessed:N0}
Gaps detected: {gapsDetected}
Invalid data skipped: {invalidDataSkipped}
Export completed successfully!
Final file size: {size} MB
```

---

## Error Handling

### 1. **File Creation Errors** (Line 349-361)
- Catches exceptions when creating file
- Prints error message
- Resets export flags
- Returns without crashing

### 2. **Write Errors** (Line 470-478)
- Catches exceptions when writing data
- Prints error with timestamp
- Continues processing (doesn't abort)

### 3. **Signal File Errors** (Line 137-140, 153-156, 336-338, 541-544)
- Wraps signal file operations in try-catch
- Prints warnings but doesn't abort export
- Export continues even if signal files fail

---

## Chart Requirements

### 1. **Must be 1-Minute Chart** (Line 21, 62-68, 252-256)
- **Validation**: Checks `BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value == 1`
- **Error**: Prints error and aborts if not 1-minute
- **Reason**: Designed specifically for minute data export

### 2. **Historical Data Only** (Line 246-250, 366-367)
- **Requirement**: Must be in `State.Historical`
- **Warning**: Prints warning if export attempted in other states
- **Reason**: Processes historical bars, not real-time data

---

## Instrument Name Handling

### 1. **Extraction** (Line 258-262)
```csharp
string instrumentName = Instrument?.MasterInstrument?.Name ?? "UNKNOWN";
if (instrumentName.Contains(" "))
{
    instrumentName = instrumentName.Split(' ')[0];
}
```
- Gets master instrument name
- If contains space, takes first word only
- **Example**: "ES 12-24" → "ES"

### 2. **Usage**
- Used in filename: `DataExport_{instrumentName}_{timestamp}.csv`
- Included in CSV data rows
- Included in signal files

---

## Key Design Decisions

### 1. **Why CSV?**
- Universal format (no dependencies)
- Easy to debug (open in Excel)
- Translator handles validation/processing
- See `EXPORT_PARQUET_VS_CSV.md` for rationale

### 2. **Why Signal Files?**
- Allows external monitoring (scheduler/conductor)
- Progress tracking for long exports
- Completion detection for pipeline automation

### 3. **Why Time Adjustment?**
- NinjaTrader uses bar close time
- Standard convention is bar open time
- Subtracting 1 minute aligns with expected format

### 4. **Why Duplicate Prevention?**
- Indicator may reload during export
- Multiple instances could run simultaneously
- Prevents data corruption and wasted processing

---

## Integration Points

### 1. **Pipeline Conductor** (`automation/daily_data_pipeline_scheduler.py`)
- Monitors completion signals
- Waits for exports to finish
- Triggers translator after completion

### 2. **Translator** (`translator/core.py`)
- Reads CSV files from `data/raw/`
- Validates and processes data
- Converts to Parquet format

### 3. **External Trigger**
- Scheduler can create `export_trigger.txt`
- DataExporter detects and starts export
- Enables programmatic control

---

## Limitations & Notes

### 1. **File Size**
- 500 MB warning but no automatic splitting
- Large exports may exceed this limit

### 2. **No Real-Time Export**
- Only processes historical data
- `OnMarketData` is empty (line 570-573)

### 3. **Hardcoded Path**
- Path: `C:\Users\jakej\QTSW2\data\raw` (line 176, 264)
- Not configurable via properties

### 4. **Single File Per Export**
- Creates one CSV file per export session
- No automatic file rotation

### 5. **No Compression**
- CSV files are uncompressed
- Translator may compress later

---

## Code Quality Notes

### 1. **Error Handling**
- Comprehensive try-catch blocks
- Graceful degradation (continues on signal file errors)
- Clear error messages

### 2. **Logging**
- Extensive `Print()` statements
- Progress updates every 100k records
- Final summary statistics

### 3. **State Management**
- Clear state flags
- Prevents duplicate exports
- Proper cleanup on termination

### 4. **Validation**
- Multiple validation layers
- Gap detection
- Data quality checks

---

## Summary

The `DataExporter` is a **robust, production-ready indicator** that:
1. ✅ Exports 1-minute OHLCV data to CSV
2. ✅ Validates data quality (NaN, OHLC relationships)
3. ✅ Detects and reports gaps
4. ✅ Prevents duplicate exports
5. ✅ Provides progress tracking via signal files
6. ✅ Integrates with pipeline automation
7. ✅ Handles errors gracefully
8. ✅ Adjusts timestamps (close → open time)

It's designed to be **automated** (via scheduler) but also supports **manual** and **external** triggers for flexibility.


