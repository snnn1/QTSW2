# Master Matrix Architectural Review

**Date:** 2025-01-XX  
**Review Type:** High-Level Architectural & Logic Review  
**Scope:** Master Matrix code and immediate helpers (sequencer_logic, filter_engine, data_loader, schema_normalizer, stream_manager)

---

## Executive Summary

The Master Matrix serves as a **research and sequencing layer** that aggregates analyzer outputs, applies sequencer logic (time changes, rolling RS state), and produces a consistent historical decision record. It feeds downstream systems (Timetable, UI, Robot) without making live execution decisions.

**Overall Assessment:** The architecture is generally well-aligned with its stated purpose, with clear separation of concerns. However, there are several architectural risks and edge cases that warrant attention, particularly around date semantics, silent filtering, and potential downstream misinterpretation of matrix outputs.

---

## 1. Purpose Alignment

### What the Master Matrix Currently Does

The Master Matrix:

1. **Loads analyzer outputs** from `data/analyzed/` across all streams (ES1, ES2, GC1, etc.)
2. **Applies sequencer logic** via `sequencer_logic.py` to select one trade per day per stream based on:
   - Rolling 13-trade history per time slot
   - Points-based scoring (Win=+2, Loss=-2, BE=0, NoTrade=0)
   - Loss-triggered time slot switching
3. **Applies stream filters** via `filter_engine.py` to mark trades as `final_allowed=False` based on:
   - Day-of-week exclusions
   - Day-of-month exclusions (e.g., days 4/16/30 for "2" streams)
   - Time exclusions (`exclude_times`)
4. **Normalizes schema** and adds global columns (day_of_month, dow, session_index, etc.)
5. **Sorts output** by `trade_date`, `entry_time`, `Instrument`, `Stream`
6. **Saves output** to `data/master_matrix/` as both JSON and Parquet

### Purpose Alignment Analysis

#### ✅ **Well-Aligned Areas**

1. **Sequencer Logic Centralization**: Time decisions are correctly owned by `sequencer_logic.py`. The Master Matrix orchestrates but doesn't mutate the `Time` column.

2. **Historical State Recording**: The matrix correctly records historical decisions (which trade was chosen, what time slot was used) without making execution decisions.

3. **Filter Application**: Filters are applied as metadata (`final_allowed`, `filter_reasons`) rather than silently dropping rows.

#### ⚠️ **Areas of Concern**

1. **Filter Application Timing**: Filters are applied **after** sequencer logic in `add_global_columns()`. This means:
   - Sequencer sees all trades (including those that will be filtered)
   - Filtered trades still appear in the matrix output (with `final_allowed=False`)
   - **Risk**: Downstream systems might misinterpret `final_allowed=False` as "don't trade today" when it's actually "historical trade was filtered"

2. **Time Column Semantics**: The `Time` column represents "sequencer's intended trading slot" (correct), but `actual_trade_time` preserves the original analyzer time. This dual representation is good, but:
   - **Risk**: Downstream systems might confuse `Time` (sequencer intent) with `actual_trade_time` (historical fact)
   - **Risk**: If `actual_trade_time` is missing, downstream might fall back to `Time`, which would be incorrect for filtering purposes

3. **NoTrade Handling**: When sequencer selects a time slot but no trade exists at that slot, a synthetic row is created with `Result='NoTrade'`. This is correct for historical completeness, but:
   - **Risk**: Downstream systems might interpret absence of a row as "no trade" when they should look for `Result='NoTrade'`

---

## 2. Authority & Ownership

### Fields Owned by Master Matrix

#### ✅ **Correctly Owned**

1. **`Time` column**: Owned by `sequencer_logic.py` (correctly documented)
   - Represents sequencer's intended trading slot for that day
   - Never mutated by Master Matrix (only read)
   - **Status**: ✅ Correct

2. **`Time Change` column**: Calculated by Master Matrix after sorting
   - Shows when time changed from previous day
   - Only set when previous day had `Result='LOSS'`
   - **Status**: ✅ Correct

3. **`final_allowed` column**: Owned by `filter_engine.py`
   - Marks trades as allowed/blocked based on filters
   - **Status**: ⚠️ **RISK** - This is historical eligibility, not live execution authority

4. **`trade_date` column**: Derived from `Date` column
   - Used for sorting and date range filtering
   - **Status**: ✅ Correct

5. **Global columns** (`day_of_month`, `dow`, `session_index`, `is_two_stream`, `dom_blocked`): Owned by `filter_engine.py`
   - Used for filtering logic
   - **Status**: ✅ Correct

### Fields That Should Be Read-Only Historical Data

#### ✅ **Correctly Treated as Read-Only**

1. **`Date`**: Original analyzer date (read-only, copied to `trade_date`)
2. **`Result`**: Analyzer result (Win/Loss/BE/NoTrade) - read-only
3. **`Profit`**: Analyzer profit - read-only
4. **`Target`**, **`Range`**, **`SL`**: Analyzer values - read-only
5. **`actual_trade_time`**: Original analyzer time (preserved by sequencer) - read-only

### Authority Risks

#### 🔴 **CRITICAL RISK: `final_allowed` Misinterpretation**

**Location**: `filter_engine.py:apply_stream_filters()`

**Issue**: `final_allowed` is a **historical eligibility flag** (was this trade filtered when it occurred?), not a **live execution decision** (should we trade today?).

**Risk**: Downstream systems (Timetable, Robot) might:
- Check `final_allowed=True` to decide if a stream trades today
- Treat absence of a row as "no trade" when it should be "no historical data"

**Evidence**:
- `filter_engine.py` sets `final_allowed=False` for filtered trades
- These trades still appear in matrix output (not dropped)
- Timetable engine reads matrix but applies its own filters (correct)
- Robot reads timetable, not matrix directly (correct)

**Recommendation**: Document that `final_allowed` is historical metadata only. Timetable and Robot should never read `final_allowed` for execution decisions.

#### ⚠️ **MODERATE RISK: Missing Row vs. NoTrade**

**Location**: `sequencer_logic.py:process_stream_daily()`

**Issue**: When sequencer selects a time slot but no trade exists, a synthetic row is created with `Result='NoTrade'`. However, if a stream has no data for a date range, no rows are created at all.

**Risk**: Downstream systems might:
- Interpret absence of rows as "no trade" when it's actually "no data"
- Miss `Result='NoTrade'` rows if they filter by `Result`

**Evidence**:
- `sequencer_logic.py:472-482` creates synthetic NoTrade rows
- `data_loader.py` returns empty DataFrame if no files found
- Matrix output might have gaps in date ranges

**Recommendation**: Document that:
- Absence of a row = no historical data (cannot infer intent)
- `Result='NoTrade'` = sequencer chose a slot but no trade existed (explicit intent)

---

## 3. Silent Failure Modes

### Continue/Skip Logic Analysis

#### ✅ **Explicit Handling (Good)**

1. **Missing Stream Directories**: `data_loader.py:134-140`
   - Returns `(False, None, stream_id)` tuple
   - Logs warning
   - **Status**: ✅ Explicit failure, not silent

2. **Empty Files**: `data_loader.py:166-167`
   - Skips empty files with `continue`
   - Logs debug message
   - **Status**: ✅ Explicit skip

3. **Invalid Dates**: `master_matrix.py:358-471`
   - Attempts to repair invalid dates
   - Preserves rows with sentinel date (`2099-12-31`)
   - Logs warnings
   - **Status**: ✅ Explicit handling

#### ⚠️ **Potential Silent Failures**

1. **Stream Discovery Failures**: `stream_manager.py:discover_streams()`
   - If `analyzer_runs_dir` doesn't exist, returns empty list
   - Master Matrix continues with empty streams list
   - **Risk**: Empty output without clear error message
   - **Location**: `master_matrix.py:87`, `master_matrix.py:205-207`

2. **Date Filtering**: `data_loader.py:apply_date_filters()`
   - If date filters exclude all data, returns empty DataFrame
   - No explicit "no data for date range" message
   - **Risk**: Silent empty result
   - **Location**: `master_matrix.py:321-323` (checks for empty, but only logs warning)

3. **Sequencer Filtering**: `sequencer_logic.py:398-408`
   - Filters out excluded times from `date_df` before selection
   - Logs warning if trades filtered out
   - **Risk**: If all times are filtered, no trade selected (but NoTrade row created)
   - **Status**: ⚠️ Handled, but could be more explicit

4. **Schema Normalization**: `schema_normalizer.py:normalize_schema()`
   - Adds missing columns with default values (NaN, empty string, etc.)
   - **Risk**: Missing data becomes implicit (NaN) rather than explicit (missing column)
   - **Status**: ⚠️ Acceptable for schema consistency, but could mask data quality issues

### Implicit Assumptions About Data Completeness

#### 🔴 **CRITICAL ASSUMPTION: All Streams Present**

**Location**: `master_matrix.py:build_master_matrix()`

**Issue**: Master Matrix assumes all discovered streams have data. If a stream has no files:
- Stream is discovered but produces no rows
- Matrix output has gaps (some streams missing for some dates)
- No explicit "stream X has no data" marker

**Risk**: Downstream systems might:
- Interpret missing rows as "no trade" when it's "no data"
- Assume all streams are always present

**Evidence**:
- `data_loader.py:200-207` returns empty list if no trades loaded
- `master_matrix.py:321-323` only warns if entire matrix is empty
- Partial failures (some streams missing) are not explicitly marked

**Recommendation**: Consider adding a `data_available` flag per stream/date, or document that missing rows = no data.

#### ⚠️ **MODERATE ASSUMPTION: Date Continuity**

**Location**: `sequencer_logic.py:process_stream_daily()`

**Issue**: Sequencer iterates over dates present in analyzer data (`unique_dates = stream_df['Date_normalized'].unique()`). If data has gaps:
- Sequencer skips missing dates (no rows created)
- Rolling histories continue across gaps
- **Risk**: Downstream might assume daily continuity

**Evidence**:
- `sequencer_logic.py:336-337` uses `unique_dates` from data (not calendar)
- No explicit handling of date gaps
- Rolling histories advance per day processed (not calendar day)

**Status**: ⚠️ Acceptable (data-driven is correct), but should be documented.

### Cases Where Absence Is Treated as Meaning

#### 🔴 **CRITICAL: Missing Rows = No Data, Not "No Trade"**

**Location**: Throughout matrix output

**Issue**: If a stream has no data for a date:
- No row is created (not even a NoTrade row)
- Downstream systems might interpret this as "no trade" when it's actually "no data"

**Risk**: Timetable/Robot might:
- Skip streams that have no historical data
- Assume "no row" means "don't trade today"

**Evidence**:
- `sequencer_logic.py` only creates rows for dates present in analyzer data
- `data_loader.py` returns empty DataFrame if no files found
- Matrix output has no explicit "no data" markers

**Recommendation**: Document that:
- Missing row = no historical data (cannot infer trading intent)
- `Result='NoTrade'` = explicit sequencer decision (no trade at chosen slot)

---

## 4. Date Semantics

### Date Column Analysis

#### ✅ **Correct Usage**

1. **`Date` column**: Original analyzer date (read-only)
2. **`trade_date` column**: Derived from `Date`, used for sorting
3. **Date normalization**: `schema_normalizer.py:60-65` converts to datetime
4. **Date repair**: `master_matrix.py:358-471` attempts to repair invalid dates

#### ⚠️ **Date Semantics Risks**

1. **`trade_date` vs `Date`**: Both exist, `trade_date` is canonical for sorting
   - **Risk**: Downstream might use `Date` instead of `trade_date`
   - **Status**: ⚠️ Documented in code comments

2. **Date Repair Quality**: `master_matrix.py:376-437` repairs invalid dates with quality scores
   - Low-quality repairs (inferred dates) might be unreliable
   - **Risk**: Repaired dates might be incorrect
   - **Status**: ⚠️ Quality scores stored (`date_repair_quality`), but downstream might ignore them

3. **Weekend/Holiday Behavior**: `sequencer_logic.py:336-337` uses data-driven dates (not calendar)
   - No rows created for weekends/holidays (correct)
   - **Risk**: Downstream might assume calendar continuity
   - **Status**: ✅ Correct (data-driven), but should be documented

4. **Forward Use of Prior-Day Data**: Not applicable (matrix is historical only)
   - **Status**: ✅ N/A

### Date Filter Logic

#### ✅ **Correct Implementation**

1. **Date filtering**: `data_loader.py:apply_date_filters()` filters before sequencer
   - **Status**: ✅ Correct (filters input, not output)

2. **Date range queries**: `master_matrix.py:build_master_matrix()` accepts `start_date`, `end_date`, `specific_date`
   - **Status**: ✅ Correct

#### ⚠️ **Potential Confusion**

1. **Historical Dates vs. Decision Dates**: Matrix outputs historical dates (when trades occurred)
   - **Risk**: Downstream might confuse historical dates with "decision dates" (when to trade)
   - **Status**: ⚠️ Should be documented

2. **`matrix_max_date`**: Not explicitly tracked
   - **Risk**: Downstream might assume matrix is always up-to-date
   - **Status**: ⚠️ File timestamps indicate freshness, but no explicit max_date column

---

## 5. Sequencer Integrity

### Sequencer Logic Centralization

#### ✅ **Well-Centralized**

1. **Single Source of Truth**: `sequencer_logic.py` owns all time decisions
   - `current_time` state
   - Rolling histories
   - Loss-triggered time changes
   - **Status**: ✅ Correct

2. **Time Column Authority**: `sequencer_logic.py:467-469` overwrites `Time` column
   - Preserves `actual_trade_time` for filtering
   - **Status**: ✅ Correct

3. **Filter Integration**: `sequencer_logic.py:258-270` handles filtered times correctly
   - `canonical_times` = all session times (always scored)
   - `selectable_times` = canonical minus filtered (for selection only)
   - **Status**: ✅ Correct

#### ⚠️ **Potential Integrity Issues**

1. **Time Change Calculation**: `master_matrix.py:511-556` calculates `Time Change` column
   - Compares consecutive days to detect changes
   - Only shows change if previous day had `Result='LOSS'`
   - **Risk**: If sorting is incorrect, `Time Change` might be wrong
   - **Status**: ⚠️ Depends on correct sorting (which is enforced)

2. **Multiple Time Truths**: `Time` (sequencer intent) vs `actual_trade_time` (analyzer fact)
   - **Risk**: Downstream might confuse the two
   - **Status**: ⚠️ Well-documented, but could be clearer

3. **Time Normalization**: `utils.normalize_time()` used throughout
   - **Risk**: If normalization is inconsistent, comparisons might fail
   - **Status**: ⚠️ Should verify normalization is consistent

### Deterministic Behavior

#### ✅ **Deterministic**

1. **Sequencer Logic**: `sequencer_logic.py:process_stream_daily()` processes dates in order
   - Rolling histories advance deterministically
   - Time changes are deterministic (based on loss + rolling sums)
   - **Status**: ✅ Correct

2. **Sorting**: `master_matrix.py:502-506` sorts deterministically
   - By `trade_date`, `entry_time`, `Instrument`, `Stream`
   - **Status**: ✅ Correct

#### ⚠️ **Potential Non-Determinism**

1. **Parallel Processing**: `sequencer_logic.py:623-657` processes streams in parallel
   - **Risk**: If parallel execution order varies, output order might vary
   - **Status**: ⚠️ Output is sorted after processing, so order is deterministic

2. **File Loading Order**: `data_loader.py:find_parquet_files()` uses `sorted()`
   - **Status**: ✅ Deterministic

---

## 6. Output Contract Quality

### Completeness Assessment

#### ✅ **Complete Output**

1. **All Streams**: Matrix includes all discovered streams (if they have data)
2. **All Dates**: Matrix includes all dates present in analyzer data
3. **All Columns**: Schema normalization ensures consistent columns

#### ⚠️ **Incomplete Output Risks**

1. **Missing Streams**: If a stream has no data, no rows are created
   - **Risk**: Downstream assumes all streams are always present
   - **Status**: ⚠️ Should be documented

2. **Missing Dates**: If analyzer data has gaps, matrix has gaps
   - **Risk**: Downstream assumes daily continuity
   - **Status**: ⚠️ Should be documented

3. **Missing Columns**: Schema normalization adds defaults (NaN, empty string)
   - **Risk**: Missing data becomes implicit (NaN) rather than explicit
   - **Status**: ⚠️ Acceptable for schema consistency

### Deterministic Output

#### ✅ **Deterministic**

1. **Sorting**: Output is sorted deterministically
2. **Sequencer Logic**: Deterministic (same input → same output)
3. **Filter Application**: Deterministic

#### ⚠️ **Potential Non-Determinism**

1. **Global Trade ID**: `master_matrix.py:509` assigns sequential IDs after sorting
   - **Risk**: If sorting changes, IDs change
   - **Status**: ⚠️ IDs are not stable across rebuilds (acceptable for historical data)

### Explicit vs. Implicit Assumptions

#### ✅ **Explicit**

1. **Filter Reasons**: `filter_engine.py` sets `filter_reasons` column
2. **Date Repair**: `master_matrix.py` sets `date_repaired` and `date_repair_quality` flags
3. **NoTrade Rows**: Synthetic rows created with `Result='NoTrade'`

#### ⚠️ **Implicit Assumptions**

1. **Missing Rows = No Data**: Not explicitly marked
2. **Stream Presence**: Assumes all streams are always present
3. **Date Continuity**: Assumes daily continuity (but actually data-driven)

### Downstream Consumption Safety

#### ✅ **Safe for Downstream**

1. **Timetable Engine**: Reads matrix but applies its own filters (correct)
2. **Robot**: Reads timetable, not matrix directly (correct)
3. **UI**: Reads matrix for display (correct)

#### ⚠️ **Potential Misinterpretation**

1. **`final_allowed` Flag**: Historical eligibility, not live execution decision
   - **Risk**: Downstream might use it for execution decisions
   - **Status**: ⚠️ Should be documented

2. **Missing Rows**: No explicit "no data" marker
   - **Risk**: Downstream might interpret as "no trade"
   - **Status**: ⚠️ Should be documented

3. **`Time` vs `actual_trade_time`**: Dual representation
   - **Risk**: Downstream might confuse the two
   - **Status**: ⚠️ Well-documented in code, but could be clearer in output schema

---

## Summary of Risks

### 🔴 **Critical Risks**

1. **`final_allowed` Misinterpretation**: Historical eligibility flag might be used for live execution decisions
   - **Mitigation**: Document that `final_allowed` is historical metadata only

2. **Missing Rows vs. NoTrade**: Absence of rows might be interpreted as "no trade" when it's "no data"
   - **Mitigation**: Document that missing rows = no historical data, `Result='NoTrade'` = explicit sequencer decision

### ⚠️ **Moderate Risks**

1. **Date Repair Quality**: Low-quality repairs might be unreliable
   - **Mitigation**: Quality scores stored, but downstream should verify

2. **Time Column Semantics**: `Time` vs `actual_trade_time` confusion
   - **Mitigation**: Well-documented in code, but could be clearer in output schema

3. **Stream Presence Assumptions**: Downstream might assume all streams are always present
   - **Mitigation**: Document that missing rows = no data for that stream/date

### ✅ **Acceptable as-Is**

1. **Filter Application Timing**: Filters applied after sequencer (correct for historical recording)
2. **Date-Driven Processing**: Data-driven dates (not calendar) is correct
3. **Schema Normalization**: Adding defaults for missing columns is acceptable for consistency
4. **Parallel Processing**: Deterministic after sorting

---

## Recommendations

### Documentation Recommendations

1. **Output Schema Documentation**: Document that:
   - `Time` = sequencer's intended trading slot (for that historical day)
   - `actual_trade_time` = original analyzer time (for filtering)
   - `final_allowed` = historical eligibility flag (not live execution decision)
   - Missing row = no historical data (cannot infer intent)
   - `Result='NoTrade'` = explicit sequencer decision (no trade at chosen slot)

2. **Date Semantics Documentation**: Document that:
   - Matrix uses data-driven dates (not calendar)
   - Missing dates = no analyzer data for that date
   - Date repair quality scores indicate confidence

3. **Stream Presence Documentation**: Document that:
   - All discovered streams are included (if they have data)
   - Missing rows = no data for that stream/date
   - Downstream should not assume all streams are always present

### Code Recommendations (Non-Breaking)

1. **Add `data_available` Flag**: Consider adding a per-stream/date flag indicating data availability
   - Would make "no data" explicit rather than implicit

2. **Enhance Logging**: Add explicit "no data" messages when streams/dates are missing
   - Would make silent failures more visible

3. **Output Schema Comments**: Add comments to output schema explaining column semantics
   - Would help downstream systems understand the contract

---

## Conclusion

The Master Matrix architecture is **generally well-aligned** with its stated purpose as a research and sequencing layer. The separation of concerns is clear, and sequencer logic is properly centralized. However, there are several **architectural risks** around date semantics, silent filtering, and potential downstream misinterpretation of matrix outputs.

**Key Takeaways:**

1. ✅ **Sequencer logic is well-centralized** - Time decisions are owned by `sequencer_logic.py`
2. ✅ **Filters are applied correctly** - As metadata, not silent drops
3. ⚠️ **`final_allowed` is historical only** - Should not be used for live execution decisions
4. ⚠️ **Missing rows = no data** - Not "no trade" (should be documented)
5. ⚠️ **Date semantics are data-driven** - Not calendar-driven (should be documented)

**Overall Assessment:** The code is **architecturally sound** but would benefit from **enhanced documentation** to prevent downstream misinterpretation of matrix outputs.
