# Complete Pipeline Flow - Detailed View

This document provides a comprehensive walkthrough of the entire data pipeline from the "Run Now" button click to final output files.

## Table of Contents
1. [User Interaction](#user-interaction)
2. [Backend API Processing](#backend-api-processing)
3. [Pipeline Process Launch](#pipeline-process-launch)
4. [Stage 1: Data Translator](#stage-1-data-translator)
5. [Stage 2: Breakout Analyzer](#stage-2-breakout-analyzer)
6. [Stage 2.5: Data Merger](#stage-25-data-merger)
7. [Event Streaming & Dashboard Updates](#event-streaming--dashboard-updates)
8. [File Locations & Data Flow](#file-locations--data-flow)

---

## User Interaction

### 1. Dashboard Frontend (`dashboard/frontend/src/App.jsx`)

**Location:** Line 228-255

**User Action:** Clicks "Run Now" button

**Frontend Code:**
```javascript
const startPipeline = async () => {
  const response = await fetch(`${API_BASE}/pipeline/start`, {
    method: 'POST'
  })
  const data = await response.json()
  setCurrentRunId(data.run_id)
  setIsRunning(true)
  setCurrentStage('starting')
  connectWebSocket(data.run_id)  // Connect to WebSocket for real-time events
}
```

**What Happens:**
1. Frontend sends POST request to `/api/pipeline/start`
2. Receives `run_id` (UUID) and `event_log_path`
3. Sets UI state to "running"
4. Opens WebSocket connection to receive real-time events
5. Displays "Pipeline started" alert

---

## Backend API Processing

### 2. FastAPI Backend (`dashboard/backend/main.py`)

**Location:** Line 200-240

**Endpoint:** `POST /api/pipeline/start`

**Backend Code:**
```python
@app.post("/api/pipeline/start", response_model=PipelineStartResponse)
async def start_pipeline(wait_for_export: bool = False, launch_ninjatrader: bool = False):
    run_id = str(uuid.uuid4())
    event_log_path = EVENT_LOGS_DIR / f"pipeline_{run_id}.jsonl"
    event_log_path.touch()
    
    env = os.environ.copy()
    env["PIPELINE_EVENT_LOG"] = str(event_log_path)
    
    cmd = ["python", str(SCHEDULER_SCRIPT), "--now", "--no-debug-window"]
    
    process = subprocess.Popen(cmd, cwd=str(QTSW2_ROOT), env=env, ...)
    
    return PipelineStartResponse(run_id=run_id, event_log_path=str(event_log_path), status="started")
```

**What Happens:**
1. Generates unique `run_id` (UUID)
2. Creates event log file: `automation/logs/pipeline_{run_id}.jsonl`
3. Sets environment variable `PIPELINE_EVENT_LOG` pointing to log file
4. Launches separate Python process running `automation/daily_data_pipeline_scheduler.py --now --no-debug-window`
5. Returns immediately (non-blocking) with `run_id` and `event_log_path`
6. Pipeline runs in background, independent of API request

---

## Pipeline Process Launch

### 3. Scheduler Main Entry (`automation/daily_data_pipeline_scheduler.py`)

**Location:** Line 1527-1638

**Command:** `python daily_data_pipeline_scheduler.py --now --no-debug-window`

**What Happens:**
1. Parses command-line arguments
2. Creates `DailyPipelineScheduler` instance
3. Calls `scheduler.run_now()` method (line 1383)

**Main Pipeline Method (`run_now`):**
```python
def run_now(self, wait_for_export: bool = False, launch_ninjatrader: bool = False) -> bool:
    run_id = os.environ.get("PIPELINE_EVENT_LOG", "").split("_")[-1].replace(".jsonl", "")
    
    # Optional: Launch NinjaTrader and wait for exports
    if launch_ninjatrader:
        self.nt_controller.launch_ninjatrader()
    
    if wait_for_export:
        self.nt_controller.wait_for_export(timeout_minutes=60, run_id=run_id)
    
    # Check for files
    raw_files = list(DATA_RAW.glob("*.csv"))
    processed_files = list(DATA_PROCESSED.glob("*.parquet"))
    
    # Determine which stages to run
    run_translator_stage = len(raw_files) > 0
    run_analyzer_stage = len(processed_files) > 0
    
    # Create orchestrator
    orchestrator = PipelineOrchestrator(self.logger, run_id)
    
    # Stage 1: Translator
    if run_translator_stage:
        orchestrator.run_translator()
    
    # Stage 2: Analyzer
    if run_analyzer_stage:
        orchestrator.run_analyzer()
    
    # Stage 2.5: Data Merger
    if success:
        orchestrator.run_data_merger()
    
    return success
```

**File Locations Checked:**
- **Raw files:** `data/raw/*.csv` (from NinjaTrader DataExporter)
- **Processed files:** `data/processed/*.parquet` (from Translator)

---

## Stage 1: Data Translator

### 4. Translator Stage (`automation/daily_data_pipeline_scheduler.py`)

**Location:** Line 533-669

**Method:** `PipelineOrchestrator.run_translator()`

**Input:** Raw CSV files in `data/raw/`
- Example: `ES.csv`, `CL.csv`, `DataExport_ES_2024_UTC.csv`
- Format: CSV with timestamps (UTC from NinjaTrader)

**Process:**
1. **File Discovery:**
   ```python
   raw_files = list(DATA_RAW.glob("*.csv"))
   raw_files = [f for f in raw_files if f.parent == DATA_RAW]  # Exclude subdirectories
   ```

2. **Command Execution:**
   ```python
   translator_cmd = [
       sys.executable,
       str(QTSW2_ROOT / "tools" / "translate_raw.py"),
       "--input", str(DATA_RAW),
       "--output", str(DATA_PROCESSED),
       "--separate-years",  # Split by year
       "--no-merge"  # Process each file separately
   ]
   subprocess.run(translator_cmd, timeout=3600)  # 1 hour timeout
   ```

3. **Translator Script (`tools/translate_raw.py`):**
   - **File Loading (line 100-190):**
     - Reads CSV files
     - Detects format (header vs no-header)
     - Parses timestamps
     - **Timezone Conversion (line 142-163):**
       - Detects if file is from DataExporter (checks filename pattern OR `data/raw` directory)
       - If UTC data: `tz_localize("UTC").tz_convert("America/Chicago")`
       - If Chicago data: `tz_localize("America/Chicago")`
     - Extracts instrument from filename
     - Validates numeric columns
   
   - **File Processing (line 280-364):**
     - Processes each file separately (`--no-merge`)
     - If `--separate-years`: Creates files like `ES_2024.parquet`, `ES_2025.parquet`
     - If no separation: Creates `ES_file.parquet`
     - Saves as Parquet format (faster, smaller)

4. **Output Files:**
   - Location: `data/processed/`
   - Format: Parquet files
   - Naming: `{INSTRUMENT}_{YEAR}.parquet` (e.g., `ES_2024.parquet`, `CL_2025.parquet`)
   - Schema:
     - `timestamp` (timezone-aware, America/Chicago)
     - `open`, `high`, `low`, `close`, `volume`
     - `instrument` (ES, NQ, YM, CL, NG, GC)
     - `contract` (optional)

5. **Cleanup:**
   - Deletes processed raw CSV files from `data/raw/` after successful translation
   - Emits event: `"Raw files deleted"` with count

**Events Emitted:**
- `translator.start` - Stage started
- `translator.log` - Progress messages ("Processing:", "Loaded:", "Saved:")
- `translator.metric` - File counts, completion status
- `translator.success` - Stage completed
- `translator.failure` - Stage failed

---

## Stage 2: Breakout Analyzer

### 5. Analyzer Stage (`automation/daily_data_pipeline_scheduler.py`)

**Location:** Line 877-1000

**Method:** `PipelineOrchestrator.run_analyzer()`

**Input:** Processed Parquet files in `data/processed/`
- Example: `ES_2024.parquet`, `CL_2025.parquet`
- Format: Timezone-aware timestamps, OHLCV data

**Process:**
1. **File Discovery:**
   ```python
   processed_files = list(DATA_PROCESSED.glob("*.parquet"))
   processed_files.extend(list(DATA_PROCESSED.glob("*.csv")))
   ```

2. **Parallel Execution Setup:**
   ```python
   max_workers = min(MAX_PARALLEL_ANALYZERS, len(instruments))  # Default: 3
   instruments = ["ES", "NQ", "YM", "CL", "NG", "GC"]
   
   with ThreadPoolExecutor(max_workers=max_workers) as executor:
       # Submit all analyzer tasks
       future_to_instrument = {
           executor.submit(self._run_single_analyzer, instrument, i+1, len(instruments)): instrument
           for i, instrument in enumerate(instruments)
       }
   ```

3. **Single Instrument Processing (`_run_single_analyzer`):**
   ```python
   analyzer_cmd = [
       sys.executable,
       str(ANALYZER_SCRIPT),  # scripts/breakout_analyzer/scripts/run_data_processed.py
       "--folder", str(DATA_PROCESSED),
       "--instrument", instrument,  # ES, NQ, YM, CL, NG, GC
       "--sessions", "S1", "S2",
       "--slots", 
       "S1:07:30", "S1:08:00", "S1:09:00",  # All S1 slots
       "S2:09:30", "S2:10:00", "S2:10:30", "S2:11:00",  # All S2 slots
       "--debug"
   ]
   ```

4. **Analyzer Script (`scripts/breakout_analyzer/scripts/run_data_processed.py`):**
   - **Data Loading (line 39-87):**
     - Loads all Parquet files from `data/processed/`
     - Concatenates into single DataFrame
     - Filters by requested instrument
     - Validates required columns: `timestamp`, `open`, `high`, `low`, `close`, `instrument`
   
   - **Strategy Execution (line 188):**
     - Calls `run_strategy(df, rp, debug=True)`
     - Processes each date, session (S1/S2), and time slot
     - Generates trade results with:
       - Entry/Exit prices
       - Target, Range, Stop Loss
       - Peak (Maximum Favorable Excursion)
       - Profit/Loss
       - Direction (Long/Short)
   
   - **Output Writing (line 289):**
     - Saves to: `data/analyzer_temp/{YYYY-MM-DD}/{INSTRUMENT}{SESSION}_an_{YYYY-MM-DD}.parquet`
     - Example: `data/analyzer_temp/2025-01-15/ES1_an_2025-01-15.parquet` (S1 session)
     - Example: `data/analyzer_temp/2025-01-15/ES2_an_2025-01-15.parquet` (S2 session)

5. **Output Files:**
   - Location: `data/analyzer_temp/{YYYY-MM-DD}/`
   - Format: Parquet files
   - Naming: `{INSTRUMENT}{SESSION}_an_{YYYY-MM-DD}.parquet`
     - `SESSION`: `1` = S1, `2` = S2
   - Schema:
     - `Date`, `Time`, `Session`, `Instrument`
     - `Target`, `Range`, `SL`, `Direction`
     - `EntryPrice`, `ExitPrice`, `Peak`, `Profit`
     - `Setup`, `NoTrade` (optional flags)

6. **Cleanup:**
   - After all instruments complete, deletes processed files from `data/processed/`
   - Emits event: `"Processed files deleted"` with count

**Events Emitted:**
- `analyzer.start` - Stage started
- `analyzer.log` - Progress per instrument ("Starting ES (1/6)", "ES completed")
- `analyzer.metric` - Instrument completion, elapsed time
- `analyzer.success` - Stage completed
- `analyzer.failure` - Instrument or stage failed

**Performance:**
- **Sequential (old):** 6 instruments × 10 min = 60 minutes
- **Parallel (new, 3 workers):** 6 instruments ÷ 3 = ~20 minutes (3x faster)

---

## Stage 2.5: Data Merger

### 6. Data Merger Stage (`automation/daily_data_pipeline_scheduler.py`)

**Location:** Line 1002-1050

**Method:** `PipelineOrchestrator.run_data_merger()`

**Input:** Daily analyzer files in `data/analyzer_temp/{YYYY-MM-DD}/`
- Example: `data/analyzer_temp/2025-01-15/ES1_an_2025-01-15.parquet`

**Process:**
1. **Command Execution:**
   ```python
   merger_cmd = [
       sys.executable,
       str(DATA_MERGER_SCRIPT),  # tools/data_merger.py
       "--non-interactive"
   ]
   subprocess.run(merger_cmd, timeout=3600)  # 1 hour timeout
   ```

2. **Merger Script (`tools/data_merger.py`):**
   - **Daily Folder Discovery (line 223-238):**
     - Scans `data/analyzer_temp/` for folders matching `YYYY-MM-DD` format
     - Skips already-processed folders (tracked in `data/merger_processed.json`)
   
   - **File Processing (line 400-500):**
     - For each daily folder:
       - Reads all Parquet files
       - Detects instrument and session from filename or data
       - Splits by session (S1 → `{INSTR}1`, S2 → `{INSTR}2`)
       - Groups by year and month
   
   - **Monthly File Creation (line 284-304):**
     - Path: `data/analyzer_runs/{INSTRUMENT}{SESSION}/{YEAR}/{INSTRUMENT}{SESSION}_an_{YEAR}_{MONTH}.parquet`
     - Example: `data/analyzer_runs/ES1/2025/ES1_an_2025_01.parquet`
     - Example: `data/analyzer_runs/ES2/2025/ES2_an_2025_01.parquet`
   
   - **Merge Logic (`_merge_with_existing_monthly_file`, line 581-621):**
     - **If new data exists:** Use ONLY new data (replaces old monthly file)
     - **If new data is empty AND old file exists:** Keep old file (skip write)
     - **If new data is empty AND no old file:** Skip write
     - Removes duplicates
     - Sorts by Date and Time

3. **Output Files:**
   - Location: `data/analyzer_runs/{INSTRUMENT}{SESSION}/{YEAR}/`
   - Format: Parquet files
   - Naming: `{INSTRUMENT}{SESSION}_an_{YEAR}_{MONTH:02d}.parquet`
   - Example structure:
     ```
     data/analyzer_runs/
     ├── ES1/2025/
     │   ├── ES1_an_2025_01.parquet
     │   ├── ES1_an_2025_02.parquet
     │   └── ...
     ├── ES2/2025/
     │   ├── ES2_an_2025_01.parquet
     │   └── ...
     └── CL1/2025/
         └── CL1_an_2025_01.parquet
     ```

4. **Cleanup:**
   - Deletes daily temp folders after successful merge
   - Marks folders as processed in `data/merger_processed.json`

**Events Emitted:**
- `merger.start` - Stage started
- `merger.log` - Progress messages ("Processing folder:", "Merged X rows")
- `merger.metric` - Files merged, rows processed
- `merger.success` - Stage completed
- `merger.failure` - Stage failed

---

## Event Streaming & Dashboard Updates

### 7. Real-Time Event System

**Event Log File:** `automation/logs/pipeline_{run_id}.jsonl`

**Event Format (JSON Lines):**
```json
{"run_id": "abc-123", "stage": "translator", "event_type": "log", "message": "Processing: ES.csv", "timestamp": "2025-01-15T10:30:00"}
{"run_id": "abc-123", "stage": "translator", "event_type": "metric", "message": "Files found", "data": {"raw_file_count": 3}}
{"run_id": "abc-123", "stage": "translator", "event_type": "success", "message": "Translator completed successfully"}
```

**Event Types:**
- `start` - Stage started
- `log` - Progress message
- `metric` - Numeric data (file counts, elapsed time)
- `success` - Stage completed successfully
- `failure` - Stage failed

**WebSocket Connection (`dashboard/backend/main.py`):**
- Endpoint: `ws://localhost:8000/ws`
- Frontend connects with `run_id`
- Backend reads event log file and streams new events
- Frontend updates UI in real-time:
  - Stage status cards
  - Event log panel
  - File counts
  - Progress indicators

**Dashboard Updates:**
- **Stage Cards:** Show current stage, elapsed time, status (running/success/failure)
- **Event Log:** Scrollable list of all events
- **File Counts:** Updated every 5 seconds via polling
- **Alerts:** Popup notifications on failures

---

## File Locations & Data Flow

### 8. Complete Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    NINJATRADER DATAEXPORTER                      │
│  (C# Indicator exporting raw CSV files)                         │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
                    ┌─────────────────┐
                    │   data/raw/     │
                    │  ES.csv         │
                    │  CL.csv         │
                    │  (UTC timestamps)│
                    └────────┬────────┘
                             │
                             │ [STAGE 1: TRANSLATOR]
                             │ tools/translate_raw.py
                             │ - Timezone conversion (UTC → Chicago)
                             │ - Format validation
                             │ - Year separation
                             │
                             ▼
                    ┌─────────────────┐
                    │ data/processed/  │
                    │ ES_2024.parquet │
                    │ ES_2025.parquet │
                    │ CL_2024.parquet │
                    │ (Chicago time)   │
                    └────────┬────────┘
                             │
                             │ [STAGE 2: ANALYZER]
                             │ scripts/breakout_analyzer/scripts/run_data_processed.py
                             │ - Parallel execution (3 workers)
                             │ - Strategy execution per instrument
                             │ - Trade result generation
                             │
                             ▼
                    ┌──────────────────────┐
                    │ data/analyzer_temp/  │
                    │ 2025-01-15/          │
                    │   ES1_an_2025-01-15.parquet
                    │   ES2_an_2025-01-15.parquet
                    │   CL1_an_2025-01-15.parquet
                    │ (Daily files)        │
                    └────────┬─────────────┘
                             │
                             │ [STAGE 2.5: DATA MERGER]
                             │ tools/data_merger.py
                             │ - Merge daily → monthly
                             │ - Split by session (S1/S2)
                             │ - Remove duplicates
                             │
                             ▼
                    ┌──────────────────────┐
                    │ data/analyzer_runs/  │
                    │ ES1/2025/            │
                    │   ES1_an_2025_01.parquet
                    │ ES2/2025/            │
                    │   ES2_an_2025_01.parquet
                    │ CL1/2025/            │
                    │   CL1_an_2025_01.parquet
                    │ (Monthly files)      │
                    └──────────────────────┘
```

### File Naming Conventions

**Raw Files:**
- `ES.csv`, `CL.csv` - Simple instrument names
- `DataExport_ES_2024_UTC.csv` - Full DataExporter format

**Processed Files:**
- `{INSTRUMENT}_{YEAR}.parquet` - e.g., `ES_2024.parquet`

**Daily Analyzer Files:**
- `{INSTRUMENT}{SESSION}_an_{YYYY-MM-DD}.parquet`
- `SESSION`: `1` = S1, `2` = S2
- Example: `ES1_an_2025-01-15.parquet` (ES, S1 session, Jan 15, 2025)

**Monthly Analyzer Files:**
- `{INSTRUMENT}{SESSION}_an_{YEAR}_{MONTH:02d}.parquet`
- Example: `ES1_an_2025_01.parquet` (ES, S1 session, January 2025)

### Directory Structure

```
QTSW2/
├── data/
│   ├── raw/                    # Input: Raw CSV from NinjaTrader
│   │   ├── ES.csv
│   │   └── logs/                # Export progress signals
│   │
│   ├── processed/               # Stage 1 output: Translated Parquet
│   │   ├── ES_2024.parquet
│   │   └── CL_2025.parquet
│   │
│   ├── analyzer_temp/           # Stage 2 output: Daily analyzer results
│   │   ├── 2025-01-15/
│   │   │   ├── ES1_an_2025-01-15.parquet
│   │   │   └── ES2_an_2025-01-15.parquet
│   │   └── 2025-01-16/
│   │
│   └── analyzer_runs/           # Stage 2.5 output: Monthly consolidated
│       ├── ES1/2025/
│       │   ├── ES1_an_2025_01.parquet
│       │   └── ES1_an_2025_02.parquet
│       └── ES2/2025/
│
├── automation/
│   └── logs/
│       └── pipeline_{run_id}.jsonl  # Event log
│
└── tools/
    ├── translate_raw.py         # Stage 1 script
    └── data_merger.py            # Stage 2.5 script
```

---

## Summary

**Complete Pipeline Flow:**
1. User clicks "Run Now" → Frontend sends POST request
2. Backend launches pipeline process → Returns `run_id`
3. Pipeline checks for files → Determines which stages to run
4. **Stage 1:** Translator converts raw CSV → Processed Parquet (with timezone conversion)
5. **Stage 2:** Analyzer processes data → Daily analyzer files (parallel execution, 3 workers)
6. **Stage 2.5:** Merger consolidates daily → Monthly files (split by session)
7. Events stream to dashboard → Real-time UI updates
8. Final output: Monthly Parquet files ready for analysis

**Key Features:**
- ✅ Parallel analyzer execution (3x faster)
- ✅ Automatic timezone conversion (UTC → Chicago)
- ✅ Year separation for efficient processing
- ✅ Session splitting (S1/S2) for analysis
- ✅ Real-time event streaming
- ✅ Automatic cleanup of intermediate files
- ✅ Idempotent merger (never double-writes)

**Performance:**
- Translator: ~1-5 minutes (depends on file size)
- Analyzer: ~20 minutes for 6 instruments (parallel, 3 workers)
- Merger: ~1-2 minutes
- **Total:** ~25-30 minutes for complete pipeline


