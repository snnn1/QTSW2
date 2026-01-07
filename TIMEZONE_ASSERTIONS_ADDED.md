# Timezone Assertions Added

## Summary

Added three assertion-style diagnostic events to prove end-to-end that range formation occurs in the correct Chicago time window.

## Assertion Events

### 1. RANGE_INTENT_ASSERT (Pre-Slot)
**Purpose:** Prove that before any bars or slot logic, the system has constructed the correct Chicago range window.

**When:** Emitted once per stream per day when transitioning to ARMED state.

**Location:** `StreamStateMachine.Transition()` method, when `next == StreamState.ARMED`

**Fields:**
- `trading_date`
- `range_start_chicago` (ISO with offset)
- `slot_time_chicago` (ISO with offset)
- `range_start_utc` (ISO)
- `slot_time_utc` (ISO)
- `chicago_offset`
- `source`: "pre-slot assertion"

### 2. RANGE_FIRST_BAR_ACCEPTED (Window Entry)
**Purpose:** Prove that the first bar entering the range is accepted because of correct Chicago comparison.

**When:** Emitted once per stream per day when the first bar satisfies `barChicagoTime >= RangeStartChicagoTime`.

**Location:** `StreamStateMachine.OnBar()` method, when first bar enters range window

**Fields:**
- `bar_utc_time`
- `bar_chicago_time`
- `range_start_chicago`
- `comparison_result`: "bar >= range_start"
- `note`: "first accepted bar"

### 3. RANGE_LOCK_ASSERT (Post-Slot)
**Purpose:** Prove that the range locks once, using only bars inside the intended Chicago window.

**When:** Emitted once per stream per day immediately after range is finalized/locked.

**Location:** `StreamStateMachine.Tick()` method, right after `RANGE_COMPUTE_COMPLETE`

**Fields:**
- `trading_date`
- `range_start_chicago`
- `slot_time_chicago`
- `bars_used_count`
- `first_bar_chicago`
- `last_bar_chicago`
- `invariant_checks`:
  - `first_bar_ge_range_start`: boolean
  - `last_bar_lt_slot_time`: boolean
- `note`: "range locked"

## Implementation Details

### Assertion Flags
Added three flags to ensure each assertion fires exactly once per stream per day:
- `_rangeIntentAssertEmitted`
- `_firstBarAcceptedAssertEmitted`
- `_rangeLockAssertEmitted`

### Flag Reset
Flags are reset in `UpdateTradingDate()` when trading date changes, ensuring one assertion per day.

## Verification Criteria

After deployment, for any stream that reaches its slot:

1. ✅ **RANGE_INTENT_ASSERT** shows the correct Chicago window (e.g., `02:00-09:30 Chicago`)
2. ✅ **RANGE_FIRST_BAR_ACCEPTED** confirms correct Chicago boundary entry
3. ✅ **RANGE_LOCK_ASSERT** confirms the window was respected end-to-end

If all three fire correctly, timezone correctness is permanently proven.

## Files Modified

- `modules/robot/core/StreamStateMachine.cs`
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

## Post-Verification Cleanup

Once confirmed working:
- These events can be removed, or
- Downgraded to DEBUG, or
- Kept behind a diagnostic flag

But do not remove until verification is complete.
