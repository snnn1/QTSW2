# Overnight Bar Fix - Trading Date Locking Bug

**Date:** 2026-01-17

## Problem

Trading date was incorrectly locked from overnight bars (23:02 PM) because `23:02 >= 02:00` evaluates to `True` when comparing `TimeOnly` values, even though 23:02 on day N is actually BEFORE 02:00 on day N+1.

**Example from logs:**
- Trading date locked to: `2026-01-01`
- Bar that locked it: `2026-01-01T23:02:00` Chicago (11:02 PM)
- Bar time of day: `23:02`
- Earliest session range start: `02:00`
- Result: Wrong date locked, all subsequent bars rejected

## Root Cause

The comparison `barTimeOfDay < earliestRangeStart.Value` fails for overnight bars:
- `23:02 < 02:00` = `False` (because 23:02 is numerically greater than 02:00)
- So the bar passes the check and locks the wrong trading date

## Fix Applied

**File:** `modules/robot/core/RobotEngine.cs` (lines 478-498)

Added explicit check for overnight bars (22:00-23:59):

```csharp
bool isBeforeSessionStart = barTimeOfDay < earliestRangeStart.Value;
bool isOvernightBar = barTimeOfDay >= new TimeOnly(22, 0); // 22:00-23:59 are overnight bars

if (isBeforeSessionStart || isOvernightBar)
{
    // Ignore bar - before session start OR overnight bar
    return;
}
```

**Logic:**
- Bars < `earliestRangeStart` (e.g., 00:00-01:59) → Ignored (before session)
- Bars >= 22:00 (e.g., 22:00-23:59) → Ignored (overnight bars)
- Bars in range [`earliestRangeStart`, 22:00) (e.g., 02:00-21:59) → Valid for locking

## Files Updated

- ✅ `modules/robot/core/RobotEngine.cs` - Fix applied
- ✅ `RobotCore_For_NinjaTrader/RobotEngine.cs` - Synced (37 files total)

## Testing

After restart, verify:
1. Trading date locks from a bar between 02:00-21:59
2. Bars at 23:02 are ignored (no date lock)
3. No more `BAR_DATE_MISMATCH` events
4. `TRADING_DATE_LOCKED` event shows correct date

---

*Fix synced to RobotCore_For_NinjaTrader: 2026-01-17*
