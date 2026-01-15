# State Machine Flow Explanation

## Correct State Sequence

The state machine flows in this order:

1. **PRE_HYDRATION** → Loads historical bar data from CSV files
2. **ARMED** → Waiting for `RangeStartUtc` (the time when range building should begin)
3. **RANGE_BUILDING** → Actively building the range from `RangeStartUtc` to `SlotTimeUtc`
4. **RANGE_LOCKED** → Range is locked at `SlotTimeUtc`, ready for trading
5. **DONE** → Slot is complete

## Key Transition Points

### PRE_HYDRATION → ARMED
- **Condition**: `_preHydrationComplete == true`
- **When**: After historical data is loaded from CSV files
- **Code**: Line 368-370 in `StreamStateMachine.cs`

### ARMED → RANGE_BUILDING
- **Condition**: `utcNow >= RangeStartUtc`
- **When**: Current UTC time reaches the range start time
- **Code**: Line 419-431 in `StreamStateMachine.cs`
- **What happens**: 
  - Logs `RANGE_WINDOW_STARTED`
  - Transitions to `RANGE_BUILDING`
  - Computes initial range from available bars

### RANGE_BUILDING → RANGE_LOCKED
- **Condition**: `utcNow >= SlotTimeUtc`
- **When**: Current UTC time reaches the slot time
- **Code**: Line 596-814 in `StreamStateMachine.cs`
- **What happens**:
  - Computes final range (if not already computed incrementally)
  - Locks the range
  - Transitions to `RANGE_LOCKED`

## What Happened on 2026-01-14 for 09:00 Slot

For the **09:00 slot (S1)**:
- **RangeStartUtc**: Should be Chicago 07:00 = UTC 13:00 (2 hours before slot time)
- **SlotTimeUtc**: Chicago 09:00 = UTC 15:00

**The Problem**: Streams were stuck in `ARMED` state and never transitioned to `RANGE_BUILDING` because:
- The condition `utcNow >= RangeStartUtc` was never met, OR
- `RangeStartUtc` was calculated incorrectly

## Why Range Building Starts AFTER ARMED

Range building starts **AFTER** ARMED, not before. The sequence is:
1. Stream starts in `PRE_HYDRATION`
2. After pre-hydration completes → transitions to `ARMED`
3. While in `ARMED`, the stream waits for `RangeStartUtc`
4. When `utcNow >= RangeStartUtc` → transitions to `RANGE_BUILDING`
5. Range building continues until `SlotTimeUtc`
6. At `SlotTimeUtc` → transitions to `RANGE_LOCKED`

So `ARMED` is the **waiting state** before range building begins. Range building cannot start until the stream is `ARMED` and `RangeStartUtc` has been reached.
