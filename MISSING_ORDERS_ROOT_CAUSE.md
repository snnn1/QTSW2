# Missing Orders at 15:00 UTC - Root Cause Found

## Problem Summary
Orders should have been submitted at 15:00 UTC (09:00 Chicago) but were not.

## Root Cause Identified ✅

**The timetable file has an empty or invalid `trading_date` field.**

### Evidence from Logs

Repeated `TIMETABLE_INVALID` errors with:
```json
{
  "event": "TIMETABLE_INVALID",
  "reason": "BAD_TRADING_DATE",
  "trading_date": ""  // ← EMPTY!
}
```

### What Happens

1. **Timetable loads** → `TimetableContract.LoadFromFile()` succeeds
2. **Trading date validation fails** → `TimeService.TryParseDateOnly(timetable.TradingDate)` returns `false` because `trading_date` is empty
3. **Engine calls `StandDown()`** → All streams are cleared, no new streams created
4. **No streams exist** → No orders can be submitted

### Code Path

**File:** `modules/robot/core/RobotEngine.cs` (lines 246-251)

```csharp
if (!TimeService.TryParseDateOnly(timetable.TradingDate, out var tradingDate))
{
    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "TIMETABLE_INVALID", state: "ENGINE",
        new { reason = "BAD_TRADING_DATE", trading_date = timetable.TradingDate }));
    StandDown();  // ← This clears all streams!
    return;
}
```

### Timeline from Logs

```
15:00:05 UTC - TRADING_DAY_ROLLOVER (bar timestamp shows 15:00 Chicago = 21:00 UTC)
15:01:06 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE (trading_date="")
15:02:05 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE (trading_date="")
15:06:06 UTC - TIMETABLE_UPDATED (but still invalid)
15:07:09 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE (trading_date="")
15:08:05 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE (trading_date="")
15:10:05 UTC - TIMETABLE_UPDATED (but still invalid)
```

**Result:** No streams created → No orders submitted

## Solution

### Step 1: Check Timetable File

Check `data/timetable/timetable_current.json`:
```bash
cat data/timetable/timetable_current.json | jq '.trading_date'
```

**Expected:** `"2026-01-05"` (or current trading date)
**Actual:** `""` (empty) or missing field

### Step 2: Fix Timetable File

The timetable file must have a valid `trading_date` field:
```json
{
  "trading_date": "2026-01-05",
  "timezone": "America/Chicago",
  "streams": [...]
}
```

### Step 3: Verify Timetable Source

Check where the timetable is generated:
- Is it coming from the Matrix/Timetable engine?
- Is the `trading_date` field being populated correctly?
- Is there a timezone conversion issue?

## Why This Happened

The timetable file is being updated (we see `TIMETABLE_UPDATED` events), but the `trading_date` field is:
1. Empty (`""`)
2. Missing entirely
3. In an invalid format (not `YYYY-MM-DD`)

This causes the Robot to reject the timetable and clear all streams, preventing any order submission.

## Next Steps

1. ✅ **Check timetable file** - Verify `trading_date` field exists and is valid
2. ✅ **Fix timetable generation** - Ensure `trading_date` is populated correctly
3. ✅ **Verify timetable source** - Check Matrix/Timetable engine output
4. ✅ **Re-run** - Once fixed, orders should submit correctly at 15:00 UTC

## Files to Check

- `data/timetable/timetable_current.json` - Check `trading_date` field
- Timetable generation code - Ensure `trading_date` is set
- Matrix/Timetable engine - Verify date formatting
