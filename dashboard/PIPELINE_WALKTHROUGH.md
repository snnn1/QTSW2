# Complete Pipeline Walkthrough

## 🎯 Pipeline Overview

Your pipeline processes trading data from raw exports through analysis and consolidation. Here's the complete flow:

```
Export → Translator → Analyzer → Data Merger → END
```

---

## 📋 Stage-by-Stage Breakdown

### **STAGE 0: Export (Optional - if launched with NinjaTrader)**

**Location**: `automation/daily_data_pipeline_scheduler.py` - `NinjaTraderController`

**What Happens**:
1. **Launch NinjaTrader** (if `launch_ninjatrader=True`)
   - Opens NinjaTrader 8 with the DataExport workspace
   - Waits for NinjaTrader to fully load

2. **Monitor for Exports**
   - Watches `data/raw/` folder for new CSV files
   - Looks for patterns: `MinuteDataExport_*.csv`, `TickDataExport_*.csv`, `DataExport_*.csv`
   - Monitors file growth (checks every 5 seconds)
   - Waits for completion signals in `data/raw/logs/export_complete_*.json`
   - **Timeout**: 60 minutes max

3. **Progress Tracking**
   - Emits events with:
     - Records processed
     - File size (MB)
     - File count
     - Gaps detected
     - Invalid data skipped

4. **Completion Detection**
   - File stops growing AND completion signal exists
   - Or file hasn't changed in 5+ minutes with completion signal

**Output**: Raw CSV files in `data/raw/` (e.g., `CL.csv`, `ES.csv`, `NQ.csv`)

**Events Emitted**:
- `export` stage: `start`, `metric`, `success`, `failure`

---

### **STAGE 1: Translator**

**Location**: `automation/daily_data_pipeline_scheduler.py` - `PipelineOrchestrator.run_translator()`

**What Happens**:
1. **Check for Raw Files**
   - Scans `data/raw/` for `*.csv` files
   - Excludes subdirectories (like `logs/`)
   - If no files found → **FAILURE** (pipeline stops)

2. **Run Translation Script**
   - Command: `python tools/translate_raw.py --input data/raw --output data/processed --separate-years --no-merge`
   - Processes each CSV file separately
   - Separates data by year
   - **Timeout**: 1 hour max

3. **Processing Steps** (per file):
   - Loads CSV with format detection (header/no header, separator detection)
   - Detects instrument from filename (CL, ES, NQ, YM, NG, GC)
   - Processes timestamps (timezone correction)
   - Separates by year
   - Saves as Parquet files: `{instrument}_{year}_{filename}.parquet`
   - Example: `ES_2025_ES.parquet`, `CL_2025_CL.parquet`

4. **File Cleanup**
   - Deletes processed raw CSV files after successful translation
   - Keeps only processed Parquet files

**Input**: Raw CSV files in `data/raw/` (e.g., `CL.csv`, `ES.csv`)

**Output**: Processed Parquet files in `data/processed/` (e.g., `ES_2025_ES.parquet`)

**Events Emitted**:
- `translator` stage: `start`, `metric`, `log`, `success`, `failure`
- Key events: "Files found", "File written", "Translation progress", "Translation complete"

**Failure Handling**:
- If translator fails → Pipeline stops (unless processed files already exist)

---

### **STAGE 2: Analyzer**

**Location**: `automation/daily_data_pipeline_scheduler.py` - `PipelineOrchestrator.run_analyzer()`

**What Happens**:
1. **Check for Processed Files**
   - Scans `data/processed/` for `*.parquet` and `*.csv` files
   - Groups files by instrument (ES, NQ, YM, CL, NG, GC)
   - If no files found → **SKIPPED** (pipeline continues)

2. **Run Analyzer for Each Instrument**
   - Processes instruments one at a time
   - Command: `python scripts/breakout_analyzer/scripts/run_data_processed.py --folder data/processed --instrument {instrument} --sessions S1 S2 --slots S1:07:30 S1:08:00 S1:09:00 S2:09:30 S2:10:00 S2:10:30 S2:11:00 --debug`
   - **Timeout**: 6 hours per instrument (for very large files)

3. **Processing Steps** (per instrument):
   - Loads all processed files for the instrument
   - Analyzes each date for breakout patterns
   - Processes time slots:
     - **S1 (Session 1)**: 07:30, 08:00, 09:00
     - **S2 (Session 2)**: 09:30, 10:00, 10:30, 11:00
   - Generates analysis results per date
   - Saves results to `data/analyzer_runs/{instrument}{session}/{year}/`
   - Example: `data/analyzer_runs/ES1/2025/ES1_an_2025_11.parquet`

4. **Progress Tracking**
   - Streams output in real-time
   - Emits events for milestones (but filters verbose "Completed date" messages)
   - Tracks elapsed time per instrument

**Input**: Processed Parquet files in `data/processed/` (e.g., `ES_2025_ES.parquet`)

**Output**: Analyzer results in `data/analyzer_runs/{instrument}{session}/{year}/` (e.g., `ES1_an_2025_11.parquet`)

**Events Emitted**:
- `analyzer` stage: `start`, `log`, `metric`, `success`, `failure`
- Key events: "Starting {instrument}", "Files available", "Running analyzer", "{instrument} completed"

**Failure Handling**:
- If analyzer fails for one instrument → **NON-FATAL** (continues to next instrument)
- Pipeline continues even if analyzer fails

---

### **STAGE 2.5: Data Merger**

**Location**: `automation/daily_data_pipeline_scheduler.py` - `PipelineOrchestrator.run_data_merger()`

**What Happens**:
1. **Check for Analyzer Files**
   - Looks in `data/analyzer_runs/` for analyzer output files
   - Groups by instrument-session (ES1, ES2, NQ1, NQ2, etc.)

2. **Run Data Merger**
   - Command: `python tools/data_merger.py`
   - Merges daily analyzer files into monthly files
   - Consolidates data for easier access

3. **Processing Steps**:
   - Finds all analyzer output files
   - Groups by instrument, session, and year
   - Merges daily files into monthly files
   - Saves consolidated files

**Input**: Analyzer results in `data/analyzer_runs/{instrument}{session}/{year}/`

**Output**: Merged monthly files (consolidated analyzer data)

**Events Emitted**:
- `merger` stage: `start`, `log`, `success`, `failure`

**Note**: This is where the pipeline **ENDS** (Sequential Processor is disabled)

---

## 🔄 Pipeline Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    PIPELINE START                           │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
         ┌───────────────────────┐
         │  OPTIONAL: Export     │
         │  (NinjaTrader)        │
         │  - Launch NT          │
         │  - Wait for exports   │
         │  - Monitor progress   │
         └───────────┬───────────┘
                     │
                     ▼
         ┌───────────────────────┐
         │  STAGE 1: Translator   │
         │  - Check raw files     │
         │  - Process CSVs        │
         │  - Convert to Parquet  │
         │  - Separate by year    │
         │  - Delete raw files    │
         └───────────┬───────────┘
                     │
                     ▼
         ┌───────────────────────┐
         │  STAGE 2: Analyzer     │
         │  - Check processed    │
         │  - Process each inst  │
         │  - Analyze dates       │
         │  - Process time slots  │
         │  - Save results        │
         └───────────┬───────────┘
                     │
                     ▼
         ┌───────────────────────┐
         │  STAGE 2.5: Merger     │
         │  - Merge daily files  │
         │  - Consolidate data   │
         │  - Create monthly     │
         └───────────┬───────────┘
                     │
                     ▼
         ┌───────────────────────┐
         │   PIPELINE END       │
         │   (Audit Report)     │
         └──────────────────────┘
```

---

## 📊 Data Flow

### File Locations

1. **Raw Data**: `data/raw/`
   - Input: CSV files from NinjaTrader exports
   - Pattern: `CL.csv`, `ES.csv`, `NQ.csv`, etc.

2. **Processed Data**: `data/processed/`
   - Output from Translator
   - Pattern: `{instrument}_{year}_{filename}.parquet`
   - Example: `ES_2025_ES.parquet`

3. **Analyzer Results**: `data/analyzer_runs/`
   - Output from Analyzer
   - Structure: `{instrument}{session}/{year}/{filename}.parquet`
   - Example: `ES1/2025/ES1_an_2025_11.parquet`

4. **Merged Data**: (in analyzer_runs, consolidated)
   - Output from Data Merger
   - Monthly consolidated files

---

## 🎮 How to Run

### Full Pipeline (from Dashboard)
1. Click **"Run Now"** button
2. Pipeline automatically:
   - Checks for raw files → runs translator
   - Checks for processed files → runs analyzer
   - Runs data merger after analyzer
   - Generates audit report

### Individual Stages (from Dashboard)
1. **Run Translator**: Click "Run Translator" button
   - Only runs if raw files exist
   
2. **Run Analyzer**: Click "Run Analyzer" button
   - Only runs if processed files exist
   
3. **Run Merger**: Click "Run Merger" button
   - Merges analyzer output files

### Manual Run (Command Line)
```bash
python automation/daily_data_pipeline_scheduler.py --stage translator
python automation/daily_data_pipeline_scheduler.py --stage analyzer
python automation/daily_data_pipeline_scheduler.py  # Full pipeline
```

---

## 📈 Event Tracking

All stages emit structured events to the event log:
- **Location**: `automation/logs/events/pipeline_{run_id}.jsonl`
- **Format**: JSON Lines (one event per line)
- **Streamed**: Real-time via WebSocket to dashboard

### Event Types
- `start`: Stage started
- `log`: Progress message
- `metric`: Measurement (file counts, rows, etc.)
- `success`: Stage completed successfully
- `failure`: Stage failed

### Dashboard Display
- Events shown in real-time in "Live Events" panel
- Filtered to show only important events (verbose logs hidden)
- Color-coded by severity (success=green, failure=red, start=blue)

---

## ⚠️ Error Handling

### Translator Failure
- **Impact**: Pipeline stops (unless processed files already exist)
- **Reason**: No processed files = nothing for analyzer to process

### Analyzer Failure
- **Impact**: **NON-FATAL** - Pipeline continues
- **Reason**: Each instrument processed independently
- **Result**: Failed instruments skipped, others continue

### Merger Failure
- **Impact**: Pipeline ends (but analyzer results still exist)
- **Reason**: Merger is final step

---

## ⏱️ Timeouts

- **Export Wait**: 60 minutes max
- **Translator**: 1 hour per run
- **Analyzer**: 6 hours per instrument
- **Merger**: 5 minutes

---

## 📝 Audit Report

After pipeline completes:
- Generates audit report
- Logs all stage results
- Saves to log file
- Shows success/failure summary

---

## 🎯 Summary

**Pipeline Flow**: `Export → Translator → Analyzer → Data Merger → END`

**Key Points**:
- Translator converts raw CSVs to processed Parquet files
- Analyzer processes each instrument separately (non-fatal failures)
- Merger consolidates analyzer results into monthly files
- Pipeline ends after merger (Sequential Processor disabled)
- All events tracked in real-time via WebSocket
- Dashboard shows filtered, clean event stream

**Total Stages**: 3 main stages + 1 optional export stage

**Duration**: Varies by data size (typically 10-30 minutes for full pipeline)



