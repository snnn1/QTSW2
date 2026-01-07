# Missing Orders at 15:00 UTC (09:00 Chicago) - Root Cause

## Problem
Orders should have been submitted at 15:00 UTC (09:00 Chicago) for ES1, GC1, NQ1, RTY1, YM1 (all have `slot_time: "09:00"`), but no orders were submitted.

## Root Cause Identified ✅

**Critical Bug:** When `StandDown()` is called due to invalid timetable, it sets `_activeTradingDate = null`. Later, when a valid timetable loads, `ApplyTimetable()` returns early because `_activeTradingDate is null`, so streams are never created.

### The Bug Chain

1. **14:37:04 UTC** - Engines restart after previous stop
2. **14:37:04 UTC** - Timetable loads but has `BAD_TRADING_DATE` (empty `trading_date`)
3. **StandDown() called** → `_streams.Clear()` + `_activeTradingDate = null`
4. **Timetable keeps polling** - File gets updated repeatedly
5. **15:00:05 UTC** - Bars arrive, but no streams exist
6. **15:01:06 UTC** - Valid timetable loads, but `ApplyTimetable()` returns early because `_activeTradingDate is null`
7. **Result:** No streams created → No orders submitted

### Code Evidence

**File:** `modules/robot/core/RobotEngine.cs`

**Line 311** - `ApplyTimetable()` early return:
```csharp
private void ApplyTimetable(TimetableContract timetable, DateOnly tradingDate, DateTimeOffset utcNow)
{
    if (_spec is null || _time is null || _activeTradingDate is null || _lastTimetableHash is null) return;
    // ← If _activeTradingDate is null, streams are never created!
```

**Line 466-469** - `StandDown()` clears state:
```csharp
private void StandDown()
{
    _streams.Clear();
    _activeTradingDate = null;  // ← This prevents ApplyTimetable() from running!
}
```

**Line 246-251** - Invalid timetable calls StandDown():
```csharp
if (!TimeService.TryParseDateOnly(timetable.TradingDate, out var tradingDate))
{
    LogEvent(RobotEvents.EngineBase(..., eventType: "TIMETABLE_INVALID", ...,
        new { reason = "BAD_TRADING_DATE", trading_date = timetable.TradingDate }));
    StandDown();  // ← Sets _activeTradingDate = null
    return;
}
```

### Timeline from Logs

```
14:37:04 UTC - ENGINE_START
14:37:04 UTC - TIMETABLE_UPDATED
14:37:04 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE → StandDown() called
              → _activeTradingDate = null, _streams.Clear()

[Repeated TIMETABLE_INVALID errors]

15:00:05 UTC - TRADING_DAY_ROLLOVER (bars arriving, but no streams exist)
15:01:06 UTC - TIMETABLE_UPDATED (valid timetable)
15:01:06 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE (still invalid)
15:02:05 UTC - TIMETABLE_UPDATED (valid timetable)
15:02:05 UTC - TIMETABLE_INVALID: BAD_TRADING_DATE (still invalid)

[No TIMETABLE_VALIDATED events = No streams created]
```

## The Fix

**Problem:** `ApplyTimetable()` checks `_activeTradingDate is null` and returns early, but `_activeTradingDate` is set AFTER validation in `ReloadTimetableIfChanged()`.

**Solution:** Set `_activeTradingDate` BEFORE calling `ApplyTimetable()`, or remove the null check in `ApplyTimetable()`.

### Option 1: Set _activeTradingDate Before ApplyTimetable()

Move `_activeTradingDate = timetable.TradingDate;` to BEFORE `ApplyTimetable()` call:

```csharp
// After validation passes
_activeTradingDate = timetable.TradingDate;  // ← Set BEFORE ApplyTimetable()
ApplyTimetable(timetable, tradingDate, utcNow);
```

### Option 2: Remove Null Check in ApplyTimetable()

Use `tradingDate` parameter instead of `_activeTradingDate`:

```csharp
private void ApplyTimetable(TimetableContract timetable, DateOnly tradingDate, DateTimeOffset utcNow)
{
    if (_spec is null || _time is null || _lastTimetableHash is null) return;
    // Remove _activeTradingDate check - use tradingDate parameter instead
```

## Why This Happened

1. Timetable file is being updated with empty `trading_date` intermittently
2. When invalid timetable loads → `StandDown()` → `_activeTradingDate = null`
3. When valid timetable loads later → `ApplyTimetable()` returns early → No streams created
4. At 15:00 UTC → No streams exist → No orders submitted

## Files to Fix

- `modules/robot/core/RobotEngine.cs` - Fix `ApplyTimetable()` null check or set `_activeTradingDate` before calling it
- `RobotCore_For_NinjaTrader/RobotEngine.cs` - Same fix

## Additional Issue

The timetable file is still being updated with empty `trading_date`. This needs to be fixed in the timetable generation code, but the Robot should also be more resilient to this.
