# Sequencer Refactor Summary - Data-Driven Remodel

## Changes Made

### 1. ✅ Removed Calendar Iteration (CRITICAL)

**Before**:
```python
min_date = stream_df['Date'].min().normalize()
max_date = stream_df['Date'].max().normalize()
all_dates = pd.date_range(start=min_date, end=max_date, freq='D')  # ❌ Calendar-driven
for date in all_dates:
```

**After**:
```python
# Iterate only over dates present in analyzer data (data-driven, not calendar)
unique_dates = stream_df['Date'].dt.normalize().unique()
trading_dates = sorted(unique_dates)  # ✅ Data-driven
for date in trading_dates:
```

**Impact**: 
- No weekends/holidays unless present in analyzer data
- No calendar assumptions
- Purely data-driven iteration

### 2. ✅ Removed SL Calculation

**Removed**: Lines 331-340 that calculated SL (Stop Loss) as 3x Target
- This is a downstream concern (schema_normalizer or filter_engine)
- Sequencer should not compute execution parameters

**Also removed**: SL column addition at end of `apply_sequencer_logic()` (lines 498-501)

### 3. ✅ Removed Time Change Formatting

**Removed**: Lines 339-345 that formatted "Time Change" column (e.g., "07:30→08:00")
- This is UI/display formatting, not sequencer logic
- Should be handled downstream if needed

### 4. ✅ Added RowSource Column

**Added**: `RowSource` column to distinguish data origin:
- `'Analyzer'`: Row came from analyzer output (real data)
- `'Sequencer'`: Row created by sequencer when no analyzer data exists at chosen time slot

**Location**: Lines 316 and 325
```python
if trade_row is not None:
    trade_dict['RowSource'] = 'Analyzer'  # Real data
else:
    trade_dict['RowSource'] = 'Sequencer'  # Structural NoTrade
```

**Purpose**: 
- Makes it clear which rows are real vs structural
- Enables downstream filtering/analysis
- Maintains data integrity transparency

## What Sequencer Now Does (Minimal)

1. ✅ Iterates over trading days present in data (no calendar)
2. ✅ Updates rolling histories for all canonical times
3. ✅ Decides time changes after LOSS
4. ✅ Selects one row per trading day per stream
5. ✅ Adds rolling sum columns for all canonical times
6. ✅ Adds RowSource to identify data origin
7. ✅ Sets Time column to sequencer's intended slot
8. ✅ Adds trade_date from Date

## What Sequencer No Longer Does

1. ❌ Calendar iteration (weekends/holidays)
2. ❌ SL calculation
3. ❌ Time Change formatting
4. ❌ UI/display formatting
5. ❌ Schema normalization
6. ❌ Filtering logic

## Files Modified

- `modules/matrix/sequencer_logic.py` - Core refactor

## Verification Needed

The sequencer is now data-driven and minimal. To verify correctness:

1. **Test with real data**: Run sequencer on actual analyzer output
2. **Check RowSource**: Verify Analyzer vs Sequencer rows are correctly marked
3. **Check date iteration**: Ensure no weekends appear unless in data
4. **Check rolling histories**: Verify all canonical times advance equally
5. **Check invariants**: Time changes only after LOSS, no filtered times selected

## Next Steps (Downstream)

Other modules may need updates:

1. **schema_normalizer.py**: Should add SL column if needed
2. **filter_engine.py**: Should handle any filtering logic
3. **master_matrix.py**: Should not mutate Time column
4. **API**: Should handle RowSource column appropriately

## Remaining Risks

1. **Downstream dependencies**: Other modules may expect SL, Time Change columns
2. **Testing**: Need to verify behavior with real data
3. **RowSource**: Downstream code needs to handle this new column

