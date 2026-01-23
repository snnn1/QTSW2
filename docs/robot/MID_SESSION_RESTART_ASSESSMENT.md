# Mid-Session Restart Assessment

**Last Updated**: 2026-01-22

## Overview

This document assesses what happens when NinjaTrader is restarted in the middle of a trading day. The robot implements a "Full Reconstruction" policy for mid-session restarts.

**Assessment Date**: 2026-01-22  
**Historical Restarts Found**: 129 mid-session restart events detected in logs

## Restart Detection

### When is a restart considered "mid-session"?

A restart is detected as mid-session when **all three** conditions are met:

1. **Journal exists** - Stream was initialized earlier today
2. **Journal not committed** - Stream was still active (not completed)
3. **Current time >= range_start** - Session has already begun

**Code Location**: `StreamStateMachine.cs` lines 228-242

```csharp
var existing = journals.TryLoad(tradingDateStr, Stream);
var isRestart = existing != null;
var isMidSessionRestart = false;

if (isRestart)
{
    var nowChicago = time.ConvertUtcToChicago(nowUtc);
    isMidSessionRestart = !existing.Committed && nowChicago >= RangeStartChicagoTime;
}
```

## Restart Policy: "Full Reconstruction"

### Policy Statement

**Current Policy**: "Restart = Full Reconstruction"

When a mid-session restart is detected:
- BarsRequest loads historical bars from `range_start` to `min(slot_time, now)`
- Range is recomputed from all available bars (historical + live)
- Stream resumes normal operation

**Trade-off**: Results may differ from uninterrupted operation if restart occurs after `slot_time`.

### Alternative Policy (Not Implemented)

**Alternative**: "Restart Invalidates Stream"
- Would mark stream as invalidated if restart occurs after `slot_time`
- Would prevent trading for that stream on that day
- **Not chosen** because it prevents recovery from crashes/restarts

**Code Location**: `StreamStateMachine.cs` lines 246-258

## What Happens During Restart

### 1. Stream Initialization

**Event Logged**: `MID_SESSION_RESTART_DETECTED`

**Event Payload**:
```json
{
  "trading_date": "2026-01-22",
  "previous_state": "RANGE_BUILDING",
  "previous_update_utc": "2026-01-22T18:30:00+00:00",
  "restart_time_chicago": "2026-01-22T17:30:00-06:00",
  "restart_time_utc": "2026-01-22T23:30:00+00:00",
  "range_start_chicago": "2026-01-22T02:00:00-06:00",
  "slot_time_chicago": "2026-01-22T22:00:00-06:00",
  "policy": "RESTART_FULL_RECONSTRUCTION",
  "note": "Mid-session restart detected - will reconstruct range from historical + live bars. Result may differ from uninterrupted operation."
}
```

### 2. Journal State Recovery

**Journal Fields Preserved**:
- `TradingDate` - Trading date (immutable)
- `Stream` - Stream identifier
- `Committed` - Whether stream completed
- `LastState` - Previous state (e.g., "RANGE_BUILDING", "RANGE_LOCKED")
- `LastUpdateUtc` - Last update timestamp
- `TimetableHashAtCommit` - Timetable hash (if committed)

**Journal Location**: `logs/robot/journal/{trading_date}_{stream}.json`

**Code Location**: `StreamStateMachine.cs` lines 277-286

### 3. State Machine Behavior

**If Journal Not Committed**:
- Stream resumes from previous state
- Range is recomputed from available bars
- Can continue to `RANGE_LOCKED` and trading

**If Journal Committed**:
- Stream goes directly to `DONE` state
- No further trading for that stream/day
- Prevents duplicate entries

**Code Location**: `StreamStateMachine.cs` lines 1914-1923

## Position Recovery (If Position Exists)

### Recovery Process

**Step 1: Account Snapshot**
- Get current positions from broker
- Get working orders from broker
- Event: `RECOVERY_ACCOUNT_SNAPSHOT`

**Step 2: Position Reconciliation**
- Match positions to streams using execution journal
- Event: `RECOVERY_POSITION_RECONCILED` (if matched)
- Event: `RECOVERY_POSITION_UNMATCHED` (if unmatched - requires operator intervention)

**Step 3: Order Cleanup**
- Cancel any orphaned robot orders (prefix: "QTSW2:")
- Event: `RECOVERY_CANCELLED_ROBOT_ORDERS`

**Step 4: Protective Order Restoration**
- Recreate stop/target orders if position exists
- Event: `RECOVERY_PROTECTIVE_ORDERS_PLACED`

**Step 5: Stream Stand-Down (If Unmatched)**
- If position cannot be matched to a stream
- Stream enters `DONE` state with `STREAM_STAND_DOWN`
- Blocks new entries for that instrument
- Still ensures protection (stop/target orders)

**Code Location**: `RobotEngine.cs` lines 2400-2500 (recovery logic)

## Range Reconstruction

### How Range is Rebuilt

**Process**:
1. BarsRequest loads historical bars from `range_start` to `min(slot_time, now)`
2. Live bars continue to arrive via `OnBar()`
3. Range is recomputed using `ComputeRangeRetrospectively()`
4. Uses all available bars (historical + live)

**Important**: Range may differ from uninterrupted operation if:
- Restart occurs after `slot_time`
- Bars were filtered differently before restart
- Data gaps exist

**Code Location**: `StreamStateMachine.cs` lines 1336-1418 (range initialization)

## Duplicate Prevention

### Journal-Based Prevention

**Prevents**:
- Double entries (if `entry_filled` flag set)
- Re-arming committed streams
- Duplicate range computation

**Journal Fields**:
- `Committed` - Stream completed (entry filled, market close, or forced flatten)
- `CommitReason` - Why stream was committed
- `LastState` - Last known state

**Code Location**: `StreamStateMachine.cs` lines 1880-1884, 1916-1919

### Execution Journal

**Additional Protection**:
- Tracks order IDs and execution IDs
- Matches positions to streams
- Prevents duplicate order submission

**Code Location**: `ExecutionJournal.cs`

## Scenarios

### Scenario 1: Restart Before Range Locked

**Timeline**:
- 18:00 CT: Range start
- 18:30 CT: Restart occurs (range still building)
- 18:30 CT: Restart detected
- 18:30 CT: BarsRequest loads bars from 18:00 to 18:30
- 18:30 CT: Range recomputed from historical + live bars
- 22:00 CT: Range locks at slot_time (normal)

**Result**: ✅ Range reconstructed successfully, trading proceeds normally

### Scenario 2: Restart After Range Locked (Before Entry)

**Timeline**:
- 18:00 CT: Range start
- 22:00 CT: Range locked
- 22:15 CT: Restart occurs (no entry yet)
- 22:15 CT: Restart detected
- 22:15 CT: BarsRequest loads bars from 18:00 to 22:00
- 22:15 CT: Range recomputed (may differ slightly)
- 22:15 CT: Range locks immediately (already past slot_time)

**Result**: ⚠️ Range may differ from uninterrupted operation, but trading can proceed

### Scenario 3: Restart With Open Position

**Timeline**:
- 18:00 CT: Range start
- 22:00 CT: Range locked
- 22:30 CT: Entry filled, position opened
- 23:00 CT: Restart occurs (position still open)
- 23:00 CT: Account snapshot taken
- 23:00 CT: Position matched to stream via execution journal
- 23:00 CT: Protective orders recreated
- 23:00 CT: Stream resumes managing position

**Result**: ✅ Position protected, trading continues

### Scenario 4: Restart With Unmatched Position

**Timeline**:
- 23:00 CT: Restart occurs
- 23:00 CT: Account snapshot shows position
- 23:00 CT: Position cannot be matched to any stream
- 23:00 CT: Event: `RECOVERY_POSITION_UNMATCHED`
- 23:00 CT: Stream stands down (`STREAM_STAND_DOWN`)
- 23:00 CT: Robot remains fail-closed (no new entries)

**Result**: ⚠️ Requires operator intervention, robot blocks new entries

## Log Events During Restart

### Key Events

1. **`MID_SESSION_RESTART_DETECTED`** - Restart detected
2. **`RECOVERY_ACCOUNT_SNAPSHOT`** - Account state captured
3. **`RECOVERY_POSITION_RECONCILED`** - Position matched to stream
4. **`RECOVERY_POSITION_UNMATCHED`** - Position cannot be matched
5. **`RECOVERY_PROTECTIVE_ORDERS_PLACED`** - Stop/target orders recreated
6. **`RANGE_INITIALIZED_FROM_HISTORY`** - Range recomputed from historical bars
7. **`STREAM_STAND_DOWN`** - Stream disabled (if recovery fails)

## Safety Guarantees

### ✅ What is Preserved

- **Journal state** - Stream state persisted to disk
- **Commit status** - Committed streams stay committed
- **Position protection** - Stop/target orders recreated
- **No duplicate entries** - Journal prevents double-entry

### ⚠️ What May Differ

- **Range values** - May differ if restart occurs after slot_time
- **Entry timing** - May enter at different time if restart after range lock
- **Bar filtering** - May filter bars differently on reconstruction

### 🚫 What is Blocked

- **Committed streams** - Cannot re-arm after commit
- **Unmatched positions** - Blocks new entries until resolved
- **Invalid journals** - Falls back to account inspection, fails closed if ambiguous

## Recommendations

### For Operators

1. **Monitor `MID_SESSION_RESTART_DETECTED` events** - Know when restarts occur
2. **Check `RECOVERY_POSITION_UNMATCHED`** - Requires immediate attention
3. **Review range differences** - Compare reconstructed ranges to original
4. **Verify protective orders** - Ensure stop/target orders are recreated

### For Development

1. **Consider range validation** - Compare reconstructed vs. original ranges
2. **Enhance position matching** - Improve matching logic for edge cases
3. **Add restart metrics** - Track restart frequency and impact
4. **Document range differences** - Log when reconstructed range differs significantly

## Testing

### Test Cases

1. **Restart before range start** - Should initialize normally
2. **Restart during range building** - Should reconstruct range
3. **Restart after range locked** - Should use reconstructed range
4. **Restart with position** - Should restore protection
5. **Restart with unmatched position** - Should stand down safely

## Related Documentation

- `docs/robot/NinjaTrader Robot Blueprint (Execution Layer).txt` - Section 12: Restart and Recovery
- `modules/robot/core/StreamStateMachine.cs` - Restart detection logic
- `modules/robot/core/RobotEngine.cs` - Position recovery logic
- `modules/robot/core/JournalStore.cs` - Journal persistence
