# NoTrade Rows: Analyzer vs Sequencer

## Important Distinction

There are **TWO different scenarios** where "NoTrade" appears:

---

## 1. NoTrade Rows FROM Analyzer (Real Data) ✅

**Location**: Analyzer output in `data/analyzed/` folders

**When created**:
- When `write_no_trade_rows=True` in analyzer configuration
- When no entry signal is detected at a time slot
- Written to the parquet files in analyzed folders

**What the analyzer creates**:
```python
# From breakout_core/engine.py lines 275-288
no_trade_row = {
    "Date": R.date.date().isoformat(),
    "Time": time_label,  # e.g., "07:30"
    "Target": target_pts,  # Real target value
    "Peak": 0.0,
    "Direction": "NA",
    "Result": "NoTrade",
    "Range": R.range_size,  # Real range value
    "Stream": stream,
    "Instrument": inst.upper(),
    "Session": sess,
    "Profit": 0.0,
}
```

**Key points**:
- These are **real rows in the analyzer output files**
- They have Target, Range, and other fields populated
- The sequencer can **find and use** these rows

---

## 2. NoTrade Rows CREATED BY Sequencer (Synthesized Data) ⚠️

**Location**: `modules/matrix/sequencer_logic.py` lines 314-322

**When created**:
- When sequencer selects a time slot (`current_time`)
- But **no row exists** at that time in `date_df` (the day's data)
- This happens in two cases:
  1. Analyzer didn't create a NoTrade row (`write_no_trade_rows=False`)
  2. Sequencer chooses a time slot that has no data for that day

**What the sequencer creates**:
```python
# From sequencer_logic.py lines 316-322
trade_dict = {
    'Stream': stream_id,
    'Date': date,
    'Time': str(current_time).strip(),  # Sequencer's chosen time
    'Result': 'NoTrade',
    'Session': current_session,
    # NOTE: Target, Range, Profit, etc. will be NaN/empty
    # These get filled by schema_normalizer with defaults
}
```

**Key points**:
- These are **synthesized/invented** by the sequencer
- Only basic fields are set (Stream, Date, Time, Result, Session)
- Target, Range, Profit, etc. will be NaN/empty/default values
- Created because sequencer processes EVERY calendar day, even when no data exists

---

## How Sequencer Handles Analyzer NoTrade Rows

**Location**: `modules/matrix/sequencer_logic.py` lines 238-243

```python
# Determine result
if date_df.empty:
    result = 'NoTrade'
else:
    slot_trade = date_df[date_df['Time_str'] == canonical_time_normalized]
    result = slot_trade.iloc[0]['Result'] if not slot_trade.empty else 'NoTrade'
```

**For scoring/history**:
- If analyzer NoTrade row exists → uses it (result = 'NoTrade')
- If no row exists → treats as 'NoTrade' for scoring

**For trade selection** (lines 307-322):
```python
trade_row = select_trade_for_time(date_df, current_time, current_session)

if trade_row is not None:
    # Found a row (could be Win/Loss/BE/NoTrade from analyzer)
    trade_dict = trade_row.to_dict()
    trade_dict['Time'] = str(current_time).strip()
else:
    # NO row found - create synthesized NoTrade
    trade_dict = {
        'Stream': stream_id,
        'Date': date,
        'Time': str(current_time).strip(),
        'Result': 'NoTrade',
        'Session': current_session,
    }
```

**Key distinction**:
- `trade_row is not None` → Found analyzer row (real data, could be NoTrade from analyzer)
- `trade_row is None` → No row found → Create synthesized NoTrade

---

## Summary Table

| Scenario | Source | Has Analyzer Data? | Target/Range Values | Status |
|----------|--------|-------------------|-------------------|--------|
| Analyzer NoTrade row exists | Analyzer output | ✅ Yes (NoTrade row) | ✅ Real values | Real data |
| No row at sequencer's time | Sequencer creates | ❌ No row | ❌ NaN/empty | Invented |
| Analyzer row exists (Win/Loss/BE) | Analyzer output | ✅ Yes | ✅ Real values | Real data |

---

## How to Tell the Difference

**In the master matrix output**:

1. **Analyzer NoTrade** (real data):
   - Has Target value (from analyzer)
   - Has Range value (from analyzer)
   - Profit = 0.0
   - Result = 'NoTrade'

2. **Sequencer NoTrade** (invented):
   - Target = NaN or empty
   - Range = NaN or empty
   - Profit = NaN or 0.0 (depending on schema normalization)
   - Result = 'NoTrade'

**Query to find only real trades** (including analyzer NoTrades):
```python
# Real trades (excludes only synthesized NoTrades with missing Target/Range)
df[df['Target'].notna() & df['Range'].notna()]
```

**Query to find only analyzer NoTrades**:
```python
df[(df['Result'] == 'NoTrade') & df['Target'].notna() & df['Range'].notna()]
```

**Query to find synthesized NoTrades**:
```python
df[(df['Result'] == 'NoTrade') & (df['Target'].isna() | df['Range'].isna())]
```

---

## Configuration Impact

**If `write_no_trade_rows=True`** (analyzer default):
- Analyzer creates NoTrade rows for time slots with no entry
- Sequencer will find and use these rows
- Less "invented" data in master matrix

**If `write_no_trade_rows=False`**:
- Analyzer doesn't create NoTrade rows
- Sequencer will create synthesized NoTrade rows when needed
- More "invented" data in master matrix

---

## Recommendation

The sequencer **DOES use** analyzer NoTrade rows when they exist. The "invented" NoTrade rows are only created when:
1. The analyzer didn't create a NoTrade row for that time slot, OR
2. The sequencer chooses a time slot that has no data

This is expected behavior - the sequencer needs to track its decision for every calendar day, even when no trade data exists.

