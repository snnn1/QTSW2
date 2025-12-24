# SL Column in Master Matrix - Explanation

## What SL Represents

**SL** stands for **Stop Loss** - the maximum loss amount for a trade.

## Historical Calculation Formula

The SL column was previously calculated as:
```
SL = 3 × Target
But capped at: min(3 × Target, Range)
```

**Example**:
- If Target = 10, then SL = min(30, Range)
- If Range = 20, then SL = 20 (capped at Range)
- If Range = 50, then SL = 30 (not capped)

## Current Status (After Refactor)

**Problem**: After the sequencer refactor, SL calculation was removed from `sequencer_logic.py` because it's considered a "downstream concern" (execution parameter, not sequencing logic).

**Current Behavior**:
- `schema_normalizer.py` adds SL column with `np.nan` (NaN) if missing (line 70)
- SL is **NOT being calculated** anywhere currently
- SL values would be:
  - NaN if not in sequencer output
  - Whatever value exists in the data if it came from analyzer output (if analyzer provides it)

## Where SL Should Be Calculated

Based on the refactor requirements, SL should be calculated in one of these downstream modules:

1. **`schema_normalizer.py`** - If it's considered a derived/schema column
2. **`filter_engine.py`** - If it's part of filtering/execution logic
3. **`master_matrix.py`** - If it's a master matrix specific calculation

## Recommendation

Add SL calculation to `schema_normalizer.py` in the `create_derived_columns()` function:

```python
# Calculate SL (Stop Loss): 3x Target, capped at Range
if 'SL' not in df.columns or df['SL'].isna().all():
    df['SL'] = df.apply(
        lambda row: min(3 * row['Target'], row['Range']) 
        if pd.notna(row.get('Target')) and pd.notna(row.get('Range')) and row['Target'] != 0 and row['Range'] > 0
        else (3 * row['Target'] if pd.notna(row.get('Target')) and row['Target'] != 0 else 0),
        axis=1
    )
```

This would:
- Calculate SL for rows with Target and Range
- Apply the 3x Target rule
- Cap at Range when Range is less than 3x Target
- Set to 0 for rows without valid Target/Range (NoTrade rows)

## What You're Seeing

If you're seeing SL values in your matrix:
1. **They might be from existing data** - If the matrix was built before the refactor
2. **They might come from analyzer output** - If the analyzer writes SL values
3. **They might be NaN** - If the matrix was built after the refactor and SL isn't being calculated

Check the `RowSource` column to see if rows are from Analyzer (real data) or Sequencer (structural NoTrade).

