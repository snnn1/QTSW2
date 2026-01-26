# Sequencer Logic - Complete Summary

## Overview

The **Sequencer Logic** is the single authoritative source for all sequencing decisions in the Master Matrix system. It selects one optimal trade per day per stream using a sophisticated points-based system with loss-triggered time changes.

**Key Principle**: The sequencer owns the `Time` column, which represents "the sequencer's intended trading slot for that day." This is NOT the analyzer's original time - it's the sequencer's decision based on performance history.

---

## Core Purpose

The sequencer logic:
1. **Selects one trade per day per stream** from multiple available trades
2. **Maintains rolling performance histories** for all time slots
3. **Automatically switches time slots** when losses occur, choosing better-performing alternatives
4. **Ensures deterministic, reproducible results** across rebuilds

---

## Key Concepts

### 1. Time Sets

**Canonical Times**: The complete fixed set of time slots for a session
- **S1 (Session 1)**: `["07:30", "08:00", "09:00"]`
- **S2 (Session 2)**: `["09:30", "10:00", "10:30", "11:00"]`

**Selectable Times**: Canonical times minus any filtered times
- Filtered times are excluded from selection but still scored
- Ensures sequencer never switches into a filtered time

**Critical Invariant**: `current_time` must always be in `selectable_times`

### 2. Scoring System

Each trade result is converted to a point score:

| Result | Score |
|--------|-------|
| **WIN** | +1 point |
| **LOSS** | -2 points |
| **BE/Break-Even** | 0 points |
| **NoTrade** | 0 points |
| **TIME** | 0 points |

**Why LOSS = -2?** Losses are penalized more heavily than wins are rewarded, creating a bias toward switching away from losing time slots.

### 3. Rolling Histories

Each time slot maintains a **rolling window of the last 13 scores** (configurable via `ROLLING_WINDOW_SIZE`).

**Example History**:
```
"08:00": [1, -2, 1, 0, 1, -2, 1, 1, 0, -2, 1, 1, 1]  # Last 13 scores
Rolling Sum: 1 + (-2) + 1 + 0 + 1 + (-2) + 1 + 1 + 0 + (-2) + 1 + 1 + 1 = 1
```

**Key Properties**:
- All canonical times advance their history every day (even filtered ones)
- Histories are updated BEFORE time change decisions
- Histories maintain consistent length (13 scores) via rolling window

### 4. Time Change Logic

**Time changes ONLY occur after a LOSS** at the current time slot.

**Decision Process**:
1. Check if current trade result is `LOSS`
2. If not LOSS → no change (return `None`)
3. If LOSS → evaluate other selectable times in the same session
4. Calculate rolling sums for all other selectable times
5. Find the time with the highest rolling sum
6. Switch ONLY if the best other time's sum is **strictly greater** than current time's sum
7. Tie-break: If sums are equal, choose the earliest time

**Example**:
```
Current time: "08:00"
Current result: LOSS
Current rolling sum: 1

Other selectable times:
- "07:30": rolling sum = 3
- "09:00": rolling sum = 2

Decision: Switch to "07:30" (highest sum: 3 > 1)
```

---

## Processing Flow

### Step 1: Initialization

For each stream:

1. **Determine Session**: S1 or S2 (from data or default to S1)
2. **Define Canonical Times**: All time slots for that session
3. **Calculate Selectable Times**: Canonical times minus filtered times
4. **Initialize Current Time**: First selectable time (or restored from checkpoint)
5. **Initialize Rolling Histories**: Empty lists for all canonical times (or restored from checkpoint)

### Step 2: Daily Processing Loop

For each trading day (in chronological order):

#### 2.1 Centralized Daily Scoring

**Score ALL canonical times** (even filtered ones):

```python
for canonical_time in canonical_times:
    # Find trade at this time slot (if exists)
    result = trade_result_at_time(canonical_time)
    
    # Calculate score
    score = calculate_time_score(result)  # WIN=1, LOSS=-2, BE=0, NoTrade=0
    
    # Update rolling history
    update_time_slot_history(time_slot_histories, canonical_time, score)
```

**Key Point**: All times are scored every day, maintaining complete histories for comparison.

#### 2.2 Time Change Decision

**Pure function** that evaluates whether to switch:

```python
next_time = decide_time_change(
    current_time="08:00",
    current_result="LOSS",
    current_sum_after=1,  # Rolling sum after today's score
    time_slot_histories={...},  # All histories (already updated)
    selectable_times=["07:30", "08:00", "09:00"],  # Excludes filtered
    current_session="S1"
)
```

**Returns**:
- `None` if no change needed
- `"07:30"` (or other time) if switch is needed

#### 2.3 Trade Selection

**Select trade at current_time**:

```python
# Filter out excluded times from available trades
date_df_filtered = date_df[~date_df['Time_str'].isin(filtered_times)]

# Select trade at current_time ONLY
trade_row = select_trade_for_time(
    date_df_filtered,
    current_time="08:00",
    current_session="S1"
)
```

**Returns**:
- Trade row if found at `current_time`
- `None` if no trade exists (recorded as `NoTrade`)

#### 2.4 Build Trade Dictionary

Create the output trade record:

```python
trade_dict = {
    'Stream': 'ES1',
    'Date': '2026-01-23',
    'trade_date': datetime(2026, 1, 23),
    'Time': '08:00',  # Sequencer's intended slot (overwrites analyzer's Time)
    'actual_trade_time': '08:00',  # Original analyzer time (preserved)
    'Result': 'WIN',
    'Profit': 50.0,
    # ... other trade fields ...
    
    # Rolling columns for ALL canonical times
    '07:30 Rolling': 3.0,  # Sum of last 13 scores
    '08:00 Rolling': 1.0,
    '09:00 Rolling': 2.0,
    '07:30 Points': 1,  # Today's score
    '08:00 Points': 1,
    '09:00 Points': 0,
    
    # Time Change (future change only)
    'Time Change': '07:30'  # Only if next_time != current_time, else ''
}
```

**Critical**: The `Time` column is **overwritten** with `current_time`, ensuring it always reflects sequencer intent, not the analyzer's original time.

#### 2.5 Update State

**At end of loop** (EXACTLY ONCE):

```python
if next_time is not None:
    current_time = next_time  # Switch to new time
    current_session = get_session_for_time(next_time)

previous_time = old_time_for_today  # Track for TimeChange display
```

---

## Example Timeline

### Day 1 (2026-01-20)
- **Current Time**: `"08:00"` (initialized to first selectable)
- **Trade Selected**: Trade at 08:00, Result = WIN
- **Scores**: 08:00 = +1, 07:30 = 0 (NoTrade), 09:00 = 0 (NoTrade)
- **Histories**: `{"08:00": [1], "07:30": [0], "09:00": [0]}`
- **Rolling Sums**: 08:00 = 1, 07:30 = 0, 09:00 = 0
- **Time Change Decision**: No (result = WIN, not LOSS)
- **Next Time**: `None` → stays at `"08:00"`
- **TimeChange Column**: `""` (empty, no change scheduled)

### Day 2 (2026-01-21)
- **Current Time**: `"08:00"` (unchanged)
- **Trade Selected**: Trade at 08:00, Result = WIN
- **Scores**: 08:00 = +1, 07:30 = 0, 09:00 = 0
- **Histories**: `{"08:00": [1, 1], "07:30": [0, 0], "09:00": [0, 0]}`
- **Rolling Sums**: 08:00 = 2, 07:30 = 0, 09:00 = 0
- **Time Change Decision**: No (result = WIN)
- **Next Time**: `None` → stays at `"08:00"`
- **TimeChange Column**: `""`

### Day 3 (2026-01-22)
- **Current Time**: `"08:00"`
- **Trade Selected**: Trade at 08:00, Result = LOSS
- **Scores**: 08:00 = -2, 07:30 = +1 (trade exists), 09:00 = 0
- **Histories**: `{"08:00": [1, 1, -2], "07:30": [0, 0, 1], "09:00": [0, 0, 0]}`
- **Rolling Sums**: 08:00 = 0, 07:30 = 1, 09:00 = 0
- **Time Change Decision**: Yes! (result = LOSS, 07:30 sum = 1 > 0)
- **Next Time**: `"07:30"` (best other time)
- **TimeChange Column**: `"07:30"` (shows future change)

### Day 4 (2026-01-23)
- **Current Time**: `"07:30"` (switched from 08:00)
- **Trade Selected**: Trade at 07:30, Result = WIN
- **Scores**: 07:30 = +1, 08:00 = 0, 09:00 = 0
- **Histories**: `{"07:30": [0, 0, 1, 1], "08:00": [1, 1, -2, 0], "09:00": [0, 0, 0, 0]}`
- **Rolling Sums**: 07:30 = 2, 08:00 = 0, 09:00 = 0
- **Time Change Decision**: No (result = WIN)
- **Next Time**: `None` → stays at `"07:30"`
- **TimeChange Column**: `""`

---

## Filtering and Selection

### Matrix Filtering

**Filtered times** (via `exclude_times` filter):
- Are **excluded from selection** (can't be chosen as `current_time`)
- Are **still scored** (histories still updated)
- Are **still in rolling columns** (for analysis)

**Example**:
```
Canonical times: ["07:30", "08:00", "09:00"]
Filtered times: ["07:30"]
Selectable times: ["08:00", "09:00"]  # 07:30 excluded

Current time can only be "08:00" or "09:00"
But "07:30" still gets scored and has rolling history
```

### Trade Selection

**Trade selection happens AFTER filtering**:
1. Filter out trades at excluded times from `date_df`
2. Select trade at `current_time` from filtered `date_df`
3. If no trade exists → `NoTrade` result

**No fallback logic**: If `current_time` has no trade, return `None` (recorded as `NoTrade`).

---

## State Management

### State Components

Each stream maintains state:

```python
state = {
    "current_time": "08:00",  # Current time slot
    "current_session": "S1",  # Current session
    "time_slot_histories": {
        "07:30": [1, -2, 1, 0, ...],  # Last 13 scores
        "08:00": [1, 1, -2, 1, ...],
        "09:00": [0, 0, 0, 1, ...]
    }
}
```

### Checkpointing

States can be saved and restored for:
- **Rolling resequence**: Restore state from checkpoint, then process forward
- **Partial rebuilds**: Resume from a specific date
- **State persistence**: Maintain consistency across rebuilds

---

## Invariants and Rules

### Hard Rules

1. **Time Column Ownership**: `Time` column is owned by sequencer, never mutated downstream
2. **Selectable Times Only**: `current_time` must always be in `selectable_times`
3. **Loss-Triggered Changes**: Time changes only occur after LOSS
4. **All Times Scored**: All canonical times are scored every day (even filtered)
5. **Strict Comparison**: Switch only if best other time's sum is **strictly greater** (not equal)

### Invariants

1. **History Length Consistency**: All time slot histories have the same length (enforced by invariant check)
2. **No Filtered Selection**: Sequencer never switches into a filtered time
3. **Deterministic**: Same input data → same output (no randomness)
4. **State Preservation**: State can be saved and restored exactly

---

## Output Columns

### Core Columns

- **`Time`**: Sequencer's intended trading slot (overwrites analyzer's Time)
- **`actual_trade_time`**: Original analyzer time (preserved for filtering)
- **`Time Change`**: Future time change (only shows when `next_time != current_time`)

### Rolling Columns

For each canonical time slot:
- **`{time} Rolling`**: Rolling sum of last 13 scores (e.g., `"08:00 Rolling" = 3.0`)
- **`{time} Points`**: Today's score for that time slot (e.g., `"08:00 Points" = 1`)

**Example**:
```
"07:30 Rolling": 2.0
"08:00 Rolling": 1.0
"09:00 Rolling": 0.0
"07:30 Points": 1
"08:00 Points": -2
"09:00 Points": 0
```

---

## Performance Optimizations

1. **Parallel Processing**: Streams processed in parallel (when `parallel=True`)
2. **Vectorized Operations**: Pre-normalize times and dates once (not per-day)
3. **Grouping-First Sort**: Sort by `[Stream, trade_date, entry_time]` for cache locality
4. **View vs Copy**: Use DataFrame views where possible (faster than copies)

---

## Error Handling

### Configuration Errors

- **All times filtered**: Raises `ValueError` with actionable message
- **Invalid restored state**: Falls back to default (first selectable time)

### Invariant Violations

- **History length mismatch**: Raises `AssertionError` (should never happen)
- **Non-selectable time**: Raises `AssertionError` (critical invariant)

---

## Summary

The sequencer logic is a **deterministic, stateful system** that:

1. **Scores all time slots** every day using a points system (WIN=+1, LOSS=-2)
2. **Maintains rolling histories** (last 13 scores) for all canonical times
3. **Selects trades** at the current time slot (one per day per stream)
4. **Switches time slots** only after losses, choosing the best-performing alternative
5. **Preserves state** for checkpointing and rolling resequences
6. **Respects filters** (excluded times can't be selected but are still scored)

The result is a **self-optimizing system** that automatically adapts to changing market conditions by switching to better-performing time slots when losses occur.
