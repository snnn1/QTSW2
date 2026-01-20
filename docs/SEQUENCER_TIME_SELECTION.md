# How the Matrix Picks Times for Streams

## Overview

The sequencer uses a **points-based rolling sum system** to automatically select the best time slot for each stream. It tracks performance across all time slots and switches to better-performing times when the current time loses.

## The Scoring System

Each trade result is converted to points:

- **WIN**: +1 point
- **LOSS**: -2 points  
- **BE (Break-Even)**: 0 points
- **NoTrade**: 0 points
- **TIME**: 0 points

## Rolling Sum (RS) Calculation

For each time slot, the sequencer maintains a **rolling history** of the last 13 trades:

1. **All canonical times are scored every day** - Even if a trade didn't execute at that time, the sequencer records the result (NoTrade = 0 points)
2. **Rolling window**: Only the last 13 trades are kept in the history
3. **Rolling Sum**: Sum of all points in the rolling history

Example:
- Time slot "08:00" has history: `[1, -2, 1, 0, 1, -2, 1, 0, 1, 1, -2, 0, 1]`
- Rolling Sum = 1 + (-2) + 1 + 0 + 1 + (-2) + 1 + 0 + 1 + 1 + (-2) + 0 + 1 = **1**

## Time Selection Process

### Initial Selection

When a stream starts, it begins with the **first selectable time slot**:
- S1 streams: "07:30" (first in `['07:30', '08:00', '09:00']`)
- S2 streams: "09:30" (first in `['09:30', '10:00', '10:30', '11:00']`)

**Note**: Filtered times (from `exclude_times`) are excluded from selection, so if "07:30" is filtered, it starts with "08:00".

### Daily Processing

For each trading day:

1. **Score all time slots**: The sequencer evaluates ALL canonical time slots for that day:
   - If a trade exists at that time → use its result (WIN/LOSS/BE/etc.)
   - If no trade exists → record NoTrade (0 points)

2. **Update rolling histories**: Add today's score to each time slot's rolling history (maintaining 13-trade window)

3. **Check for time change**: Only happens if:
   - Current time slot had a **LOSS** today
   - AND another selectable time slot has a **higher rolling sum**

4. **Switch logic**: If conditions are met:
   - Compare current time's rolling sum (after today's loss) vs. all other selectable times
   - Switch to the time slot with the **highest rolling sum**
   - If tied, choose the **earliest time** (tie-breaker)

### Example: Time Change Decision

**Current state:**
- Current time: "07:30"
- Today's result: LOSS (-2 points)
- "07:30" rolling sum after loss: -1
- "08:00" rolling sum: +3
- "09:00" rolling sum: +1

**Decision**: Switch to "08:00" because:
- Current time lost (trigger condition met)
- "08:00" has highest rolling sum (+3 > -1)
- "08:00" is selectable (not filtered)

**Result**: Next day, stream uses "08:00" instead of "07:30"

## Filtered Times Behavior

**Critical**: Filtered times (from `exclude_times`) are handled specially:

### What Happens When a Time is Filtered

1. **Still Scored Every Day**
   - The sequencer evaluates ALL canonical times daily, including filtered ones
   - If a trade exists at a filtered time → it gets scored normally (WIN=+1, LOSS=-2, etc.)
   - If no trade exists → it gets NoTrade (0 points)
   - **Why**: Maintains accurate historical data for if/when the filter is removed

2. **Rolling Histories Continue Updating**
   - Filtered times maintain their rolling 13-trade history
   - Scores are added to their history just like non-filtered times
   - **Why**: If a time is later unfiltered, it has up-to-date performance data

3. **Never Selected as Current Time**
   - Filtered times are excluded from `selectable_times`
   - Cannot be chosen as `current_time` during initialization
   - Cannot be switched to during time change decisions
   - **Why**: Prevents the sequencer from trading at excluded times

4. **Trades at Filtered Times are Ignored**
   - When selecting which trade to use, trades at filtered times are removed from consideration
   - Even if analyzer data exists at a filtered time, it won't be selected
   - **Why**: Ensures no execution happens at excluded times

5. **Cannot Switch Into Filtered Time**
   - If `current_time` becomes filtered (filter added while stream is running), it's an error condition
   - The sequencer validates that `current_time` is always in `selectable_times`
   - **Why**: Maintains invariant that sequencer never uses filtered times

### Example: Filtering "07:30"

**Before filtering:**
- ES1 is using "07:30" (current_time)
- All times selectable: ["07:30", "08:00", "09:00"]
- Rolling sums: 07:30=+2, 08:00=+1, 09:00=0

**After filtering "07:30":**
- ES1 must switch to "08:00" (first selectable time)
- Selectable times: ["08:00", "09:00"] (07:30 removed)
- "07:30" still scored daily (NoTrade = 0 points)
- "07:30" rolling history continues: [+2, 0, 0, ...]
- "07:30" cannot be selected again until filter is removed

**If filter is later removed:**
- "07:30" becomes selectable again
- Its rolling history is intact: [+2, 0, 0, ...]
- Can be switched to if it has the highest rolling sum

### Key Invariants

- **All canonical times are scored** - Even filtered ones
- **Only selectable times can be current_time** - Filtered times excluded
- **Sequencer never switches into filtered time** - Validation ensures this
- **Filtered times maintain history** - For potential future use

## Time Column in Matrix

The `Time` column in the master matrix represents:
- **Sequencer's intended trading slot** for that day
- NOT the analyzer's original trade time
- Always reflects what the sequencer selected

If a time change occurred, the `Time Change` column shows:
- The new time slot (e.g., "08:00")
- This is the time the sequencer switched TO

## Key Files

- **`modules/matrix/sequencer_logic.py`**: Core sequencer logic
  - `process_stream_daily()`: Main processing loop
  - `decide_time_change()`: Time change decision logic
  - `calculate_time_score()`: Converts results to points

- **`modules/matrix/utils.py`**: 
  - `calculate_time_score()`: Scoring function

- **`modules/timetable/timetable_engine.py`**:
  - `calculate_rs_for_stream()`: Calculates rolling sums for timetable
  - `select_best_time()`: Selects best time based on RS

## Example Timeline

**Day 1**: ES1 starts at "07:30"
- Trade at 07:30: WIN (+1)
- Rolling sums: 07:30=+1, 08:00=0, 09:00=0
- **No change** (no loss)

**Day 2**: ES1 still at "07:30"
- Trade at 07:30: LOSS (-2)
- Rolling sums: 07:30=-1, 08:00=+2, 09:00=0
- **Switch to "08:00"** (loss + better alternative)

**Day 3**: ES1 now at "08:00"
- Trade at 08:00: WIN (+1)
- Rolling sums: 07:30=-1, 08:00=+3, 09:00=0
- **No change** (no loss)

**Day 4**: ES1 still at "08:00"
- Trade at 08:00: WIN (+1)
- Rolling sums: 07:30=-1, 08:00=+4, 09:00=0
- **No change** (no loss)

## Summary

The sequencer picks times by:
1. **Tracking performance** of all time slots using a rolling 13-trade window
2. **Scoring each trade** (WIN=+1, LOSS=-2, others=0)
3. **Switching only on loss** to the time slot with the highest rolling sum
4. **Respecting filters** by excluding filtered times from selection (but still tracking them)
5. **Maintaining state** day-to-day so decisions are consistent

This creates an **adaptive system** that automatically moves to better-performing time slots when the current one starts losing.
