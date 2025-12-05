# Scheduler - Complete Explanation

## Overview

The scheduler (`automation/daily_data_pipeline_scheduler.py`) is an automated system that runs your data processing pipeline every 15 minutes. It orchestrates the complete workflow from raw data to final merged results.

**Current Size**: 1,777 lines  
**Main Purpose**: Automate the data pipeline (Translator → Analyzer → Data Merger) on a schedule

---

## Architecture

The scheduler consists of **3 main classes**:

### 1. `NinjaTraderController` (~280 lines)
**Purpose**: Manages NinjaTrader process and export monitoring

**Key Methods**:
- `launch()` - Starts NinjaTrader (currently disabled)
- `wait_for_export()` - Monitors for CSV export files to appear
- `is_running()` - Checks if NinjaTrader is running

**How Export Monitoring Works**:
- Watches `data/raw/` directory for new CSV files
- Monitors `data/raw/logs/` for signal files:
  - `export_start_*.json` - Export began
  - `export_progress_*.json` - Progress updates (every 100k records)
  - `export_complete_*.json` - Export finished
- Tracks file size growth to detect when files stop being written
- Times out after 60 minutes if no completion detected

**Status**: NinjaTrader launch is **disabled** (you don't want it)

---

### 2. `PipelineOrchestrator` (~1,200 lines)
**Purpose**: Executes each stage of the pipeline

**Key Methods**:

#### `run_translator()` (~300 lines)
- **What it does**: Converts raw CSV files to processed Parquet files
- **Input**: CSV files in `data/raw/`
- **Output**: Parquet files in `data/processed/` (separated by year/instrument)
- **How it works**:
  1. Finds all CSV files in `data/raw/`
  2. Runs `tools/translate_raw.py` with `--separate-years --no-merge`
  3. Monitors output in real-time using threading/queues
  4. Emits progress events every 60 seconds
  5. Detects completion by looking for `[SUCCESS]` message
  6. Auto-terminates if process hangs after success message
  7. Deletes processed raw CSV files after successful translation
- **Timeout**: 1 hour
- **Recent Fix**: Added real-time monitoring to prevent "stuck" issues

#### `run_analyzer()` (~500 lines)
- **What it does**: Runs breakout analysis on processed data
- **Input**: Parquet files in `data/processed/`
- **Output**: Analysis results in `data/analyzer_runs/`
- **How it works**:
  1. Uses `tools/run_analyzer_parallel.py` to process multiple instruments simultaneously
  2. For each instrument (ES, NQ, YM, CL, NG, GC):
     - Runs `scripts/breakout_analyzer/scripts/run_data_processed.py`
     - Processes multiple time slots (07:30, 08:00, 09:00, etc.)
     - Generates monthly Parquet files per instrument/session/year
  3. Tracks progress per instrument
  4. Handles failures gracefully (one instrument failing doesn't stop others)
- **Timeout**: 6 hours per instrument
- **Parallel Processing**: All instruments run simultaneously for speed

#### `run_data_merger()` (~70 lines)
- **What it does**: Merges daily analyzer files into monthly files
- **Input**: Daily files in `data/analyzer_runs/`
- **Output**: Monthly merged files in `data/analyzer_runs/`
- **How it works**:
  1. Runs `tools/data_merger.py`
  2. Consolidates daily files into monthly files per instrument/session/year
  3. **This is where the pipeline ends** (sequential processor removed)
- **Timeout**: 30 minutes

#### Helper Methods
- `_delete_raw_files()` - Removes processed CSV files
- `_delete_processed_files()` - Removes processed Parquet files (if needed)
- `generate_audit_report()` - Creates JSON report of pipeline execution

---

### 3. `DailyPipelineScheduler` (~200 lines)
**Purpose**: Main scheduler that orchestrates everything

**Key Methods**:

#### `run_now()` - Execute Pipeline Immediately
**What it does**: Runs the complete pipeline right now (used by scheduler and manual runs)

**Flow**:
1. **Generate Run ID**: Creates unique UUID for this run
2. **Create Event Log**: Sets up JSONL file for event tracking
3. **Check for Files**:
   - Looks for CSV files in `data/raw/` → triggers translator
   - Looks for processed files in `data/processed/` → triggers analyzer
4. **Run Stages** (if files found):
   - **Stage 1**: Translator (if raw CSV files exist)
   - **Stage 2**: Analyzer (if processed files exist)
   - **Stage 2.5**: Data Merger (after analyzer completes)
5. **Generate Audit Report**: Creates summary JSON file

**Key Features**:
- Skips stages if no files found (won't run translator if no CSV files)
- Continues to analyzer even if translator fails (if processed files already exist)
- Emits events throughout for dashboard visibility

#### `wait_for_schedule()` - 15-Minute Interval Runner
**What it does**: Runs the pipeline every 15 minutes at :00, :15, :30, :45

**Flow**:
1. **Startup**: Emits "scheduler started" event
2. **Infinite Loop**:
   - Calculates next run time (next :00, :15, :30, or :45)
   - Waits until that time
   - Calls `run_now()` to execute pipeline
   - Waits 5 seconds
   - Repeats
3. **Runs Continuously**: Never stops (until process is killed)

**Example Schedule**:
- 12:00 → Run pipeline
- 12:15 → Run pipeline
- 12:30 → Run pipeline
- 12:45 → Run pipeline
- 13:00 → Run pipeline
- ... and so on

---

## Event Logging System

**Purpose**: Structured logging for dashboard visibility

**How it works**:
- Each pipeline run gets a unique `run_id` (UUID)
- Events are written to `automation/logs/events/pipeline_{run_id}.jsonl`
- Each event is a JSON object with:
  - `run_id`: Unique identifier
  - `stage`: Which stage (translator, analyzer, merger, pipeline)
  - `event`: Event type (start, success, failure, log, metric)
  - `timestamp`: ISO format timestamp (Chicago time)
  - `msg`: Human-readable message
  - `data`: Optional structured data (file counts, metrics, etc.)

**Event Types**:
- `start` - Stage began
- `success` - Stage completed successfully
- `failure` - Stage failed
- `log` - General log message
- `metric` - Progress/metrics update

**Example Events**:
```json
{"run_id": "abc-123", "stage": "translator", "event": "start", "timestamp": "2025-12-03T19:00:00-06:00", "msg": "Starting data translator stage"}
{"run_id": "abc-123", "stage": "translator", "event": "metric", "timestamp": "2025-12-03T19:01:00-06:00", "msg": "Translation in progress (1 min elapsed, 3 files written)", "data": {"elapsed_minutes": 1, "files_written": 3}}
{"run_id": "abc-123", "stage": "translator", "event": "success", "timestamp": "2025-12-03T19:05:00-06:00", "msg": "Translator completed successfully"}
```

---

## How the Scheduler Starts

**From Dashboard Backend** (`dashboard/backend/main.py`):
- Backend starts → `lifespan` event fires
- Calls `POST /api/scheduler/start` endpoint
- Starts scheduler process in background
- Scheduler runs `wait_for_schedule()` → begins 15-minute loop

**Manual Start** (CLI):
```bash
python automation/daily_data_pipeline_scheduler.py --schedule 07:30
# Runs every 15 minutes starting from next :00, :15, :30, or :45
```

**Immediate Run** (CLI):
```bash
python automation/daily_data_pipeline_scheduler.py --run-now
# Runs pipeline once and exits
```

**Single Stage** (CLI):
```bash
python automation/daily_data_pipeline_scheduler.py --stage translator
# Runs only translator stage
```

---

## Current Pipeline Flow

```
┌─────────────────────────────────────────────────────────┐
│  Scheduler (runs every 15 minutes)                      │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  run_now() - Check for files                            │
│  • Raw CSV files? → Run Translator                      │
│  • Processed files? → Run Analyzer                      │
└─────────────────────────────────────────────────────────┘
                        │
        ┌───────────────┴───────────────┐
        │                               │
        ▼                               ▼
┌──────────────────┐          ┌──────────────────┐
│  Translator      │          │  Analyzer         │
│  • CSV → Parquet │          │  • Parallel       │
│  • Separate years│          │  • Per instrument │
│  • Delete CSV    │          │  • Monthly files │
└──────────────────┘          └──────────────────┘
        │                               │
        └───────────────┬───────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  Data Merger                                             │
│  • Merge daily → monthly                                 │
│  • Per instrument/session/year                           │
└─────────────────────────────────────────────────────────┘
                        │
                        ▼
┌─────────────────────────────────────────────────────────┐
│  Pipeline Complete                                       │
│  • Generate audit report                                 │
│  • Wait for next 15-minute interval                      │
└─────────────────────────────────────────────────────────┘
```

---

## Key Configuration

**Paths** (defined at top of file):
- `DATA_RAW = QTSW2_ROOT / "data" / "raw"` - Input CSV files
- `DATA_PROCESSED = QTSW2_ROOT / "data" / "processed"` - Translator output
- `ANALYZER_RUNS = QTSW2_ROOT / "data" / "analyzer_runs"` - Analyzer output
- `EVENT_LOGS_DIR = LOGS_DIR / "events"` - Event log files

**Scripts**:
- `TRANSLATOR_SCRIPT = tools/translate_raw.py`
- `PARALLEL_ANALYZER_SCRIPT = tools/run_analyzer_parallel.py`
- `DATA_MERGER_SCRIPT = tools/data_merger.py`

**Timezone**: Chicago (America/Chicago) - All timestamps use this

---

## Error Handling

**Translator**:
- Timeout after 1 hour → emits failure event
- Detects success message → treats as complete even if process hangs
- Auto-terminates if stuck after success

**Analyzer**:
- Per-instrument failures don't stop other instruments
- Timeout after 6 hours per instrument
- Continues pipeline even if some instruments fail

**Data Merger**:
- Timeout after 30 minutes
- Failure stops pipeline (merger is final stage)

**General**:
- All exceptions caught and logged
- Events emitted for all failures
- Pipeline continues when possible (non-fatal errors)

---

## Integration with Dashboard

**How Dashboard Sees Scheduler**:
1. Backend starts scheduler process on startup
2. Scheduler writes events to JSONL files
3. Backend tails event log files via WebSocket
4. Frontend receives events in real-time
5. Dashboard displays pipeline progress

**Event Flow**:
```
Scheduler → Event Log (JSONL) → Backend (tail) → WebSocket → Frontend
```

**Status Detection**:
- Dashboard checks `/api/pipeline/status` endpoint
- Backend reads latest event log file
- Determines if pipeline is active based on:
  - Last event timestamp
  - Event type (success/failure = complete)
  - File modification time

---

## What Happens During a Run

**Example: 12:00 Run**

1. **12:00:00** - Scheduler wakes up
2. **12:00:01** - `run_now()` called
3. **12:00:02** - Event log created: `pipeline_abc123.jsonl`
4. **12:00:03** - Check `data/raw/` → Found 6 CSV files
5. **12:00:04** - Start Translator
   - Event: `{"stage": "translator", "event": "start", ...}`
6. **12:00:05-12:05:00** - Translator processing
   - Events every 60s: `{"stage": "translator", "event": "metric", "msg": "Translation in progress (1 min elapsed, 2 files written)", ...}`
   - Events per file: `{"stage": "translator", "event": "log", "msg": "File written: Saved: ES_2025_ES.parquet", ...}`
7. **12:05:01** - Translator completes
   - Event: `{"stage": "translator", "event": "success", ...}`
8. **12:05:02** - Check `data/processed/` → Found 6 Parquet files
9. **12:05:03** - Start Analyzer (parallel)
   - Event: `{"stage": "analyzer", "event": "start", ...}`
10. **12:05:04-12:15:00** - Analyzer processing (all instruments in parallel)
    - Events per instrument: `{"stage": "analyzer", "event": "log", "msg": "Running analyzer for ES", ...}`
11. **12:15:01** - Analyzer completes
    - Event: `{"stage": "analyzer", "event": "success", ...}`
12. **12:15:02** - Start Data Merger
    - Event: `{"stage": "merger", "event": "start", ...}`
13. **12:15:03-12:16:00** - Merger processing
14. **12:16:01** - Merger completes
    - Event: `{"stage": "merger", "event": "success", ...}`
15. **12:16:02** - Pipeline complete
    - Event: `{"stage": "pipeline", "event": "success", ...}`
16. **12:16:03** - Wait until 12:15 (next interval)
17. **12:15:00** - Next run begins

---

## Important Notes

1. **No NinjaTrader**: Launch is disabled (you don't want it)
2. **No Sequential Processor**: Removed (you don't want it)
3. **Pipeline Ends After Merger**: That's the final stage
4. **15-Minute Intervals**: Runs at :00, :15, :30, :45 every hour
5. **Event-Driven**: All activity logged to JSONL files for dashboard
6. **Resilient**: Continues even if some stages fail (when possible)
7. **Progress Updates**: Emits events every 60 seconds during long operations

---

## Detailed Stage Explanations

### Translator Stage (`run_translator()`)

**What It Does**:
Converts raw NinjaTrader CSV exports into clean, processed Parquet files organized by year and instrument.

**Process**:
1. **File Discovery**: Scans `data/raw/` for CSV files (excludes subdirectories like `logs/`)
2. **Command Execution**: Runs `tools/translate_raw.py` with:
   - `--input data/raw` - Source directory
   - `--output data/processed` - Output directory
   - `--separate-years` - Split data by year
   - `--no-merge` - Process each CSV file separately
3. **Real-Time Monitoring**:
   - Uses `subprocess.Popen()` with threading to capture output in real-time
   - Reads stdout/stderr via separate threads and queues
   - Emits events as files are written
   - Emits progress updates every 60 seconds
4. **Completion Detection**:
   - Looks for `[SUCCESS] Data translation completed successfully!` message
   - If process doesn't exit within 10 seconds of success message, auto-terminates
   - Treats as complete if success message found (even if return code isn't 0)
5. **Cleanup**: Deletes processed raw CSV files after successful translation

**Output Structure**:
```
data/processed/
  ├── ES_2025_ES.parquet
  ├── NQ_2025_NQ.parquet
  ├── CL_2025_CL.parquet
  └── ...
```

**Timeout**: 1 hour (kills process if exceeded)

---

### Analyzer Stage (`run_analyzer()`)

**What It Does**:
Runs breakout analysis on processed data files, generating trading signals and statistics.

**Process**:
1. **Parallel Execution**: Uses `tools/run_analyzer_parallel.py` to process all instruments simultaneously
2. **Per-Instrument Processing**:
   - For each instrument (ES, NQ, YM, CL, NG, GC):
     - Runs `scripts/breakout_analyzer/scripts/run_data_processed.py`
     - Processes multiple time slots (07:30, 08:00, 09:00, 09:30, 10:00, 10:30, 11:00)
     - Generates analysis results per instrument/session/year/month
3. **Output Structure**:
   ```
   data/analyzer_runs/
     ├── ES1/
     │   └── 2025/
     │       ├── ES1_an_2025_11.parquet
     │       └── ES1_an_2025_12.parquet
     ├── ES2/
     │   └── 2025/
     │       └── ES2_an_2025_11.parquet
     └── ...
   ```
4. **Error Handling**: 
   - One instrument failing doesn't stop others
   - Continues pipeline even if some instruments fail
   - Logs failures per instrument

**Timeout**: 6 hours per instrument (very large files)

**Why Parallel**: Processes 6 instruments simultaneously instead of sequentially, saving ~5x time

---

### Data Merger Stage (`run_data_merger()`)

**What It Does**:
Consolidates daily analyzer output files into monthly files for easier analysis.

**Process**:
1. **Runs**: `tools/data_merger.py`
2. **Input**: Daily files in `data/analyzer_runs/{instrument}{session}/{year}/`
3. **Output**: Monthly merged files in same structure
4. **Purpose**: Combines multiple daily files into single monthly file per instrument/session/year

**Timeout**: 30 minutes

**Note**: This is the **final stage** - pipeline ends here (sequential processor removed)

---

## Scheduling Logic (`wait_for_schedule()`)

**How 15-Minute Intervals Work**:

```python
def wait_for_schedule():
    while True:  # Infinite loop
        # Calculate next run time (next :00, :15, :30, or :45)
        now = datetime.now(CHICAGO_TZ)
        current_minute = now.minute
        
        # Find next 15-minute mark
        if current_minute < 15:
            next_minute = 15
        elif current_minute < 30:
            next_minute = 30
        elif current_minute < 45:
            next_minute = 45
        else:
            next_minute = 0
            next_hour = (now.hour + 1) % 24
        
        # Wait until that time
        next_run = now.replace(minute=next_minute, second=0, microsecond=0)
        if next_minute == 0:
            next_run = next_run.replace(hour=next_hour)
        
        # Sleep until next run time
        sleep_seconds = (next_run - now).total_seconds()
        time.sleep(sleep_seconds)
        
        # Run pipeline
        run_now()
        
        # Small delay before calculating next run
        time.sleep(5)
```

**Example Timeline**:
- **12:00:00** - Scheduler starts, calculates next run = 12:00:00 (now)
- **12:00:01** - Runs pipeline
- **12:16:00** - Pipeline completes
- **12:16:01** - Calculates next run = 12:15:00 (already passed, so 12:30:00)
- **12:16:02** - Sleeps until 12:30:00
- **12:30:00** - Runs pipeline
- **12:46:00** - Pipeline completes
- **12:46:01** - Calculates next run = 12:45:00 (already passed, so 13:00:00)
- ... and so on

**Key Point**: Runs at :00, :15, :30, :45 of every hour, regardless of when it started

---

## Event Flow Example

**Complete Event Sequence for One Run**:

```json
// 1. Pipeline starts
{"run_id": "abc-123", "stage": "pipeline", "event": "start", "msg": "Pipeline run started"}

// 2. Translator starts
{"run_id": "abc-123", "stage": "translator", "event": "start", "msg": "Starting data translator stage"}
{"run_id": "abc-123", "stage": "translator", "event": "metric", "msg": "Files found", "data": {"raw_file_count": 6}}

// 3. Translator progress (every 60 seconds)
{"run_id": "abc-123", "stage": "translator", "event": "metric", "msg": "Translation in progress (1 min elapsed, 2 files written)", "data": {"elapsed_minutes": 1, "files_written": 2}}

// 4. Files being written
{"run_id": "abc-123", "stage": "translator", "event": "log", "msg": "File written: Saved: ES_2025_ES.parquet (144,204 rows)"}
{"run_id": "abc-123", "stage": "translator", "event": "metric", "msg": "Wrote ES 2025 file", "data": {"instrument": "ES", "year": "2025"}}

// 5. Translator completes
{"run_id": "abc-123", "stage": "translator", "event": "metric", "msg": "Translation complete", "data": {"processed_file_count": 6}}
{"run_id": "abc-123", "stage": "translator", "event": "success", "msg": "Translator completed successfully"}

// 6. Analyzer starts
{"run_id": "abc-123", "stage": "analyzer", "event": "start", "msg": "Starting analyzer stage"}

// 7. Analyzer processing (per instrument)
{"run_id": "abc-123", "stage": "analyzer", "event": "log", "msg": "Running analyzer for ES"}
{"run_id": "abc-123", "stage": "analyzer", "event": "log", "msg": "Running analyzer for NQ"}
// ... etc

// 8. Analyzer completes
{"run_id": "abc-123", "stage": "analyzer", "event": "success", "msg": "Analyzer completed successfully"}

// 9. Data Merger starts
{"run_id": "abc-123", "stage": "merger", "event": "start", "msg": "Starting data merger stage"}

// 10. Data Merger completes
{"run_id": "abc-123", "stage": "merger", "event": "success", "msg": "Data merger completed successfully"}

// 11. Pipeline complete
{"run_id": "abc-123", "stage": "pipeline", "event": "success", "msg": "Pipeline complete"}
```

---

## Summary

The scheduler is a **robust automation system** that:
- ✅ Runs your pipeline every 15 minutes automatically
- ✅ Handles all stages: Translator → Analyzer → Data Merger
- ✅ Provides real-time progress updates via events
- ✅ Handles errors gracefully
- ✅ Integrates with dashboard for visibility
- ✅ Works without NinjaTrader (launch disabled)
- ✅ Ends after merger (sequential processor removed)

**Complexity**: Moderate (1,777 lines) but most complexity is justified:
- Export monitoring needs to be robust
- Process monitoring prevents stuck stages
- Parallel processing improves performance
- Event logging enables dashboard integration

**Key Design Decisions**:
1. **15-minute intervals** - Frequent enough to catch new data, not too frequent to waste resources
2. **Parallel analyzer** - Processes 6 instruments simultaneously for speed
3. **Real-time monitoring** - Prevents "stuck" issues with progress updates
4. **Event-driven** - All activity logged for dashboard visibility
5. **Resilient** - Continues even when some stages fail (when possible)

