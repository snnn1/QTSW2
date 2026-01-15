# S1 Range Building Timing

## Answer: When Should S1 Streams Enter RANGE_BUILDING?

**S1 streams should enter RANGE_BUILDING at 02:00 Chicago time (08:00 UTC in winter, 07:00 UTC in summer)**

## Configuration

From `configs/analyzer_robot_parity.json`:
```json
"S1": {
  "range_start_time": "02:00",  // Chicago time
  "slot_end_times": ["07:30", "08:00", "09:00"]
}
```

## Transition Logic

**File**: `modules/robot/core/StreamStateMachine.cs` (line 434)

```csharp
if (utcNow >= RangeStartUtc)
{
    // Transition to RANGE_BUILDING
    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
}
```

**Condition**: `utcNow >= RangeStartUtc`

Where `RangeStartUtc` is calculated as:
- `RangeStartChicagoTime` = Trading Date + "02:00" (Chicago timezone)
- `RangeStartUtc` = `RangeStartChicagoTime.ToUniversalTime()`

## Timeline for S1 (09:00 Slot)

| Chicago Time | UTC Time (Winter) | State | What Happens |
|--------------|-------------------|-------|--------------|
| 00:00 | 06:00 | PRE_HYDRATION | Robot starts, loads CSV |
| 00:05 | 06:05 | PRE_HYDRATION → ARMED | Pre-hydration completes |
| **02:00** | **08:00** | **ARMED → RANGE_BUILDING** | **Range building starts** |
| 02:00-09:00 | 08:00-15:00 | RANGE_BUILDING | Collecting bars, updating range |
| 09:00 | 15:00 | RANGE_BUILDING → RANGE_LOCKED | Range locks |

## Current Status Check

Based on logs checked:
- **Current Time**: 19:27 Chicago (01:27 UTC next day)
- **Range Start Was**: 02:00 Chicago (08:00 UTC) - **17.5 hours ago**
- **Expected State**: RANGE_BUILDING or RANGE_LOCKED
- **Actual State**: PRE_HYDRATION_COMPLETE (last event)

**Issue**: Streams completed pre-hydration but never transitioned to RANGE_BUILDING.

## Possible Causes

1. **Robot restarted after range start time** - Streams re-entered PRE_HYDRATION
2. **Transition condition not met** - `utcNow >= RangeStartUtc` check failing
3. **Pre-hydration flag issue** - `_preHydrationComplete` might be false
4. **Time calculation issue** - `RangeStartUtc` might be incorrect

## Next Steps to Debug

1. Check if `Tick()` is being called (look for ENGINE_TICK_HEARTBEAT)
2. Check ARMED_STATE_DIAGNOSTIC logs to see `can_transition` flag
3. Verify `RangeStartUtc` calculation is correct
4. Check if `_preHydrationComplete` is true when in ARMED state
