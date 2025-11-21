# DataExporter ↔ Conductor Integration

## Overview

This document describes how the **DataExporter** (NinjaTrader indicator) integrates with the **Pipeline Conductor** (scheduler).

## Integration Flow

```
1. Conductor launches NinjaTrader
   ↓
2. DataExporter auto-triggers at 07:00 CT (or manual trigger)
   ↓
3. DataExporter creates start signal file
   ↓
4. DataExporter exports CSV + updates progress file
   ↓
5. DataExporter creates completion signal file
   ↓
6. Conductor detects completion signal
   ↓
7. Conductor triggers Translator → Analyzer
```

## Signal Files

### 1. Start Signal
**File Pattern:** `export_start_YYYYMMDD_HHMMSS.json`

**Created:** When export begins

**Contents:**
```json
{
  "status": "started",
  "instrument": "ES",
  "dataType": "Minute",
  "startedAt": "2025-11-06T07:00:00.000Z"
}
```

### 2. Progress Signal
**File Pattern:** `export_progress_{CSV_FILENAME}.json`

**Created:** Every 100,000 records during export

**Contents:**
```json
{
  "status": "in_progress",
  "filePath": "C:\\Users\\jakej\\QTSW2\\data\\raw\\MinuteDataExport_ES_20251106_070000_UTC.csv",
  "fileName": "MinuteDataExport_ES_20251106_070000_UTC.csv",
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

### 3. Completion Signal
**File Pattern:** `export_complete_{CSV_FILENAME}.json`

**Created:** When export finishes successfully

**Contents:**
```json
{
  "status": "complete",
  "filePath": "C:\\Users\\jakej\\QTSW2\\data\\raw\\MinuteDataExport_ES_20251106_070000_UTC.csv",
  "fileName": "MinuteDataExport_ES_20251106_070000_UTC.csv",
  "dataType": "Minute",
  "instrument": "ES",
  "totalBarsProcessed": 1000000,
  "gapsDetected": 2,
  "invalidDataSkipped": 0,
  "fileSizeBytes": 104857600,
  "fileSizeMB": 100.0,
  "completedAt": "2025-11-06T07:30:00.000Z"
}
```

## Conductor Detection Logic

The conductor uses a **multi-layered detection approach**:

### Priority 1: Completion Signals (Most Reliable)
- Checks for `export_complete_*.json` files
- Reads signal file to get export details
- Returns immediately when found

### Priority 2: Progress Signals
- Checks for `export_progress_*.json` files
- Logs progress information
- Continues monitoring

### Priority 3: File Modification
- Monitors CSV file modification times
- Detects files modified within last 2 minutes
- Falls back if signals not available

### Priority 4: File Growth Detection
- Tracks file sizes over time
- Detects stalled exports (no growth for 5+ minutes)
- Warns if export appears stuck

## Path Configuration

**DataExporter exports to:**
```
C:\Users\jakej\QTSW2\data\raw
```

**Conductor monitors:**
```python
DATA_RAW = QTSW2_ROOT / "data" / "raw"
```

✅ **Paths are now aligned!**

## Error Detection

### Stalled Export Detection
- Monitors file size every 5 minutes
- Warns if file stops growing without completion signal
- Helps identify failed/crashed exports

### Timeout Handling
- Default timeout: 60 minutes
- Logs progress every 5 minutes
- Returns False if no completion detected

## Benefits

1. **Reliable Detection**: Completion signals provide definitive export status
2. **Progress Tracking**: Real-time visibility into export progress
3. **Error Detection**: Identifies stalled or failed exports
4. **Better Logging**: Detailed information about exports
5. **No False Positives**: Only proceeds when export is truly complete

## Troubleshooting

### Export not detected
- Check that `data/raw` folder exists
- Verify DataExporter is writing to correct path
- Check for completion signal files
- Review conductor logs for detection attempts

### Export detected but incomplete
- Check progress files for current status
- Verify file is still growing (not stalled)
- Check NinjaTrader logs for errors

### Multiple exports
- Conductor processes all completion signals
- Translator handles multiple files
- Each export gets its own signal file










