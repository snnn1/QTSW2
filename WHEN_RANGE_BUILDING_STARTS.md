# When Range Building Should Start

## Configuration

From `configs/analyzer_robot_parity.json`:

- **S1 sessions**: `range_start_time: "02:00"` (Chicago time)
- **S2 sessions**: `range_start_time: "08:00"` (Chicago time)

## For the 09:00 Slot (S1 Session)

### Timeline (Chicago Time)
- **Range Start**: 02:00 Chicago
- **Slot Time**: 09:00 Chicago
- **Range Window**: [02:00, 09:00) - 7 hours of data

### Timeline (UTC Time on 2026-01-14)
- **Range Start UTC**: 08:00 UTC (Chicago 02:00)
- **Slot Time UTC**: 15:00 UTC (Chicago 09:00)
- **Range Window**: [08:00 UTC, 15:00 UTC)

## State Machine Flow

1. **PRE_HYDRATION** → Loads historical bars from CSV files
2. **ARMED** → Waiting for `RangeStartUtc` (08:00 UTC / Chicago 02:00)
3. **RANGE_BUILDING** → Starts when `utcNow >= RangeStartUtc` (08:00 UTC)
   - Range is computed from bars between 02:00-09:00 Chicago
   - Range is updated incrementally as live bars arrive
4. **RANGE_LOCKED** → At `SlotTimeUtc` (15:00 UTC / Chicago 09:00)
5. **DONE** → Slot complete

## Transition Condition

Range building starts when:
```csharp
if (utcNow >= RangeStartUtc)  // Line 419 in StreamStateMachine.cs
{
    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
}
```

Where `RangeStartUtc` is calculated as:
```csharp
RangeStartChicagoTime = _time.ConstructChicagoTime(newTradingDate, "02:00");  // For S1
RangeStartUtc = RangeStartChicagoTime.ToUniversalTime();  // Converts to UTC
```

## What Should Have Happened on 2026-01-14

For the **09:00 slot**:
- **08:00 UTC** (Chicago 02:00): Streams should transition from `ARMED` → `RANGE_BUILDING`
- **08:00-15:00 UTC**: Range building active, collecting bars from 02:00-09:00 Chicago
- **15:00 UTC** (Chicago 09:00): Range locks, transition to `RANGE_LOCKED`

## What Actually Happened

- Streams entered `ARMED` state successfully
- Streams **never transitioned** to `RANGE_BUILDING`
- The condition `utcNow >= RangeStartUtc` was never met
- This suggests either:
  1. `RangeStartUtc` was calculated incorrectly
  2. `utcNow` was not advancing properly
  3. The time comparison had a bug

## Expected Behavior

Range building should start at **08:00 UTC** (Chicago 02:00) for the 09:00 slot, which is **7 hours before** the slot time of 15:00 UTC (Chicago 09:00).
