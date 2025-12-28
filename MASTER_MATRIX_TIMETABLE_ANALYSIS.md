# Master Matrix & Timetable Engine Code Analysis

## Executive Summary

This document analyzes the master matrix and timetable engine codebase for:
1. Code clashes and duplication
2. Potential errors and edge cases
3. Performance optimization opportunities

**Date:** 2025-01-27  
**Scope:** `modules/matrix/` and `modules/timetable/`

---

## 1. CODE CLASHES & DUPLICATION

### 1.1 Duplicate SLOT_ENDS Definitions ⚠️ CRITICAL

**Issue:** `SLOT_ENDS` is defined in 4 different locations with identical values:

1. `modules/matrix/sequencer_logic.py:60` - **AUTHORITATIVE** (used by sequencer)
2. `modules/matrix/trade_selector.py:23` - Duplicate (should import from sequencer_logic)
3. `modules/timetable/timetable_engine.py:53` - Different name (`session_time_slots`) but same data
4. `modules/analyzer/breakout_core/config.py:6` - Analyzer config (may be intentional)

**Risk:** If values diverge, sequencer and timetable will use different time slots, causing incorrect trade selection.

**Recommendation:**
- Make `sequencer_logic.SLOT_ENDS` the single source of truth
- Import it in `trade_selector.py` instead of redefining
- Import it in `timetable_engine.py` instead of `session_time_slots`
- Keep analyzer config separate (it's a different context)

**Code Change:**
```python
# In trade_selector.py - REPLACE:
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}

# WITH:
from .sequencer_logic import SLOT_ENDS
```

```python
# In timetable_engine.py - REPLACE:
self.session_time_slots = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"]
}

# WITH:
from modules.matrix.sequencer_logic import SLOT_ENDS
self.session_time_slots = SLOT_ENDS  # Reference, not copy
```

### 1.2 Duplicate Logic: Sequencer Application

**Issue:** Sequencer logic application is duplicated in two places:

1. `master_matrix.py:_load_all_streams_with_sequencer()` (lines 163-208)
2. `master_matrix.py:update_master_matrix()` (lines 710-786)

**Risk:** Logic divergence, maintenance burden, potential bugs if one is fixed but not the other.

**Recommendation:** Extract to a shared method:
```python
def _apply_sequencer_to_streams(self, streams, date_range=None):
    """Shared method for applying sequencer logic."""
    # Consolidate the logic here
```

### 1.3 Duplicate Date Parsing Logic

**Issue:** Date parsing/normalization happens in multiple places:
- `master_matrix.py:build_master_matrix()` (lines 360-460)
- `master_matrix.py:update_master_matrix()` (lines 609-617)
- `schema_normalizer.py:create_derived_columns()` (lines 147-164)
- `timetable_engine.py:get_scf_values()` (lines 235-248)

**Risk:** Inconsistent date handling, potential timezone issues, maintenance burden.

**Recommendation:** Create a centralized date utility module.

### 1.4 Duplicate Stream Discovery Logic

**Issue:** Stream discovery happens in multiple places:
- `master_matrix.py:__init__()` calls `stream_manager.discover_streams()`
- `master_matrix.py:build_master_matrix()` re-discovers if `analyzer_runs_dir` changes
- `master_matrix.py:update_master_matrix()` re-discovers if `analyzer_runs_dir` changes

**Risk:** Inconsistent stream lists if directory structure changes between calls.

**Recommendation:** Cache discovered streams and invalidate only when needed.

---

## 2. POTENTIAL ERRORS & EDGE CASES

### 2.1 Date Repair Logic May Hide Data Quality Issues ⚠️

**Location:** `master_matrix.py:build_master_matrix()` lines 371-460

**Issue:** The date repair logic is very forgiving - it:
1. Tries multiple date formats
2. Falls back to median date from same stream
3. Uses sentinel date (2099-12-31) as last resort

**Risk:** Invalid dates are silently repaired, making it hard to detect data quality issues.

**Recommendation:**
- Add a `date_quality_score` column to track repair confidence
- Log warnings for repaired dates with low confidence
- Consider failing fast if >5% of dates need repair

### 2.2 Race Condition in Parallel Stream Loading

**Location:** `data_loader.py:load_all_streams()` lines 229-280

**Issue:** Uses `ThreadPoolExecutor` for parallel loading, but:
- No locking around shared `all_trades` list
- `streams_failed` dict is modified from multiple threads

**Risk:** Thread safety issues (though Python's GIL may mitigate).

**Recommendation:**
- Use `concurrent.futures` properly (already done, but verify thread safety)
- Consider using `multiprocessing.Manager().list()` if issues occur
- Add explicit locks if needed

**Note:** Current implementation is likely safe due to GIL, but worth monitoring.

### 2.3 None/NaN Handling Inconsistencies

**Issue:** Different modules handle None/NaN differently:

1. **sequencer_logic.py:** Checks `pd.isna()` and `is None` separately
2. **filter_engine.py:** Uses `pd.isna()` only
3. **schema_normalizer.py:** Uses `pd.notna()` checks
4. **statistics.py:** Has `_normalize_result()` but not used everywhere

**Risk:** Edge cases where None vs NaN vs empty string behave differently.

**Recommendation:** Standardize on pandas `pd.isna()` / `pd.notna()` everywhere.

### 2.4 Missing Error Handling in Timetable RS Calculation

**Location:** `timetable_engine.py:calculate_rs_for_stream()` lines 58-138

**Issue:** 
- No error handling if parquet files are corrupted
- No validation that `lookback_days` trades exist
- Assumes `Result` column exists and is valid

**Risk:** Silent failures or incorrect RS values.

**Recommendation:**
```python
try:
    df = pd.read_parquet(file_path)
except Exception as e:
    logger.warning(f"Failed to load {file_path}: {e}")
    continue  # Skip corrupted files
```

### 2.5 Potential Index Out of Bounds in Time Change Logic

**Location:** `sequencer_logic.py:decide_time_change()` lines 68-140

**Issue:** Accesses `sorted_others[0]` without checking if list is empty (though there's a check for `other_sums`).

**Risk:** If `other_sums` is empty but `sorted_others` somehow has items, could fail.

**Recommendation:** Add explicit check:
```python
if not sorted_others:
    return None
best_other_time, best_other_sum = sorted_others[0]
```

### 2.6 Stream Filter Validation Missing

**Issue:** No validation that `stream_filters` keys match actual streams.

**Location:** `filter_engine.py:apply_stream_filters()` lines 88-148

**Risk:** Typos in stream IDs silently ignored, filters not applied.

**Recommendation:**
```python
def apply_stream_filters(df, stream_filters):
    valid_streams = set(df['Stream'].unique())
    filter_streams = set(stream_filters.keys())
    invalid = filter_streams - valid_streams
    if invalid:
        logger.warning(f"Filters provided for non-existent streams: {invalid}")
```

### 2.7 Memory Leak Risk: Large DataFrame Copies

**Issue:** Multiple `.copy()` calls on large DataFrames:
- `master_matrix.py:build_master_matrix()` - multiple copies
- `sequencer_logic.py:process_stream_daily()` - copies per date
- `filter_engine.py:apply_stream_filters()` - copies

**Risk:** High memory usage with large datasets.

**Recommendation:** Use views (`df.loc[...]`) where possible instead of copies.

---

## 3. PERFORMANCE OPTIMIZATION OPPORTUNITIES

### 3.1 Redundant DataFrame Sorting ⚡ HIGH IMPACT

**Issue:** DataFrames are sorted multiple times:

1. `data_loader.py:load_all_streams()` - sorts by Stream, Date (line 478)
2. `master_matrix.py:build_master_matrix()` - sorts by trade_date, entry_time, Instrument, Stream (line 480)
3. `sequencer_logic.py:process_stream_daily()` - sorts by Date (line 221)
4. `sequencer_logic.py:apply_sequencer_logic()` - sorts by Stream, Date (line 478)

**Impact:** O(n log n) operations repeated unnecessarily.

**Recommendation:**
- Sort once at the end of the pipeline
- Use `sort=False` in `pd.concat()` when sorting will happen later
- Cache sorted DataFrames if reused

**Estimated Speedup:** 20-30% for large datasets

### 3.2 Inefficient Date Filtering

**Location:** `data_loader.py:apply_date_filters()` lines 54-90

**Issue:** Converts dates to datetime on every call, even if already datetime.

**Recommendation:**
```python
def apply_date_filters(df, start_date=None, end_date=None, specific_date=None):
    if 'Date' not in df.columns:
        return df
    
    # Check if already datetime before converting
    if not pd.api.types.is_datetime64_any_dtype(df['Date']):
        df['Date'] = pd.to_datetime(df['Date'])
    # ... rest of logic
```

**Estimated Speedup:** 5-10% for date-filtered queries

### 3.3 No Caching of Expensive Operations

**Issue:** No caching for:
- Stream discovery (scans filesystem every time)
- RS calculations in timetable (recalculates from scratch)
- Schema normalization (re-runs on every call)

**Recommendation:**
- Cache stream discovery with TTL or file modification time check
- Cache RS calculations per stream/session with date range
- Cache normalized schemas (if schema doesn't change)

**Estimated Speedup:** 30-50% for repeated operations

### 3.4 Inefficient GroupBy Operations

**Location:** Multiple places use `groupby()` without optimization:
- `sequencer_logic.py:apply_sequencer_logic()` - groups by Stream (line 483)
- `statistics.py:_calculate_daily_metrics()` - groups by date (line 555)

**Recommendation:**
- Use `sort=False` in groupby when order doesn't matter
- Pre-sort before groupby if order matters
- Use categorical dtypes for grouping columns

**Estimated Speedup:** 10-15% for large datasets

### 3.5 Redundant Time Normalization

**Issue:** `normalize_time()` is called repeatedly on the same values:
- In `sequencer_logic.py` - normalizes same times multiple times per day
- In `filter_engine.py` - normalizes exclude_times for every row

**Recommendation:**
- Cache normalized times in a dict
- Normalize once per unique time value
- Use vectorized operations where possible

**Estimated Speedup:** 5-10% for time-heavy operations

### 3.6 Sequential File Reading Could Be Parallelized

**Location:** `timetable_engine.py:calculate_rs_for_stream()` lines 89-98

**Issue:** Reads parquet files sequentially in a loop.

**Recommendation:**
```python
from concurrent.futures import ThreadPoolExecutor

with ThreadPoolExecutor(max_workers=4) as executor:
    futures = [executor.submit(pd.read_parquet, fp) for fp in parquet_files[:10]]
    all_trades = [f.result() for f in futures if f.result() is not None]
```

**Estimated Speedup:** 30-40% for RS calculation (I/O bound)

### 3.7 Unnecessary DataFrame Copies in Filter Engine

**Location:** `filter_engine.py:apply_stream_filters()` lines 88-148

**Issue:** Uses `.loc[]` assignments which may trigger copies.

**Recommendation:**
- Use boolean masks more efficiently
- Consider using `df.query()` for complex filters
- Batch filter applications

**Estimated Speedup:** 5-10% for filter-heavy operations

### 3.8 Memory Inefficiency: String Operations

**Issue:** String operations create new objects:
- `filter_reasons` concatenation (line 107-109, 116-118, 143-145)
- Time normalization creates new strings

**Recommendation:**
- Pre-allocate `filter_reasons` as list, join at end
- Use string interning for common values
- Consider using categorical dtype for repeated strings

**Estimated Speedup:** 5-10% memory reduction

---

## 4. ARCHITECTURAL RECOMMENDATIONS

### 4.1 Centralize Configuration

**Recommendation:** Create `modules/matrix/config.py`:
```python
"""Centralized configuration for master matrix."""
SLOT_ENDS = {
    "S1": ["07:30", "08:00", "09:00"],
    "S2": ["09:30", "10:00", "10:30", "11:00"],
}
ROLLING_WINDOW_SIZE = 13
DOM_BLOCKED_DAYS = {4, 16, 30}
```

### 4.2 Extract Date Utilities

**Recommendation:** Create `modules/matrix/date_utils.py`:
```python
"""Date parsing and normalization utilities."""
def parse_trade_date(date_value):
    """Parse date with multiple fallback strategies."""
    # Consolidate all date parsing logic here
```

### 4.3 Add Type Hints

**Issue:** Many functions lack type hints, making it hard to catch errors.

**Recommendation:** Add type hints throughout (use `typing` module).

### 4.4 Add Unit Tests

**Issue:** Limited test coverage for edge cases.

**Recommendation:** Add tests for:
- Date repair logic
- Time normalization edge cases
- Filter application
- RS calculation

---

## 5. PRIORITY FIXES

### 🔴 CRITICAL (Fix Immediately)
1. **Duplicate SLOT_ENDS** - Consolidate to single source
2. **Date repair logic** - Add quality scoring
3. **Stream filter validation** - Validate filter keys

### 🟡 HIGH PRIORITY (Fix Soon)
1. **Duplicate sequencer logic** - Extract shared method
2. **Redundant sorting** - Optimize sort operations
3. **RS calculation caching** - Cache expensive calculations

### 🟢 MEDIUM PRIORITY (Optimize When Possible)
1. **Date parsing optimization** - Check dtype before converting
2. **GroupBy optimization** - Use sort=False where possible
3. **Time normalization caching** - Cache normalized times

### 🔵 LOW PRIORITY (Nice to Have)
1. **Type hints** - Add throughout codebase
2. **Unit tests** - Increase coverage
3. **Documentation** - Add docstrings where missing

---

## 6. ESTIMATED PERFORMANCE GAINS

If all optimizations are implemented:

- **Build time:** 30-40% faster for large datasets
- **Update time:** 25-35% faster
- **Timetable generation:** 40-50% faster (with caching)
- **Memory usage:** 15-20% reduction

---

## 7. CONCLUSION

The codebase is generally well-structured but has several areas for improvement:

1. **Code clashes:** Duplicate SLOT_ENDS definitions need consolidation
2. **Potential errors:** Date repair logic may hide issues, missing validations
3. **Performance:** Multiple optimization opportunities, especially around sorting and caching

**Recommended Action Plan:**
1. Fix critical issues first (SLOT_ENDS consolidation)
2. Add validations and error handling
3. Implement performance optimizations incrementally
4. Add tests to prevent regressions

---

**Report Generated:** 2025-01-27  
**Analyzed Files:** 15+ Python modules  
**Lines Analyzed:** ~3000+

