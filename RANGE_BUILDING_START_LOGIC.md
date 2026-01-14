# How Range Building Knows When to Start

## Overview

Range building starts automatically when the current time reaches the **range start time** defined in the parity spec for each session.

## The Process

### 1. **Initialization (Stream Creation)**
When a stream is created (from the timetable), it:
- Starts in `PRE_HYDRATION` state
- Reads `range_start_time` from the parity spec for its session
- Constructs `RangeStartChicagoTime` by combining:
  - Trading date (from timetable)
  - `range_start_time` from spec (e.g., "02:00" for S1, "08:00" for S2)
- Converts to UTC: `RangeStartUtc = RangeStartChicagoTime.ToUniversalTime()`

**Example:**
- Session: S1
- Trading Date: 2026-01-13
- `range_start_time`: "02:00" (from `configs/analyzer_robot_parity.json`)
- `RangeStartChicagoTime`: 2026-01-13 02:00:00 America/Chicago
- `RangeStartUtc`: 2026-01-13 08:00:00 UTC (assuming CST, UTC-6)

### 2. **Pre-Hydration Phase**
- Stream remains in `PRE_HYDRATION` state
- Loads historical bars from external CSV files
- Once complete, transitions to `ARMED` state

### 3. **ARMED State (Waiting)**
- Stream waits in `ARMED` state
- On each `Tick()` call (every 1 second), checks:
  ```csharp
  if (utcNow >= RangeStartUtc)
  ```
- **If false:** Continues waiting in ARMED state
- **If true:** Transitions to `RANGE_BUILDING` state

### 4. **Transition to RANGE_BUILDING**
When `utcNow >= RangeStartUtc`:
- Logs: `RANGE_WINDOW_STARTED` event
- Transitions: `ARMED` → `RANGE_BUILDING`
- Computes initial range from available bars (pre-hydrated + any live bars received)
- Begins accepting live bars for range computation

## Code Location

**File:** `modules/robot/core/StreamStateMachine.cs`

**Key Method:** `Tick()` method, `ARMED` case (lines 374-428)

```csharp
case StreamState.ARMED:
    // Require pre-hydration completion before entering RANGE_BUILDING
    if (!_preHydrationComplete)
    {
        // Error - should not happen
        break;
    }
    
    if (utcNow >= RangeStartUtc)  // <-- THIS IS THE TRIGGER
    {
        // Range window started
        LogHealth("INFO", "RANGE_WINDOW_STARTED", ...);
        Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
        
        // Compute initial range from available bars
        // ...
    }
    break;
```

## Configuration

**File:** `configs/analyzer_robot_parity.json`

```json
{
  "sessions": {
    "S1": {
      "range_start_time": "02:00",  // <-- Chicago time
      "slot_end_times": ["07:30", "08:00", "09:00"]
    },
    "S2": {
      "range_start_time": "08:00",  // <-- Chicago time
      "slot_end_times": ["09:30", "10:00", "10:30", "11:00"]
    }
  }
}
```

## Timeline Example

**For S1 session with slot_time = "07:30":**

```
00:00 - Stream created, PRE_HYDRATION starts
00:01 - Pre-hydration completes → ARMED state
02:00 - utcNow >= RangeStartUtc → RANGE_BUILDING starts ✅
02:00-07:30 - Range building (accepting bars, updating range)
07:30 - Slot time reached → RANGE_LOCKED
```

**For S2 session with slot_time = "09:30":**

```
00:00 - Stream created, PRE_HYDRATION starts
00:01 - Pre-hydration completes → ARMED state
08:00 - utcNow >= RangeStartUtc → RANGE_BUILDING starts ✅
08:00-09:30 - Range building (accepting bars, updating range)
09:30 - Slot time reached → RANGE_LOCKED
```

## Key Points

1. **Time-Based Trigger:** Range building starts when current UTC time >= `RangeStartUtc`
2. **Session-Specific:** Each session (S1, S2) has its own `range_start_time`
3. **Automatic:** No manual trigger needed - happens automatically via `Tick()` calls
4. **Pre-Hydration Required:** Must complete pre-hydration before entering ARMED (and thus RANGE_BUILDING)
5. **Chicago Time Source:** `range_start_time` is in Chicago time, converted to UTC for comparison

## Verification

You can verify range building started by checking logs for:
- `RANGE_WINDOW_STARTED` event (INFO level)
- State transition: `ARMED` → `RANGE_BUILDING`
- `RANGE_INITIALIZED_FROM_HISTORY` event (if bars were available)

## Summary

**Range building starts automatically when:**
1. ✅ Pre-hydration is complete (`_preHydrationComplete == true`)
2. ✅ Stream is in `ARMED` state
3. ✅ Current UTC time >= `RangeStartUtc` (derived from session's `range_start_time`)

The check happens every second via `Tick()` calls, so the transition is precise to the second.
