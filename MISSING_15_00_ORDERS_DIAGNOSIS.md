# Missing Orders at 15:00 UTC (09:00 Chicago) - Diagnosis Report

## Problem
Orders should have been submitted at 15:00 UTC (09:00 Chicago) but were not.

## Clarification
- **15:00 UTC** = **09:00 Chicago** (during standard time, UTC-6)
- **15:00 UTC** = **10:00 Chicago** (during daylight time, UTC-5)
- **09:00 Chicago** IS in the allowed slot_end_times for S1: `["07:30", "08:00", "09:00"]`

So slot_time validation is NOT the issue. The problem is elsewhere.

## Root Cause Analysis

### 1. Slot Time Validation (NOT THE ISSUE)

The Robot validates all timetable `slot_time` values against the allowed `slot_end_times` in the spec:

**Current Spec (`configs/analyzer_robot_parity.json`):**
```json
{
  "sessions": {
    "S1": {
      "slot_end_times": ["07:30", "08:00", "09:00"]
    },
    "S2": {
      "slot_end_times": ["09:30", "10:00", "10:30", "11:00"]
    }
  }
}
```

**15:00 is NOT in either session's allowed slot_end_times!**

### 2. Validation Points

The Robot validates slot_time at TWO points:

#### A. During Timetable Application (`RobotEngine.ApplyTimetable()`)
- **Location:** `modules/robot/core/RobotEngine.cs` line 372-380
- **Action:** If `slot_time` not in allowed list → Stream is **SKIPPED**
- **Log Event:** `STREAM_SKIPPED` with reason `INVALID_SLOT_TIME`
- **Result:** Stream never gets created/updated

```csharp
var allowed = _spec.Sessions[session].SlotEndTimes;
if (!allowed.Contains(slotTimeChicago))
{
    LogEvent(RobotEvents.Base(..., "STREAM_SKIPPED", "ENGINE", 
        new { reason = "INVALID_SLOT_TIME", slot_time = slotTimeChicago, allowed_times = allowed }));
    continue; // Stream skipped - never created
}
```

#### B. During Execution Gate Check (`RiskGate.CheckGates()`)
- **Location:** `modules/robot/core/Execution/RiskGate.cs` line 64-68
- **Action:** If `slot_time` not in allowed list → Execution **BLOCKED**
- **Log Event:** `EXECUTION_BLOCKED` with reason `SLOT_TIME_NOT_ALLOWED`
- **Result:** Order submission prevented

```csharp
var allowedSlots = _spec.Sessions[session].SlotEndTimes;
var slotTimeAllowed = allowedSlots.Contains(slotTimeChicago);
if (!slotTimeAllowed)
{
    return (false, "SLOT_TIME_NOT_ALLOWED");
}
```

### 3. What Happens with 15:00 Slot Time

**Scenario:** Timetable has a stream with `slot_time = "15:00"`

**Result:**
1. ✅ Timetable loads successfully
2. ❌ Stream is **SKIPPED** during `ApplyTimetable()` because 15:00 is not in allowed list
3. ❌ Stream never gets created/armed
4. ❌ No orders can be submitted because stream doesn't exist

**Log Evidence to Look For:**
```json
{
  "event_type": "STREAM_SKIPPED",
  "reason": "INVALID_SLOT_TIME",
  "slot_time": "15:00",
  "allowed_times": ["07:30", "08:00", "09:00"]  // or S2 times
}
```

## Solutions

### Option 1: Add 15:00 to Spec (If 15:00 is Valid)

If 15:00 is a legitimate slot time, add it to the appropriate session:

**For S1:**
```json
{
  "S1": {
    "slot_end_times": ["07:30", "08:00", "09:00", "15:00"]
  }
}
```

**For S2:**
```json
{
  "S2": {
    "slot_end_times": ["09:30", "10:00", "10:30", "11:00", "15:00"]
  }
}
```

**Or create a new session:**
```json
{
  "S3": {
    "range_start_time": "14:00",  // or appropriate start time
    "slot_end_times": ["15:00"]
  }
}
```

### Option 2: Fix Timetable (If 15:00 is Wrong)

If the timetable incorrectly has 15:00, fix the timetable source to use a valid slot time.

### Option 3: Check Session Assignment

Verify which session the 15:00 stream is assigned to. It might be:
- Assigned to wrong session
- Missing session assignment
- Session doesn't exist in spec

## Diagnostic Steps

### 1. Check Bar Time vs Slot Time Comparison

The Robot compares `barUtc >= SlotTimeUtc` to detect when slot time is reached.

**Check logs for:**
- `EXECUTION_GATE_EVAL` events around 15:00 UTC
- Look at `bar_timestamp_utc` vs `slot_time_utc`
- Check if `slot_reached = true`

**Expected log:**
```json
{
  "event_type": "EXECUTION_GATE_EVAL",
  "bar_timestamp_utc": "2026-01-05T15:00:00Z",
  "slot_time_utc": "2026-01-05T15:00:00Z",  // Should match
  "slot_reached": true,
  "final_allowed": true/false
}
```

### 2. Check Stream State

Verify the stream is in `RANGE_LOCKED` state when the bar arrives:
- Look for `RANGE_LOCKED` transition event before 15:00 UTC
- Check if stream state is correct

### 3. Check Entry Detection

Verify entry detection logic is triggering:
- Look for `EXECUTION_ALLOWED` events
- Check for `EXECUTION_BLOCKED` events with reason
- Verify `can_detect_entries = true` in gate eval

### 4. Check Timezone Conversion

Verify `SlotTimeUtc` is calculated correctly:
- Timetable has `slot_time = "09:00"` (Chicago)
- Robot converts to UTC: `SlotTimeUtc = ConvertChicagoLocalToUtc(tradingDate, "09:00")`
- Should result in 15:00 UTC (or 14:00 UTC during DST)

**Check logs for:**
- `RANGE_LOCKED` event shows `slot_time_chicago = "09:00"` and `slot_time_utc = "2026-01-05T15:00:00Z"`
- Verify conversion is correct for the trading date

## Expected Log Sequence (15:00 UTC = 09:00 Chicago)

```
[Before 15:00 UTC] - TIMETABLE_LOADED
[Before 15:00 UTC] - STREAM_ARMED (stream with slot_time="09:00" Chicago)
[Before 15:00 UTC] - RANGE_BUILDING (collecting range)
[15:00:00 UTC] - RANGE_LOCKED (barUtc >= SlotTimeUtc, slot_time_utc=15:00 UTC)
[15:00:00 UTC] - EXECUTION_GATE_EVAL: slot_reached=true, final_allowed=true
[15:00:00 UTC] - EXECUTION_ALLOWED (if immediate entry) OR breakout detection
[15:00:00 UTC] - ORDER_SUBMIT_SUCCESS
```

## Possible Failure Points

### Failure 1: Bar Time Not Reaching Slot Time
```
[15:00:00 UTC] - EXECUTION_GATE_EVAL: slot_reached=false
Reason: barUtc < SlotTimeUtc (bar time hasn't reached slot time yet)
```

### Failure 2: Stream Not in RANGE_LOCKED State
```
[15:00:00 UTC] - Stream still in RANGE_BUILDING state
Reason: barUtc < SlotTimeUtc (comparison failed)
```

### Failure 3: Entry Detection Not Triggering
```
[15:00:00 UTC] - EXECUTION_GATE_EVAL: slot_reached=true, can_detect_entries=false
Reason: Missing breakout levels or entry already detected
```

### Failure 4: Execution Gates Blocking
```
[15:00:00 UTC] - EXECUTION_GATE_EVAL: slot_reached=true, final_allowed=false
Reason: One of the gates failed (check gate evaluation log)
```

### Failure 5: Timezone Conversion Issue
```
[Slot time conversion] - slot_time_chicago="09:00" → slot_time_utc="14:00:00Z" (wrong!)
Reason: DST conversion error or wrong trading date
```

## Next Steps

1. ✅ **Check logs** for `STREAM_SKIPPED` with `INVALID_SLOT_TIME` and `slot_time=15:00`
2. ✅ **Verify timetable** has 15:00 streams and which session they're in
3. ✅ **Decide:** Is 15:00 valid? If yes → add to spec. If no → fix timetable.
4. ✅ **Update spec** if 15:00 is legitimate slot time

## Files to Check

- `data/timetable/timetable_current.json` - Check for 15:00 slot_time
- `logs/robot/robot_ENGINE.jsonl` - Look for STREAM_SKIPPED events
- `configs/analyzer_robot_parity.json` - Verify slot_end_times
