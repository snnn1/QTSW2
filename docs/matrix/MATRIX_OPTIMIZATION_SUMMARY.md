# Matrix Optimization Implementation Summary

## What Was Changed

### Phase 1 — Downstream Matrix Contract

1. **Audit** (`docs/matrix/MATRIX_DOWNSTREAM_AUDIT.md`)
   - Documented downstream analyzer reads in `timetable_engine.py` (`calculate_rs_for_stream`, `get_scf_values`, `generate_timetable`)
   - Call sites: `run_matrix_and_timetable.py`, API `generate_timetable` endpoint

2. **Contract** (`docs/matrix/MATRIX_DOWNSTREAM_CONTRACT.md`)
   - Defined required columns: Stream, trade_date, Time, selected_time, final_allowed, Result, Session, Instrument, scf_s1, scf_s2, points, rs_value, filter_reasons
   - Per-time-slot columns: `{time} Rolling`, `{time} Points`

3. **Write metrics during build**
   - `schema_normalizer.py`: Populates `rs_value` and `points` from sequencer's `{Time} Rolling` / `{Time} Points` (vectorized)
   - `filter_engine.py`: Added `_apply_scf_filter` so `final_allowed` includes SCF blocking (scf_s1/scf_s2 >= threshold)

4. **Remove downstream analyzer reads**
   - `run_matrix_and_timetable.py`: Matrix-first path when `master_df` exists; timetable-only loads matrix from disk; fallback to `generate_timetable` only when no matrix
   - API `generate_timetable`: Loads matrix from disk, uses `write_execution_timetable_from_master_matrix` + `build_timetable_dataframe_from_master_matrix`; fallback to `generate_timetable` when matrix empty
   - Added `build_timetable_dataframe_from_master_matrix` for display/API parity

### Phase 2 — Structured Telemetry

- `instrumentation.py`: Added `log_timing_event()` for JSONL events to `logs/matrix_timing.jsonl`
- Instrumented: `matrix_load` (file_manager), `matrix_save` (file_manager), `full_rebuild` (master_matrix), `rolling_resequence` (master_matrix_rolling_resequence), `timetable_generation` (file_manager background), `api_matrix_load` (api with cache_hit)

### Phase 3 — Matrix Build Journal

- `build_journal.py`: Append-only JSONL to `logs/matrix_build_journal.jsonl`
- Events: build_start, build_complete, resequence_start, resequence_complete, timetable_start, timetable_complete, matrix_saved, failure
- Metadata: timestamp, mode, rows_written, streams_processed, date_window, matrix_file_path, duration_ms, error

### Phase 4 — MatrixState (In-Process Only)

- `matrix_state.py`: Holds current matrix DataFrame, last path, mtime; invalidates on new file
- API uses `get_matrix_state()` when loading; cache invalidation on build/resequence
- Eligibility builder and CLI continue to use disk

### Phase 5 — Vectorization

- Replaced `iterrows` with `itertuples` in: `timetable_engine.write_execution_timetable`, `api.calculate_profit_breakdown` (day, dow, dom, time, month, year)

## Final Downstream Matrix Contract

See `docs/matrix/MATRIX_DOWNSTREAM_CONTRACT.md`.

## Remaining Downstream Analyzer Reads

- **`generate_timetable`** (timetable_engine): Still used when no matrix exists (timetable-only with empty matrix dir, or matrix load fails). Reads analyzer parquet for RS and SCF. Marked as legacy fallback.
- **Eligibility builder**: Uses matrix from disk; no analyzer reads for RS/SCF.

## Telemetry Output Locations

- `logs/matrix_timing.jsonl` — Phase timings
- `logs/matrix_build_journal.jsonl` — Build/resequence/timetable audit trail
- `logs/matrix_feature_usage.jsonl` — Existing feature usage

## Partitioned Storage Recommendation

Deferred. Telemetry should be collected first. After measuring cold vs warm load, resequence vs full rebuild, and API cache hit rates, decide whether partitioned storage is justified.

## Validation Results (2026-03-07)

| Phase | Duration | Notes |
|-------|----------|-------|
| Full rebuild | 96.4s | 29,230 rows, 14 streams |
| Rolling resequence (10 days) | 8.1s | ~12x faster than full rebuild |
| Matrix load | 55ms | Cold load from parquet |
| Matrix save | 115–125ms | Parquet write |
| Timetable generation | 2.4s | From matrix (no analyzer reads) |

**Tests:** `modules/matrix/tests/test_matrix_optimization.py` — 5/5 pass (SCF filter, rs_value/points, build_timetable_dataframe, MatrixState).
