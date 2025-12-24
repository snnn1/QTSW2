# Data Invention Analysis - Master Matrix

This document analyzes where and what data is being "invented" (created/synthesized) versus selected from real analyzer output in the master matrix.

## Summary

**Yes, there is some data being invented**, but it's primarily for two purposes:
1. **NoTrade records** - When the sequencer selects a time slot but no trade exists
2. **Derived/calculated columns** - Computed values based on real data
3. **Missing column defaults** - Schema normalization fills missing columns with defaults

---

## 1. NoTrade Records (Synthesized Data) ⚠️

**Location**: `modules/matrix/sequencer_logic.py` lines 314-322

When the sequencer chooses a time slot but no trade exists at that slot, a `NoTrade` record is created:

```python
# NoTrade: sequencer chose current_time but no trade exists at that slot
trade_dict = {
    'Stream': stream_id,
    'Date': date,
    'Time': str(current_time).strip(),  # Still set Time to current_time (sequencer's intent)
    'Result': 'NoTrade',
    'Session': current_session,
}
```

**What's invented here**:
- The entire trade record is synthesized
- Only basic fields are set: Stream, Date, Time, Result='NoTrade', Session
- All other fields (Profit, Target, Range, etc.) will be filled by schema normalization (see below)

**Why this exists**:
- The sequencer processes every calendar day
- Even when no trade exists, the sequencer still needs to record its decision
- This is necessary for the rolling history calculations to work correctly

---

## 2. Derived/Calculated Columns (Computed from Real Data) ✅

### Rolling Sum Columns
**Location**: `modules/matrix/sequencer_logic.py` lines 324-329

For ALL canonical times (even if not selected), rolling sum columns are added:

```python
for canonical_time in canonical_times:
    canonical_time_normalized = normalize_time(str(canonical_time))
    rolling_sum = sum(time_slot_histories.get(canonical_time_normalized, []))
    trade_dict[f"{canonical_time} Rolling"] = round(rolling_sum, 2)
    trade_dict[f"{canonical_time} Points"] = daily_scores.get(canonical_time_normalized, 0)
```

**What's computed**:
- Rolling sums are calculated from actual trade results (WIN/LOSS/BE/NoTrade scores)
- These are computed values based on real historical data
- **Not invented** - derived from actual results

### SL (Stop Loss) Column
**Location**: `modules/matrix/sequencer_logic.py` lines 331-337

```python
sl_value = 0
if trade_dict.get('Target') != 'NO DATA' and isinstance(trade_dict.get('Target'), (int, float)):
    sl_value = 3 * trade_dict['Target']
    if trade_dict.get('Range') != 'NO DATA' and isinstance(trade_dict.get('Range'), (int, float)) and trade_dict.get('Range', 0) > 0:
        sl_value = min(sl_value, trade_dict['Range'])
trade_dict['SL'] = sl_value
```

**What's computed**:
- Calculated from Target (3x Target, capped at Range)
- Uses real data from the trade if available
- Defaults to 0 if Target is missing

### Time Change Column
**Location**: `modules/matrix/sequencer_logic.py` lines 339-345

```python
time_change_display = ''
if previous_time is not None and str(old_time_for_today) != str(previous_time):
    time_change_display = f"{previous_time}→{old_time_for_today}"
elif next_time is not None:
    time_change_display = f"{old_time_for_today}→{next_time}"
trade_dict['Time Change'] = time_change_display
```

**What's computed**:
- Shows time slot changes (e.g., "07:30→08:00")
- Based on sequencer's time change decisions
- **Not invented** - reflects actual sequencer state transitions

---

## 3. Schema Normalization (Missing Column Defaults) ⚠️

**Location**: `modules/matrix/schema_normalizer.py` lines 64-82

When columns are missing, defaults are filled in:

```python
# Add missing required columns
for col, dtype in required_columns.items():
    if col not in df.columns:
        if col == 'Date':
            df[col] = pd.NaT
        elif dtype == 'float64':
            df[col] = np.nan
        elif dtype == 'object':
            df[col] = ''  # Empty string
        else:
            df[col] = None

# Add missing optional columns with NaN
for col, dtype in optional_columns.items():
    if col not in df.columns:
        df[col] = np.nan
```

**What's filled**:
- Missing float columns → `NaN`
- Missing object columns → `''` (empty string)
- Missing Date columns → `NaT` (Not a Time)

**Impact**:
- **NoTrade records** will have most fields as NaN/empty/default values
- Real trades should have all fields from analyzer output, but missing columns get defaults

### Derived Columns Created
**Location**: `modules/matrix/schema_normalizer.py` lines 89-145

Several columns are derived/created:

```python
# entry_time, exit_time (using Time as entry_time)
df['entry_time'] = df['Time']  # Copy of Time
df['exit_time'] = df['Time']   # Copy of Time (placeholder)

# entry_price, exit_price (not in analyzer output, create placeholder)
df['entry_price'] = np.nan     # ⚠️ Always NaN - not from real data
df['exit_price'] = np.nan      # ⚠️ Always NaN - not from real data

# R (Risk-Reward ratio) - calculate from Profit/Target
df['R'] = df.apply(
    lambda row: row['Profit'] / row['Target'] if pd.notna(row['Target']) and row['Target'] != 0 else np.nan,
    axis=1
)  # ✅ Calculated from real data

# pnl (same as Profit for now)
df['pnl'] = df['Profit']  # ✅ Copy of real data

# rs_value (Rolling Sum value)
df['rs_value'] = np.nan  # ⚠️ Always NaN - not implemented

# selected_time (same as Time for now)
df['selected_time'] = df['Time']  # ✅ Copy of real data

# time_bucket (same as Time for now)
df['time_bucket'] = df['Time']  # ✅ Copy of real data

# trade_date (same as Date)
df['trade_date'] = pd.to_datetime(df['Date'], errors='coerce')  # ✅ Derived from real data
```

**What's invented**:
- `entry_price` - Always NaN (not from real data)
- `exit_price` - Always NaN (not from real data)
- `rs_value` - Always NaN (not implemented)

**What's derived from real data**:
- `R` - Calculated from Profit/Target
- `pnl` - Copy of Profit
- `entry_time`, `exit_time`, `selected_time`, `time_bucket` - Copies of Time
- `trade_date` - Derived from Date

---

## 4. Time Column Overwriting ⚠️

**Location**: `modules/matrix/sequencer_logic.py` line 313

```python
# CRITICAL: Time column is sequencer's authority - overwrite analyzer's Time
# This ensures Time always reflects sequencer's intended slot, not trade's original time
trade_dict['Time'] = str(current_time).strip()
```

**What's happening**:
- The analyzer's original Time is **overwritten** with the sequencer's chosen time
- This is intentional - Time represents "sequencer's intended slot", not the trade's actual time

**Note**: The original analyzer time is lost. If you need the original time, it would need to be preserved as a separate column (e.g., `original_time` or `analyzer_time`).

---

## Summary by Category

| Category | Type | Status | Impact |
|----------|------|--------|--------|
| **NoTrade records** | Synthesized | ⚠️ Full record invented | Creates records for days with no trades |
| **Rolling columns** | Calculated | ✅ Derived from real data | Computed from actual results |
| **SL column** | Calculated | ✅ Derived from real data | Based on Target/Range |
| **Time Change** | Calculated | ✅ Reflects sequencer state | Shows time transitions |
| **entry_price** | Missing | ⚠️ Always NaN | Not from real data |
| **exit_price** | Missing | ⚠️ Always NaN | Not from real data |
| **rs_value** | Missing | ⚠️ Always NaN | Not implemented |
| **R ratio** | Calculated | ✅ Derived from real data | Profit/Target |
| **Time column** | Overwritten | ⚠️ Original lost | Replaced with sequencer intent |
| **Missing columns** | Defaults | ⚠️ Filled with NaN/empty | Schema normalization |

---

## Recommendations

### Data Integrity Concerns

1. **NoTrade Records**: 
   - These are necessary for sequencer logic
   - Fields like Profit, Target, Range will be NaN/empty for NoTrade records
   - **This is expected behavior** - NoTrade means no trade occurred

2. **Price Columns**:
   - `entry_price` and `exit_price` are always NaN
   - These fields don't exist in analyzer output
   - If needed, they would need to be added to analyzer output first

3. **Time Column Overwriting**:
   - Original analyzer Time is lost
   - If original time is needed, preserve it as `analyzer_time` or `original_time` before overwriting

4. **Missing Fields in NoTrade**:
   - NoTrade records will have NaN for Profit, Target, Range, etc.
   - Filter by `Result != 'NoTrade'` to get only real trades

### To Preserve Original Time (if needed):

Add this before line 313 in `sequencer_logic.py`:
```python
# Preserve original analyzer time if it exists
if 'Time' in trade_dict and 'analyzer_time' not in trade_dict:
    trade_dict['analyzer_time'] = trade_dict.get('Time', '')
trade_dict['Time'] = str(current_time).strip()
```

---

## Conclusion

**Yes, data is being invented**, but primarily for:
1. **NoTrade records** - Necessary for sequencer logic to work correctly
2. **Placeholder columns** - `entry_price`, `exit_price`, `rs_value` are always NaN
3. **Schema defaults** - Missing columns filled with NaN/empty strings

**Real trade data** (when `Result != 'NoTrade'`) comes from analyzer output and is not invented, except:
- Time column is overwritten with sequencer's chosen time
- Some columns are calculated from real data (R, rolling sums, SL)

