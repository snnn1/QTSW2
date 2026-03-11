# NoTrade Discrepancy Assessment

## Summary

Many days are marked **NoTrade** in the master matrix when the analyzer produced **Win/Loss/BE** (executed trades) for the same date and time slot. A diagnostic script found **870 discrepancies** across 12 streams.

## Diagnostic Results (Sample Run)

| Stream | Discrepancies |
|--------|---------------|
| GC2    | 200 |
| NG2    | 135 |
| CL2    | 102 |
| YM2    | 79 |
| NQ2    | 58 |
| GC1    | 63 |
| ES2    | 60 |
| NG1    | 50 |
| YM1    | 35 |
| CL1    | 36 |
| ES1    | 29 |
| NQ1    | 23 |
| RTY1/2 | 0 |

## Example

For **ES1 on 2018-07-19 at 09:00**:
- **Analyzer**: Result=BE (Break-Even), Session=S1, Time=09:00
- **Matrix**: Result=NoTrade, Session=S1, Time=09:00

The sequencer chose 09:00 for that day but `select_trade_for_time` returned no row, so the matrix recorded NoTrade even though the analyzer had a trade.

## Likely Causes

### 1. **Stream filters (exclude_times)**
If a stream has `exclude_times` configured (e.g. ES1 excluding 07:30), those slots are removed from `date_df` before selection. If 09:00 were excluded, the analyzer’s 09:00 trade would be filtered out.

**Check**: In the timetable app, open stream filters and confirm which times are excluded for each stream.

### 2. **Session/Time format mismatch**
`select_trade_for_time` matches on `(Time_str, Session)`. Mismatches can occur if:
- Session is stored as `"s1"` vs `"S1"`
- Time is stored as `"9:00"` vs `"09:00"`

**Fix applied**: Session comparison now normalizes (strip + uppercase). Time is already normalized via `normalize_time()`.

### 3. **Stale matrix vs current analyzer data**
The matrix may have been built from older analyzer output. After running the merger and rebuild, use **Reload** (or `nocache=true`) so the UI loads the latest matrix file.

### 4. **Invalid trade_date (rows dropped)**
If `trade_date` is invalid or NaT, rows are dropped during the build (see `logs/MASTER_MATRIX_ERRORS.md`). That can remove valid trades.

**Check**: `grep "invalid trade_date" logs/master_matrix.log`

### 5. **Data pipeline**
Analyzer → Merger → `data/analyzed/` → DataLoader → Sequencer. A failure in any step can drop rows.

## Run the Diagnostic

```bash
# All streams
python scripts/diagnose_no_trade_discrepancy.py

# Single stream with verbose sample
python scripts/diagnose_no_trade_discrepancy.py --stream ES1 -v

# Limit output
python scripts/diagnose_no_trade_discrepancy.py --limit 10
```

## Recommended Actions

1. **Confirm stream filters**  
   Ensure `exclude_times` does not remove slots that should be traded.

2. **Rebuild and reload**  
   Run merger (if needed), rebuild the matrix, then use Reload so the latest file is used.

3. **Inspect logs**  
   Search for `invalid trade_date`, `Filtered out`, and `exclude_times` in `logs/master_matrix.log`.

4. **Compare analyzer vs matrix input**  
   For a sample discrepancy (e.g. ES1 2018-07-19 09:00), verify the row exists in `data/analyzed/ES1/` parquet files and that Session/Time match expectations.
