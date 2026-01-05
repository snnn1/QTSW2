# Matrix Feature Map
**Date:** 2025  
**Purpose:** Complete inventory of matrix features, modes, and subsystems for deletion campaign

---

## Public Entry Points

### API Endpoints (`modules/matrix/api.py`)

| Endpoint | Method | Purpose | Parameters |
|----------|--------|---------|------------|
| `/api/matrix/build` | POST | Full/partial/authoritative rebuild | `MatrixBuildRequest` (start_date, end_date, specific_date, streams, authoritative, stream_filters) |
| `/api/matrix/update` | POST | Incremental update (rolling window) | `MatrixBuildRequest` (mode: 'window', authoritative) |
| `/api/matrix/reload_latest` | POST | Reload from disk without rebuild | None |
| `/api/matrix/freshness` | GET | Check analyzer vs matrix staleness | analyzer_runs_dir |
| `/api/matrix/data` | GET | Fetch matrix data | file_path, limit, order, essential_columns_only, skip_cleaning, contract_multiplier, include_filtered_executed, stream_include |
| `/api/matrix/breakdown` | POST | Statistics breakdown | `BreakdownRequest` (breakdown_type, stream_filters, use_filtered, contract_multiplier, stream_include) |
| `/api/matrix/stream-stats` | POST | Per-stream statistics | `StreamStatsRequest` (stream_id, include_filtered_executed, contract_multiplier) |
| `/api/matrix/files` | GET | List matrix files | analyzer_runs_dir |

### Direct Methods (`modules/matrix/master_matrix.py`)

| Method | Lines | Purpose | Parameters |
|--------|-------|---------|------------|
| `build_master_matrix()` | 894-968 | Main build method | start_date, end_date, specific_date, output_dir, stream_filters, analyzer_runs_dir, streams, authoritative |
| `update_master_matrix()` | 1214-1448 | Incremental update | output_dir, stream_filters, analyzer_runs_dir |
| `build_master_matrix_window_update()` | 1570+ | Window update (rolling) | reprocess_days, output_dir, stream_filters, analyzer_runs_dir |

---

## Rebuild Modes

| Mode | Entry Point | Parameters | Description |
|------|-------------|------------|-------------|
| **Full Rebuild** | `build_master_matrix()` | `streams=None` | Rebuilds all streams from scratch |
| **Partial Rebuild** | `build_master_matrix()` | `streams=['ES1', 'GC1']` | Rebuilds only specified streams, merges with existing |
| **Authoritative Rebuild** | `build_master_matrix()` | `authoritative=True` | Matrix exactly equals analyzer output (removes stale rows) |
| **Window Update** | `build_master_matrix_window_update()` | `reprocess_days=35` | Rolling window update (reprocesses last N trading days) |
| **Incremental Update** | `update_master_matrix()` | None | Adds only new dates after latest date per stream |
| **Reload Latest** | `reload_latest_matrix()` API | None | Reloads most recent matrix file from disk without rebuild |

---

## Helper Subsystems

### MatrixBuilder (`modules/matrix/master_matrix.py` lines 46-81)

**Responsibilities:**
- Loading analyzer data from disk
- Applying authoritative rebuild mode

**Methods:**
- `load_analyzer_data()` - Delegates to `MasterMatrix._load_analyzer_data()`
- `apply_authoritative_rebuild()` - Delegates to `MasterMatrix._apply_authoritative_rebuild()`

**Status:** Active

---

### MatrixValidator (`modules/matrix/master_matrix.py` lines 84-119)

**Responsibilities:**
- Schema normalization
- Adding global columns
- Repairing invalid dates

**Methods:**
- `normalize_schema_and_columns()` - Delegates to `MasterMatrix._normalize_schema_and_columns()`
- `repair_invalid_dates()` - Delegates to `MasterMatrix._repair_invalid_dates()`

**Status:** Active

---

### MatrixSorter (`modules/matrix/master_matrix.py` lines 122-148)

**Responsibilities:**
- Sorting matrix in canonical order
- Calculating Time Change column

**Methods:**
- `sort_canonically()` - Delegates to `MasterMatrix._sort_matrix_canonically()`

**Status:** Active

---

### MatrixPersistence (`modules/matrix/master_matrix.py` lines 151-179)

**Responsibilities:**
- Saving matrix to disk
- Creating checkpoints
- Logging summary statistics

**Methods:**
- `persist()` - Delegates to `MasterMatrix._persist_matrix()`

**Status:** Active

---

## Core Implementation Modules

### schema_normalizer.py

**Purpose:** Ensures consistent schema across all streams

**Key Functions:**
- `normalize_schema()` - Adds missing columns with defaults
- `create_derived_columns()` - Creates derived columns (entry_time, exit_time, etc.)

**Status:** Active

---

### data_loader.py

**Purpose:** Handles loading trade data from analyzer_runs directory

**Key Functions:**
- `find_parquet_files()` - Finds monthly consolidated parquet files
- `load_stream_data()` - Loads a single stream's data
- `load_all_streams()` - Loads all streams with parallel loading and retry logic

**Status:** Active

---

### file_manager.py

**Purpose:** Handles file I/O operations for matrix files

**Key Functions:**
- `save_master_matrix()` - Saves matrix to parquet file
- `load_existing_matrix()` - Loads most recent existing matrix file
- `get_latest_matrix_file()` - Gets path to most recent matrix file

**Status:** Active

---

### checkpoint_manager.py

**Purpose:** Manages checkpoints for window updates

**Key Functions:**
- `CheckpointManager.load_latest_checkpoint()` - Loads latest checkpoint
- `CheckpointManager.save_checkpoint()` - Saves checkpoint

**Status:** Active

---

### sequencer_logic.py

**Purpose:** Selects one trade per day per stream using time change logic

**Key Functions:**
- `process_stream_daily()` - Processes a stream's daily trades
- `apply_sequencer_logic()` - Applies sequencer logic to DataFrame

**Status:** Active (Core logic - DO NOT DELETE)

---

### filter_engine.py

**Purpose:** Applies stream filters (excluded times, days, etc.)

**Key Functions:**
- `apply_stream_filters()` - Applies filters to DataFrame
- `add_global_columns()` - Adds global filter columns

**Status:** Active

---

### statistics.py

**Purpose:** Calculates matrix statistics

**Key Functions:**
- `calculate_summary_stats()` - Calculates summary statistics
- Various metric calculation functions

**Status:** Active

---

## Internal Methods (MasterMatrix)

### Data Loading

| Method | Lines | Purpose |
|--------|-------|---------|
| `_load_analyzer_data()` | 253-297 | Load analyzer data (full or partial) |
| `_rebuild_full()` | 261-273 | Rebuild everything from scratch |
| `_rebuild_partial()` | 275-323 | Rebuild specific streams and merge |

### Authoritative Mode

| Method | Lines | Purpose |
|--------|-------|---------|
| `_apply_authoritative_rebuild()` | 449-559 | Apply authoritative rebuild (remove stale rows) |

### Validation & Repair

| Method | Lines | Purpose |
|--------|-------|---------|
| `_normalize_schema_and_columns()` | 561-592 | Normalize schema and add global columns |
| `_repair_invalid_dates()` | 594-740 | Repair invalid dates (multiple strategies) |

### Sorting

| Method | Lines | Purpose |
|--------|-------|---------|
| `_sort_matrix_canonically()` | 742-864 | Sort in canonical order and calculate Time Change |

### Persistence

| Method | Lines | Purpose |
|--------|-------|---------|
| `_persist_matrix()` | 695-892 | Save matrix and create checkpoint |

---

## Usage Statistics

*To be populated after Phase 1 instrumentation*

- Mode invocation counts
- Subsystem usage frequency
- Repair/default-fill execution rates
- Average execution times

---

## Deletion Status

| Subsystem/Mode | Status | Deletion Date | Reason |
|----------------|--------|---------------|--------|
| Stream Filtering Safety Check (`_rebuild_partial`) | REMOVED | 2025-01-05 | REDUNDANT - Sequencer already filters streams |

---

**Last Updated:** 2025
