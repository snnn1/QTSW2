# How The Refactored Pipeline System Works

## Overview

The pipeline system has been completely refactored from a monolithic scheduler into a **modular, service-oriented architecture**. Each component has a single, clear responsibility, making the system easier to understand, test, and maintain.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    Windows Task Scheduler                    │
│              (Runs every 15 minutes at :00, :15,            │
│                        :30, :45)                            │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│              pipeline_runner.py (Entry Point)                │
│  • Generates unique run_id                                   │
│  • Sets up logging and event logging                         │
│  • Creates all services                                      │
│  • Calls orchestrator                                        │
│  • Generates audit report                                    │
└────────────────────────┬────────────────────────────────────┘
                         │
                         ▼
┌─────────────────────────────────────────────────────────────┐
│              PipelineOrchestrator                            │
│  • Decides which stages to run (based on file state)        │
│  • Calls services in sequence                                │
│  • Tracks stage results                                      │
│  • Emits events                                              │
│  • Generates final report                                    │
└────────┬───────────────┬───────────────┬────────────────────┘
         │               │               │
         ▼               ▼               ▼
┌──────────────┐ ┌──────────────┐ ┌──────────────┐
│ Translator   │ │  Analyzer    │ │   Merger     │
│   Service    │ │   Service    │ │   Service    │
└──────┬───────┘ └──────┬───────┘ └──────┬───────┘
       │                │                │
       └────────────────┼────────────────┘
                        │
                        ▼
         ┌──────────────────────────────┐
         │   ProcessSupervisor          │
         │  • Runs subprocesses         │
         │  • Monitors output           │
         │  • Handles timeouts          │
         │  • Detects hangs             │
         └──────────────────────────────┘
```

## Component Responsibilities

### 1. **Configuration (`config.py`)**
**Single Responsibility**: Hold all configuration constants

- All paths (data directories, log directories, script locations)
- All timeouts (translator, analyzer, merger)
- Default instrument lists
- No hardcoded paths anywhere else in the codebase

**Key Features:**
- Centralized configuration
- Easy to modify
- Can be extended for environment-based config

### 2. **Logging Setup (`logging_setup.py`)**
**Single Responsibility**: Create configured logger instances

- Sets up file and console handlers
- Configures log levels and formatting
- No global logger state

### 3. **Event Logger (`services/event_logger.py`)**
**Single Responsibility**: Emit structured JSONL events

- Writes one JSON object per line to event log file
- Fault-tolerant (never crashes pipeline if logging fails)
- Used by dashboard for real-time monitoring
- Each pipeline run gets its own event log file

**Event Format:**
```json
{
  "run_id": "uuid-here",
  "stage": "translator",
  "event": "start",
  "timestamp": "2025-04-12T02:00:00-05:00",
  "msg": "Starting data translator stage"
}
```

### 4. **Process Supervisor (`services/process_supervisor.py`)**
**Single Responsibility**: Execute subprocesses safely with monitoring

**Key Features:**
- **Generic**: Doesn't know what "translator" or "analyzer" is
- **Threading**: Uses threads to stream stdout/stderr in real-time
- **Timeout Handling**: Kills processes that exceed timeout
- **Hang Detection**: Detects when process stops producing output
- **Completion Detection**: Can detect when process is "done" even if it hasn't exited
- **Graceful Termination**: Tries SIGTERM before SIGKILL

**Returns**: `ProcessResult` with:
- Return code
- Stdout/stderr (full and tail)
- Execution time
- Whether it timed out or was terminated
- Success flag

### 5. **File Manager (`services/file_manager.py`)**
**Single Responsibility**: Atomic file operations and locking

- Scans directories for files
- Acquires/releases locks (prevents concurrent runs)
- Safe file deletion with logging
- No business logic - just file operations

### 6. **Translator Service (`pipeline/stages/translator.py`)**
**Single Responsibility**: Translate raw CSV files to processed Parquet files

**What It Does:**
1. Scans `data/raw/` for CSV files
2. If files found, builds command: `python translate_raw.py --input ... --output ...`
3. Calls `ProcessSupervisor.execute()` with the command
4. Monitors output to track which instruments are processed
5. Detects completion when all expected instruments are done
6. Returns `TranslatorResult` with status, file counts, duration

**Does NOT:**
- Delete files (that's handled by data lifecycle manager)
- Know about analyzer or merger
- Manage global state

### 7. **Analyzer Service (`pipeline/stages/analyzer.py`)**
**Single Responsibility**: Run breakout analysis on processed files

**What It Does:**
1. Scans `data/processed/` for Parquet/CSV files
2. Determines which instruments to analyze
3. Builds command: `python run_analyzer_parallel.py --instruments ...`
4. Calls `ProcessSupervisor.execute()` with the command
5. Monitors output to track progress
6. Returns `AnalyzerResult` with status, instruments processed, duration

**Does NOT:**
- Delete files
- Know about translator or merger
- Manage global state

### 8. **Merger Service (`pipeline/stages/merger.py`)**
**Single Responsibility**: Merge analyzer results

**What It Does:**
1. Builds command: `python data_merger.py`
2. Calls `ProcessSupervisor.execute()` with the command
3. Returns `MergerResult` with status and duration

**Does NOT:**
- Delete files
- Know about translator or analyzer
- Manage global state

### 9. **Pipeline Orchestrator (`pipeline/orchestrator.py`)**
**Single Responsibility**: Coordinate stage execution

**What It Does:**
1. **Decides what to run** based on file state:
   - Raw files exist → run translator
   - Processed files exist → run analyzer
   - Analyzer succeeded → run merger

2. **Calls services in sequence:**
   - Translator → Analyzer → Merger
   - Skips stages if prerequisites aren't met

3. **Tracks state:**
   - Records start/end times for each stage
   - Collects metrics (file counts, durations)
   - Determines overall status (success/partial/failure)

4. **Emits events:**
   - Before/after each stage
   - Stage metrics
   - Final pipeline status

5. **Returns**: `PipelineReport` with complete execution summary

**Does NOT:**
- Know subprocess details
- Parse stdout strings
- Delete files directly

### 10. **Audit Reporter (`audit.py`)**
**Single Responsibility**: Persist final pipeline report

- Takes `PipelineReport` from orchestrator
- Saves as JSON file in logs directory
- Provides historical record of all pipeline runs

### 11. **Data Lifecycle Manager (`data_lifecycle.py`)**
**Single Responsibility**: Decide when to delete files

**Policies:**
- **Raw files**: Only delete after translator succeeds
- **Processed files**: Only delete after analyzer AND merger succeed

**Does NOT:**
- Delete files automatically
- Know about pipeline stages directly
- Make decisions based on file names

## Execution Flow

### Step-by-Step: What Happens When Pipeline Runs

1. **Windows Task Scheduler** triggers at :00, :15, :30, :45
   - Runs: `python -m automation.pipeline_runner`

2. **`pipeline_runner.py`** starts:
   - Generates unique `run_id` (UUID)
   - Creates logger (file + console)
   - Creates event logger (JSONL file: `pipeline_{run_id}.jsonl`)
   - Creates all services:
     - `ProcessSupervisor` (for running subprocesses)
     - `FileManager` (for file operations)
     - `TranslatorService`, `AnalyzerService`, `MergerService`
   - Creates `PipelineOrchestrator`
   - Calls `orchestrator.run(run_id)`

3. **`PipelineOrchestrator.run()`** executes:
   
   **a. Check file state:**
   - Scans `data/raw/` for CSV files
   - Scans `data/processed/` for Parquet/CSV files
   - Decides: run translator? run analyzer?
   
   **b. Stage 1: Translator (if raw files exist)**
   - Calls `translator_service.run(run_id)`
   - Translator service:
     - Scans for raw CSV files
     - Builds command: `python translate_raw.py ...`
     - Calls `process_supervisor.execute()`
     - Process supervisor:
       - Starts subprocess
       - Streams stdout/stderr in real-time
       - Monitors for completion
       - Handles timeouts/hangs
       - Returns `ProcessResult`
     - Translator interprets result
     - Returns `TranslatorResult`
   - Orchestrator records result in report
   - Emits events: `translator.start`, `translator.success/failure`
   
   **c. Stage 2: Analyzer (if processed files exist)**
   - Calls `analyzer_service.run(run_id)`
   - Analyzer service:
     - Scans for processed files
     - Determines instruments to analyze
     - Builds command: `python run_analyzer_parallel.py ...`
     - Calls `process_supervisor.execute()`
     - Returns `AnalyzerResult`
   - Orchestrator records result
   - Emits events: `analyzer.start`, `analyzer.success/failure`
   
   **d. Stage 3: Merger (if analyzer succeeded)**
   - Calls `merger_service.run(run_id)`
   - Merger service:
     - Builds command: `python data_merger.py`
     - Calls `process_supervisor.execute()`
     - Returns `MergerResult`
   - Orchestrator records result
   - Emits events: `merger.start`, `merger.success/failure`
   
   **e. Generate final report:**
   - Determines overall status:
     - `success`: All stages succeeded
     - `partial`: Some stages succeeded
     - `failure`: All stages failed
   - Returns `PipelineReport`

4. **`pipeline_runner.py`** finishes:
   - Calls `audit_reporter.generate_report(report)`
   - Saves JSON report to logs directory
   - Exits with code:
     - `0`: Success
     - `1`: Partial success
     - `2`: Failure
     - `3`: Exception

## Key Improvements Over Old System

### 1. **Separation of Concerns**
- **Old**: One giant file with everything mixed together
- **New**: Each component has one clear responsibility

### 2. **Testability**
- **Old**: Hard to test (everything coupled)
- **New**: Each service can be tested in isolation

### 3. **Subprocess Handling**
- **Old**: Translator-specific logic scattered everywhere
- **New**: Generic `ProcessSupervisor` handles all subprocesses

### 4. **State Management**
- **Old**: Mutable global state, hard to track
- **New**: Immutable dataclasses (`PipelineState`, `StageResult`)

### 5. **Configuration**
- **Old**: Hardcoded paths everywhere
- **New**: All config in one place (`config.py`)

### 6. **Event Logging**
- **Old**: Mixed with regular logging
- **New**: Dedicated `EventLogger` for structured events

### 7. **File Deletion**
- **Old**: Deleted files too early, scattered logic
- **New**: Centralized `DataLifecycleManager` with explicit policies

### 8. **Scheduling**
- **Old**: Infinite Python loop
- **New**: OS-level scheduling (Windows Task Scheduler)

### 9. **Error Handling**
- **Old**: Unpredictable behavior
- **New**: Clear error paths, graceful failures

### 10. **Monitoring**
- **Old**: Hard to see what's happening
- **New**: Structured events, clear stage boundaries

## Data Flow

```
Raw CSV Files (data/raw/)
    ↓
[Translator Service]
    ↓
Processed Parquet Files (data/processed/)
    ↓
[Analyzer Service]
    ↓
Analysis Results (data/analyzer_runs/)
    ↓
[Merger Service]
    ↓
Merged Results (data/analyzer_runs/merged/)
```

## Event Flow

```
pipeline.start
    ↓
translator.start → translator.metric → translator.success/failure
    ↓
analyzer.start → analyzer.metric → analyzer.success/failure
    ↓
merger.start → merger.metric → merger.success/failure
    ↓
pipeline.success/failure
```

## File Outputs

### Logs
- **Pipeline Logs**: `automation/logs/pipeline_YYYYMMDD_HHMMSS.log`
  - Human-readable log of entire pipeline run
  - Includes all stage output, errors, warnings

### Event Logs
- **Event Logs**: `automation/logs/events/pipeline_{run_id}.jsonl`
  - Structured JSON events (one per line)
  - Used by dashboard for real-time monitoring
  - Machine-readable

### Audit Reports
- **Audit Reports**: `automation/logs/pipeline_report_YYYYMMDD_HHMMSS_{run_id}.json`
  - Complete execution summary
  - Stage results, metrics, timestamps
  - Historical record

## Key Design Principles

1. **Single Responsibility**: Each module does one thing
2. **Dependency Injection**: Services receive dependencies, don't create them
3. **Immutability**: State is tracked in dataclasses, not mutated globally
4. **Fault Tolerance**: Event logging never crashes pipeline
5. **Separation of Concerns**: Business logic separate from infrastructure
6. **Testability**: Each component can be tested independently
7. **Observability**: Clear events and logging at every step

## Summary

The new system is:
- ✅ **Modular**: Clear separation of concerns
- ✅ **Testable**: Each component can be tested in isolation
- ✅ **Maintainable**: Easy to understand and modify
- ✅ **Reliable**: Better error handling and monitoring
- ✅ **Observable**: Structured events and clear logging
- ✅ **Scalable**: Easy to add new stages or modify existing ones

The old monolithic scheduler has been replaced with a clean, service-oriented architecture that follows software engineering best practices.


