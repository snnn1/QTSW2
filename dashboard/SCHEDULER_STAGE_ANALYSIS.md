# Scheduler Stage Execution Analysis

## Current Stage Flow

The scheduler runs stages in this order:

1. **Translator** → Converts CSV to Parquet
2. **Analyzer** → Runs breakout analysis
3. **Data Merger** → Consolidates daily files into monthly files

## Current Logic

### Stage 1: Translator
```python
if run_translator_stage:  # If CSV files exist
    if not self.orchestrator.run_translator():
        success = False
        if not run_analyzer_stage:  # No processed files exist
            return False  # Abort pipeline
    else:
        # Translator succeeded, refresh processed files list
        processed_files = list(DATA_PROCESSED.glob("*.parquet"))
        run_analyzer_stage = len(processed_files) > 0
```

**Behavior:**
- ✅ Runs if CSV files exist
- ✅ If fails and no processed files exist → Aborts pipeline
- ✅ If succeeds → Refreshes processed files list for analyzer
- ✅ If fails but processed files exist → Continues to analyzer

### Stage 2: Analyzer
```python
if run_analyzer_stage and success:
    if not self.orchestrator.run_analyzer():
        success = False  # Non-fatal (continues anyway)
elif not run_analyzer_stage and run_translator_stage:
    # Translator ran but no processed files created
    emit_event(run_id, "analyzer", "log", "Skipped: No processed files available")
```

**Behavior:**
- ✅ Runs if processed files exist AND translator succeeded (or files already existed)
- ✅ If fails → Sets `success = False` but continues (non-fatal)
- ✅ If skipped → Logs warning

### Stage 3: Data Merger
```python
if success and run_analyzer_stage:
    success = success and self.orchestrator.run_data_merger()
```

**Behavior:**
- ⚠️ **ISSUE**: Only runs if `success` is True AND `run_analyzer_stage` is True
- ⚠️ If analyzer fails (sets `success = False`), merger won't run
- ⚠️ This means even if analyzer produced some output, merger won't run if analyzer failed

## Potential Issues

### Issue 1: Merger Doesn't Run If Analyzer Fails

**Scenario:**
1. Translator succeeds → Creates processed files
2. Analyzer runs but fails (e.g., one instrument fails) → Sets `success = False`
3. Analyzer may have produced some output files
4. Merger won't run because `success = False`

**Impact:** Partial analyzer results won't be merged, even though they might be valid.

**Fix Options:**
- Option A: Run merger even if analyzer failed (check if analyzer output exists)
- Option B: Keep current behavior (don't merge if analyzer failed - might be intentional)

### Issue 2: Merger Check Logic

The merger check is:
```python
if success and run_analyzer_stage:
```

This means:
- ✅ If analyzer succeeded → Merger runs
- ❌ If analyzer failed → Merger doesn't run
- ✅ If analyzer was skipped (no processed files) → Merger doesn't run (correct)

**Question:** Should merger run even if analyzer partially failed?

## Recommendations

### Current Behavior (Conservative)
- **Pros:** Only merges when analyzer fully succeeds
- **Cons:** Partial results aren't merged

### Alternative Behavior (Aggressive)
- **Pros:** Merges whatever analyzer produced, even if it failed
- **Cons:** Might merge incomplete/corrupted data

### Recommended Fix

Check if analyzer output exists, regardless of success:

```python
# Stage 3: Data Merger (merge analyzer files into monthly files)
# Run merger if analyzer ran (regardless of success) - merge whatever was produced
if run_analyzer_stage:
    # Check if analyzer output exists
    analyzer_output_exists = False
    if ANALYZER_RUNS.exists():
        analyzer_files = list(ANALYZER_RUNS.rglob("*.parquet"))
        analyzer_output_exists = len(analyzer_files) > 0
    
    if analyzer_output_exists:
        self.orchestrator.run_data_merger()
        # Don't fail pipeline if merger fails (it's the last stage)
    else:
        self.logger.warning("No analyzer output found - skipping merger")
```

## Current Status

**✅ Stages run in correct order:**
1. Translator → Analyzer → Data Merger

**✅ Conditional execution works:**
- Translator only runs if CSV files exist
- Analyzer only runs if processed files exist
- Merger only runs if analyzer ran

**⚠️ Potential issue:**
- Merger doesn't run if analyzer failed (even if partial output exists)

## Conclusion

The scheduler **does run each stage properly** in sequence, but there's a conservative design choice where the merger won't run if the analyzer failed. This might be intentional (don't merge incomplete results) or might need adjustment (merge whatever was produced).

**Recommendation:** Keep current behavior unless you want to merge partial analyzer results.



