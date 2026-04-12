# Master Matrix Architecture - Comprehensive Answers

## A. Master Matrix Structure & Lifecycle

### Q1. Where exactly is the Master Matrix built?

**Single Authoritative Build Path:**

1. **Entry Point**: `modules/matrix/api.py` → `@router.post("/build")` → `build_master_matrix()`
2. **Orchestrator**: `modules/matrix/master_matrix.py` → `MasterMatrix.build_master_matrix()`
3. **Data Loading**: `modules/matrix/data_loader.py` → `load_all_streams()`
4. **Sequencing**: `modules/matrix/sequencer_logic.py` → `apply_sequencer_logic()`
5. **Schema Normalization**: `modules/matrix/schema_normalizer.py` → `normalize_schema()`
6. **Filter Application**: `modules/matrix/filter_engine.py` → `add_global_columns()` → `apply_stream_filters()`
7. **Statistics**: `modules/matrix/statistics.py` → `calculate_summary_stats()`
8. **Output**: `modules/matrix/file_manager.py` → `save_master_matrix()`

**Exact Build Flow:**
```
MasterMatrix.build_master_matrix()
  → _load_all_streams_with_sequencer()  [loads + sequences]
    → data_loader.load_all_streams()
      → sequencer_logic.apply_sequencer_logic()  [selects trades]
  → normalize_schema()  [standardizes columns]
  → add_global_columns()  [applies filters, sets final_allowed]
  → file_manager.save_master_matrix()  [writes parquet]
```

### Q2. Is the Master Matrix built incrementally or fully rebuilt?

**Hybrid System:**

- **Full Rebuild**: Default mode - rebuilds everything from scratch (`_rebuild_full()`)
- **Partial Rebuild**: When `streams` parameter provided - rebuilds only specified streams and merges with existing (`_rebuild_partial()`)
- **Checkpoint System**: `modules/matrix/checkpoint_manager.py` supports state checkpointing for sequencer state

**Implications for Calendar Blocks:**
- Calendar blocks should be persisted separately (like sequencer state checkpoints)
- Partial rebuilds merge with existing data, so calendar state must be mergeable
- Use checkpoint pattern: `data/master_matrix/checkpoints/calendar_blocks_*.json`

### Q3. At what point is `final_allowed` set?

**Single Point of Truth:**

`final_allowed` is set **during matrix build** in `modules/matrix/filter_engine.py`:

1. **Initialization**: Line 112 - `df['final_allowed'] = True` (all trades start allowed)
2. **Filter Application**: Lines 203, 216, 247 - `df.loc[mask, 'final_allowed'] = False` (filtered trades marked)
3. **Timing**: After sequencer logic, before statistics calculation
4. **Never Mutated After**: Once set, `final_allowed` is never changed downstream

**Last Point of Truth**: `filter_engine.add_global_columns()` is the **only** place that sets `final_allowed`. Calendar blocks should check this flag before counting trades.

---

## B. Stream & Sequencing Mechanics

### Q4. What object/function decides "this trade is eligible to be taken today"?

**Exact Function:**

`modules/matrix/sequencer_logic.py` → `process_stream_daily()` → `select_trade_for_time()`

**Function Signature:**
```python
def select_trade_for_time(
    date_df: pd.DataFrame,      # All trades for a single day
    current_time: str,          # Current time slot (e.g., "07:30")
    current_session: str       # Session (S1 or S2)
) -> Optional[pd.Series]:      # Returns chosen trade or None
```

**Input Flow:**
1. `process_stream_daily()` iterates day-by-day chronologically
2. For each day, filters `date_df` to trades matching `current_time` and `current_session`
3. Calls `select_trade_for_time()` which selects ONE trade per day
4. Returns chosen trade dict or None (if no trade available)

**Output:**
- Returns `pd.Series` (single trade row) if trade selected
- Returns `None` if no trade available for that time slot
- Only ONE trade per day per stream is selected

**Where Calendar Block Check Should Live:**
- **Option 1**: In `select_trade_for_time()` - check calendar blocks before selecting trade
- **Option 2**: In `process_stream_daily()` - check calendar blocks before calling `select_trade_for_time()`
- **Recommendation**: Option 2 (higher level) - allows calendar blocks to prevent trade selection entirely

### Q5. Does the sequencer already consume external health signals?

**Yes - Filter System:**

The sequencer already consumes `stream_filters` which include:
- `exclude_days_of_week` (DOW filtering)
- `exclude_days_of_month` (DOM filtering)  
- `exclude_times` (time slot filtering)

**Integration Point:**
- `process_stream_daily()` receives `stream_filters: Dict` parameter
- Filters applied at line 288-292: `selectable_times = canonical_times - filtered_times`
- Filtered times excluded from selection (but still scored in rolling histories)

**Calendar Blocks Integration:**
- **Follow same pattern**: Add calendar block check alongside existing filter checks
- **Hook location**: In `process_stream_daily()` before `select_trade_for_time()` call
- **State model**: Calendar blocks should follow same state restoration pattern as sequencer state

### Q6. Is the sequencer stateless per run, or does it load prior state?

**Stateful with Restoration:**

**State Structure:**
```python
{
    "current_time": str,              # Current time slot
    "current_session": str,            # S1 or S2
    "time_slot_histories": {           # Rolling histories per time slot
        "07:30": [1, -1, 0, ...],     # Scores for last N days
        "08:00": [1, 1, -2, ...],
        ...
    }
}
```

**State Management:**
- `apply_sequencer_logic_with_state()` returns final states for checkpointing
- `CheckpointManager` saves/loads sequencer state: `data/master_matrix/checkpoints/sequencer_state_*.json`
- `initial_states` parameter allows state restoration
- Rolling histories are **persistent** across runs (40-day window)

**Calendar Blocks State Model:**
- **Must follow same pattern**: Persist calendar block state alongside sequencer state
- **Checkpoint location**: `data/master_matrix/checkpoints/calendar_blocks_*.json`
- **Restoration**: Load calendar block state before `process_stream_daily()` starts

---

## C. Trade Outcome & Points Data

### Q7. Where are trade outcomes defined?

**Exact Column Names:**

**Result Column:**
- Column name: `Result` (string)
- Values: `"WIN"`, `"LOSS"`, `"BE"`, `"BREAKEVEN"`, `"TIME"`, `"NoTrade"`, `"OPEN"`
- Normalized column: `ResultNorm` (uppercase, standardized)

**Profit Column:**
- Column name: `Profit` (float) - points/profit in contract points
- Column name: `ProfitDollars` (float) - profit in dollars (computed from `Profit * contract_multiplier * contract_value`)

**Points Column:**
- **No separate points column** - `Profit` IS the points column
- Points are contract-specific (e.g., ES = $50/point, NQ = $20/point)
- `ProfitDollars` = `Profit * contract_multiplier * contract_value`

**Outcome Classification:**
- **WIN**: `Result == "WIN"` → `Profit > 0`
- **LOSS**: `Result == "LOSS"` → `Profit < 0`
- **BE/Scratch**: `Result in ["BE", "BREAKEVEN"]` → `Profit == 0` (or -1 tick if T1 triggered)
- **TIME**: `Result == "TIME"` → Trade expired without target/stop
- **NoTrade**: `Result == "NoTrade"` → No trade executed

**Source:**
- `modules/matrix/statistics.py` lines 434-440: Result classification logic
- `modules/analyzer/logic/instrument_logic.py` lines 271-333: Profit calculation

### Q8. Are trade rows strictly time-ordered and stable?

**Yes - Guaranteed Ordering:**

**Sort Order (Canonical):**
```python
df.sort_values(['trade_date', 'entry_time', 'Instrument', 'Stream'], ascending=[True, True, True, True])
```

**Ordering Guarantees:**
1. **Chronological**: `trade_date` ascending (oldest first)
2. **Within Day**: `entry_time` ascending (earliest first)
3. **Stable**: Sort is deterministic (mergesort algorithm)
4. **No Backfills**: Once written, trades are never modified
5. **No Corrections**: Analyzer output is final - no post-hoc corrections

**Rolling Window Safety:**
- **Safe for rolling calculations**: Data is strictly chronological
- **No lookahead risk**: Processing is always forward-only
- **Window boundaries**: Use `trade_date` for day boundaries (not `Date` string)

**Source:**
- `modules/matrix/master_matrix.py` line 367-369: Canonical sort order documented
- `modules/matrix/sequencer_logic.py` line 669-683: Sort validation

### Q9. Are rejected/filtered trades written to the matrix with a flag, or omitted entirely?

**Written with Flag:**

**Filtered trades are INCLUDED in matrix with `final_allowed=False`:**

1. **All trades loaded**: Sequencer loads ALL trades from analyzer output
2. **Filtered trades marked**: `filter_engine` sets `final_allowed=False` for filtered trades
3. **Filter reasons stored**: `filter_reasons` column contains reason strings (e.g., `"dow_filter(monday)"`)
4. **Never omitted**: Filtered trades remain in matrix for analysis

**Execution Eligibility:**
- **`final_allowed == True`**: Trade is eligible for execution
- **`final_allowed == False`**: Trade is filtered (DOW/DOM/time filter)
- **`Result == "NoTrade"`**: No trade executed (different from filtered)

**Calendar Blocks:**
- **Count only executed trades**: `Result in ["WIN", "LOSS", "BE", "BREAKEVEN", "TIME"]`
- **Respect final_allowed**: Only count executed trades where `final_allowed == True`
- **Filtered executed trades**: Can be included/excluded via `include_filtered_executed` flag

**Source:**
- `modules/matrix/filter_engine.py` lines 111-112: `final_allowed` initialization
- `modules/matrix/statistics.py` lines 408-418: Executed trade filtering logic

---

## D. Calendar Data Availability

### Q10. Where are `day_of_month` and `dow` computed?

**Exact Location:**

**File**: `modules/matrix/filter_engine.py`
**Function**: `add_global_columns()`
**Lines**: 80-85

```python
# day_of_month
df['day_of_month'] = df['trade_date'].dt.day  # 1-31

# dow (day of week)
df['dow'] = df['trade_date'].dt.strftime('%a')  # Mon, Tue, Wed, etc.
df['dow_full'] = df['trade_date'].dt.strftime('%A')  # Monday, Tuesday, etc.
```

**Timezone:**
- **Source**: `trade_date` column (datetime dtype, normalized by `data_loader.py`)
- **Timezone**: Uses pandas datetime (timezone-naive, assumes local/Chicago time)
- **Normalization**: `data_loader.py` → `_normalize_date_to_trade_date()` converts `Date` string to `trade_date` datetime

**Computation:**
- **Computed once**: During `add_global_columns()` call
- **Never recomputed**: Columns added to DataFrame and persisted
- **Always present**: Guaranteed to exist after `filter_engine.add_global_columns()` runs

### Q11. Are DOM/DOW columns guaranteed present for all trades?

**Yes - Guaranteed:**

**Contract:**
- `add_global_columns()` is called **after** sequencer logic
- All trades in matrix have `day_of_month`, `dow`, `dow_full` columns
- Historical trades: If matrix was built with old code, columns may be missing

**Safe Backfill:**
- Check: `if 'day_of_month' not in df.columns:`
- Backfill: Recompute from `trade_date` (same logic as `add_global_columns()`)
- Location: In calendar block calculation function (defensive check)

**Source:**
- `modules/matrix/filter_engine.py` lines 80-85: Column creation
- `modules/matrix/master_matrix.py` line 453: `add_global_columns()` call

---

## E. Persistence & Artifacts

### Q12. What persistent artifacts exist alongside Master Matrix?

**Existing Artifacts:**

1. **Master Matrix Files**: `data/master_matrix/master_matrix_YYYYMMDD_HHMMSS.parquet`
2. **Sequencer Checkpoints**: `data/master_matrix/checkpoints/sequencer_state_*.json`
3. **Run History**: `automation/logs/runs/runs.jsonl` (pipeline run summaries)
4. **Orchestrator State**: `automation/logs/orchestrator_state.json` (pipeline state)
5. **Breakdown Caches**: Frontend caches breakdown calculations (not persisted)
6. **Timetable Files**: `data/timetable/timetable_*.parquet` (execution timetables)

**Pattern:**
- **Versioned**: Timestamped filenames (`*_YYYYMMDD_HHMMSS.*`)
- **Checkpoints**: Separate `checkpoints/` subdirectory
- **JSONL**: Append-only logs (run history)
- **Parquet**: Columnar data files (matrix, timetables)

**Calendar Blocks Artifact:**
- **Location**: `data/master_matrix/checkpoints/calendar_blocks_YYYYMMDD_HHMMSS.json`
- **Format**: JSON (like sequencer state)
- **Versioned**: Timestamped filename
- **Pattern**: Mirror sequencer checkpoint pattern

### Q13. Established patterns for derived state tables?

**Patterns:**

**1. Checkpoint Pattern** (Sequencer State):
- **Location**: `data/master_matrix/checkpoints/`
- **Format**: JSON files
- **Naming**: `{name}_YYYYMMDD_HHMMSS.json`
- **Overwrite**: New checkpoint overwrites old (or keep last N)
- **Restoration**: Load latest checkpoint on startup

**2. Run History Pattern** (Pipeline Runs):
- **Location**: `automation/logs/runs/`
- **Format**: JSONL (append-only)
- **Naming**: `runs.jsonl` (single file)
- **Overwrite**: Never overwrites, always appends
- **Query**: Read all lines, filter in memory

**3. Timetable Pattern** (Execution Timetables):
- **Location**: `data/timetable/`
- **Format**: Parquet + JSON
- **Naming**: `timetable_{date}_{timestamp}.parquet`
- **Overwrite**: New file per build (keep historical)
- **Query**: Load specific date or latest

**Calendar Blocks Recommendation:**
- **Use Checkpoint Pattern**: `data/master_matrix/checkpoints/calendar_blocks_*.json`
- **Versioned**: Timestamped filename
- **Overwrite**: Keep last N checkpoints (e.g., last 10)
- **Restoration**: Load latest on matrix build start

---

## F. Logging & Explainability

### Q14. How are rejection reasons currently logged/stored?

**Format:**

**Column**: `filter_reasons` (string)
**Format**: Comma-separated reason strings
**Examples**:
- `"dow_filter(monday,wednesday)"`
- `"dom_filter(4,16,30)"`
- `"time_filter(07:30,08:00)"`
- `"dow_filter(friday), dom_filter(30)"` (multiple filters)

**Storage:**
- **In DataFrame**: `filter_reasons` column (persisted in parquet)
- **Not in logs**: Filter reasons not logged separately (only in matrix)
- **Structured**: Simple string format (not JSON/structured object)

**Calendar Block Reasons:**
- **Follow same pattern**: Add to `filter_reasons` column
- **Format**: `"calendar_block({reason})"` (e.g., `"calendar_block(3_losses_in_5_days)"`)
- **Append**: Add to existing `filter_reasons` if other filters also apply

**Source:**
- `modules/matrix/filter_engine.py` lines 205-210, 218-223: Reason string construction

### Q15. Is there an existing debug/audit view for rejections?

**Yes - Multiple Views:**

1. **Matrix UI**: `modules/matrix_timetable_app/frontend/` - Shows `filter_reasons` column
2. **Statistics Logging**: `modules/matrix/statistics.py` logs filtered vs allowed counts
3. **API Endpoint**: `/api/matrix/data` returns `filter_reasons` column
4. **Breakdown Views**: DOW/DOM breakdowns show filtered vs allowed trades

**Calendar Blocks Integration:**
- **Add to filter_reasons**: Calendar block reasons will automatically appear in UI
- **Statistics**: Filtered count will include calendar-blocked trades
- **Breakdowns**: Calendar-blocked trades will be visible in DOW/DOM breakdowns

**Source:**
- `modules/matrix/api.py` line 549: `filter_reasons` included in API response
- `modules/matrix/statistics.py` lines 442-454: Filtered trade logging

---

## G. Safety Checks

### Q16. Existing safeguards against lookahead bias?

**Yes - Multiple Safeguards:**

1. **Chronological Processing**: `process_stream_daily()` iterates day-by-day forward-only
2. **No Future Data**: Sequencer only sees trades up to current date
3. **Rolling Windows**: `history_manager.py` uses fixed-size deque (40 days)
4. **State Isolation**: Each stream processed independently (no cross-stream lookahead)
5. **Date Filtering**: `data_loader.py` filters by date range before sequencing

**Calendar Blocks Safeguards:**
- **Use same pattern**: Process day-by-day, check calendar blocks before trade selection
- **Rolling window**: Calendar blocks use fixed window (e.g., last 5 days)
- **No future data**: Only count executed trades up to current date
- **State isolation**: Calendar blocks per-stream (no cross-stream contamination)

**Source:**
- `modules/matrix/sequencer_logic.py` lines 237-593: Day-by-day processing
- `modules/matrix/history_manager.py`: Rolling window implementation

### Q17. What happens if expected artifact is missing?

**Fail-Closed Pattern:**

**Sequencer State:**
- **Missing checkpoint**: Starts with default state (first time slot, empty histories)
- **Fail-safe**: If checkpoint corrupt, falls back to default state
- **No skip**: Always processes (doesn't skip if state missing)

**Master Matrix:**
- **Missing matrix**: Builds from scratch (no existing matrix to merge)
- **Missing streams**: Validates critical streams, warns on non-critical
- **Fail-fast**: Invalid dates cause build failure (unless salvage mode)

**Calendar Blocks:**
- **Missing checkpoint**: Start with empty calendar block state (no blocks active)
- **Fail-safe**: If checkpoint corrupt, start fresh (no blocks)
- **No skip**: Always check calendar blocks (even if state missing, no blocks = all allowed)

**Source:**
- `modules/matrix/checkpoint_manager.py`: Checkpoint loading with fallback
- `modules/matrix/master_matrix.py` lines 487-544: Invalid date handling

---

## Minimal Subset Answers (Critical 6)

### 1. Where is the Master Matrix built?
**Answer**: `modules/matrix/master_matrix.py` → `MasterMatrix.build_master_matrix()`
- Loads: `data_loader.load_all_streams()`
- Sequences: `sequencer_logic.apply_sequencer_logic()`
- Filters: `filter_engine.add_global_columns()`
- Writes: `file_manager.save_master_matrix()`

### 2. Where does sequencing/eligibility decision happen?
**Answer**: `modules/matrix/sequencer_logic.py` → `process_stream_daily()` → `select_trade_for_time()`
- **Hook for calendar blocks**: In `process_stream_daily()` before `select_trade_for_time()` call
- **State**: Per-stream state with rolling histories
- **Pattern**: Check filters, then check calendar blocks, then select trade

### 3. Where do trade outcomes & points live?
**Answer**: 
- **Result**: `Result` column (`"WIN"`, `"LOSS"`, `"BE"`, `"TIME"`, `"NoTrade"`)
- **Points**: `Profit` column (contract points)
- **Dollars**: `ProfitDollars` column (computed)
- **Source**: Analyzer output, normalized by `schema_normalizer.py`

### 4. Are trades strictly ordered and executed-only?
**Answer**: 
- **Ordered**: Yes - strictly chronological (`trade_date` ascending)
- **Stable**: No backfills or corrections after write
- **Executed-only for calendar blocks**: Count only `Result in ["WIN", "LOSS", "BE", "BREAKEVEN", "TIME"]`
- **Respect final_allowed**: Only count executed trades where `final_allowed == True`

### 5. Are DOM/DOW already computed and stable?
**Answer**: 
- **Computed**: Yes - in `filter_engine.add_global_columns()` (lines 80-85)
- **Stable**: Yes - computed once, never recomputed
- **Guaranteed**: Present for all trades after `add_global_columns()` runs
- **Backfill**: Defensive check recommended for historical matrices

### 6. How are other health/RS signals integrated today?
**Answer**: 
- **Pattern**: `stream_filters` dict passed to `process_stream_daily()`
- **Integration**: Filters applied before trade selection (line 288-292)
- **State**: Filters are stateless (no persistence needed)
- **Calendar blocks**: Follow same pattern - add to `stream_filters` or separate check before `select_trade_for_time()`

---

## Implementation Recommendations

### Calendar Blocks Integration Points

1. **State Persistence**: `data/master_matrix/checkpoints/calendar_blocks_*.json`
2. **Check Location**: `process_stream_daily()` before `select_trade_for_time()` call
3. **State Structure**: Per-stream rolling window (last N days of executed trades)
4. **Filter Reasons**: Append `"calendar_block({reason})"` to `filter_reasons` column
5. **Statistics**: Calendar-blocked trades included in filtered counts
6. **Safeguards**: Use same chronological processing, no lookahead, fail-safe on missing state

### Exact Files to Modify

1. **`modules/matrix/sequencer_logic.py`**: Add calendar block check in `process_stream_daily()`
2. **`modules/matrix/checkpoint_manager.py`**: Add calendar block state persistence
3. **`modules/matrix/filter_engine.py`**: Add calendar block reason to `filter_reasons`
4. **`modules/matrix/master_matrix.py`**: Load calendar block state before sequencing

### Data Flow

```
MasterMatrix.build_master_matrix()
  → Load calendar block state (if exists)
  → _load_all_streams_with_sequencer()
    → sequencer_logic.apply_sequencer_logic()
      → process_stream_daily() [FOR EACH STREAM]
        → Check calendar blocks (before select_trade_for_time)
        → select_trade_for_time() [if not blocked]
        → Update calendar block state (after trade selection)
  → Save calendar block state (checkpoint)
  → add_global_columns() [adds filter_reasons]
```

---

**End of Answers**
