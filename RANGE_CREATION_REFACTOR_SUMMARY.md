# Range Creation Refactor Summary

## BEFORE

### How the range was built
- **Incremental building**: Range was built incrementally as bars arrived via `OnBar()` calls
- **Live updates**: `RangeHigh` and `RangeLow` were updated in real-time as each bar was processed
- **State-dependent**: Range building occurred only when `State == RANGE_BUILDING` and `barUtc < SlotTimeUtc`
- **Grace period hack**: If range values were missing when slot time was reached, a 5-minute grace period allowed waiting for historical bars

### Why it was fragile
1. **Timing dependency**: Range building depended on stream creation timing relative to slot time
2. **Live bar availability**: Required bars to arrive in real-time during the range building window
3. **Late stream creation**: If streams were created after slot time, range values remained null
4. **Grace period workaround**: The 5-minute grace period was a band-aid that didn't solve the root cause
5. **Partial ranges**: System could proceed with incomplete ranges if grace period expired

### What assumptions it relied on
- Robot "watched the session live" - assumed bars would arrive in real-time during the range building window
- Stream creation timing - assumed streams would be created before slot time
- Historical bar availability - assumed historical bars would be available if streams were created late
- Incremental processing - assumed range could be built bar-by-bar as data arrived

## AFTER

### How the range is built now
- **Retrospective computation**: Range is computed in one shot after the session window ends (when `utcNow >= SlotTimeUtc`)
- **Single-pass algorithm**: All bars in `[RangeStartUtc, SlotTimeUtc)` are queried and processed in one pass
- **Atomic assignment**: Range values (`RangeHigh`, `RangeLow`, `FreezeClose`) are set atomically after computation completes
- **Bar buffering**: In live mode, bars are buffered as they arrive; in DRYRUN mode, bars are queried from `IBarProvider`
- **Deterministic**: Range computation is deterministic and independent of stream creation timing

### Why it is deterministic
1. **No timing dependency**: Range computation occurs when slot time is reached, regardless of when the stream was created
2. **Complete dataset**: All bars in the range window are queried/computed together, ensuring completeness
3. **Single computation**: Range is computed exactly once (guarded by `_rangeComputed` flag)
4. **Fail-closed**: If range data is unavailable, stream is marked `NO_TRADE` - no partial ranges allowed

### Why instant breakouts are safe
1. **Range guaranteed**: `RANGE_LOCKED` state guarantees `RangeHigh != null` and `RangeLow != null`
2. **Breakout levels computed**: Breakout levels are computed immediately after range is locked
3. **Entry detection enabled**: Entry detection (`can_detect_entries`) is enabled only after range and breakout levels are computed
4. **No race conditions**: Range computation completes before state transition to `RANGE_LOCKED`, ensuring entry detection has all required data

### What invariants are now guaranteed
1. **RANGE_LOCKED invariant**: `RangeHigh != null && RangeLow != null` - enforced by `ComputeRangeRetrospectively()` validation
2. **Entry detection invariant**: Entry detection never runs before range computation - enforced by state machine logic
3. **Data completeness invariant**: If full range window data is unavailable, stream is marked `NO_TRADE` - no partial ranges
4. **Single computation invariant**: Range is computed exactly once per stream per day - enforced by `_rangeComputed` flag
5. **Atomic assignment invariant**: Range values are set atomically after computation completes - no intermediate states

## Implementation Details

### Key Changes
1. **Bar Buffer**: Added `_barBuffer` (thread-safe list) to store bars as they arrive in live mode
2. **ComputeRangeRetrospectively()**: New method that queries bars and computes range in one pass
3. **OnBar() refactor**: Now only buffers bars; does not update range incrementally
4. **Tick() refactor**: Computes range retrospectively when slot time is reached
5. **Grace period removal**: Removed all grace period logic and `RANGE_BUILDING_WAITING_FOR_BARS` events
6. **RANGE_DATA_MISSING**: New event logged when range data is unavailable; stream marked `NO_TRADE`

### Logging Events
- `RANGE_COMPUTE_START`: Logged when range computation begins
- `RANGE_COMPUTE_COMPLETE`: Logged when range computation completes successfully (includes `range_high`, `range_low`, `bar_count`, `duration_ms`)
- `RANGE_DATA_MISSING`: Logged when range data is unavailable (stream marked `NO_TRADE`)

### Files Modified
- `modules/robot/core/StreamStateMachine.cs`
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs`
- `modules/robot/core/RobotEngine.cs` (updated constructor call)
- `RobotCore_For_NinjaTrader/RobotEngine.cs` (updated constructor call)

## Design Intent (Non-Negotiable)

**This system trades context, not time.**

If the robot did not see the entire session window, it must not trade.

- No guessing
- No reconstruction
- No partial ranges

The refactor enforces this principle by:
- Requiring complete range window data before proceeding
- Marking streams as `NO_TRADE` if data is unavailable
- Computing range atomically from a complete dataset
- Ensuring range values are guaranteed before entry detection begins
