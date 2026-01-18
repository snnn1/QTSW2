# Trading Date Locking Bug Analysis

## Problem Identified

**From logs:**
- **TRADING_DATE_LOCKED** at `2026-01-17T21:03:39.0826421+00:00`
- **Trading date locked to:** `2026-01-01`
- **Bar that locked it:**
  - UTC: `2026-01-02T05:02:00.0000000+00:00`
  - Chicago: `2026-01-01T23:02:00.0000000-06:00`
  - Time of day: `23:02` (11:02 PM)
- **Earliest session range start:** `02:00` (2:00 AM)
- **Session:** S1

## The Bug

**Current Code Logic (Line 478):**
```csharp
if (barTimeOfDay < earliestRangeStart.Value)
{
    // Ignore bar - before session start
    return;
}
// Lock trading date
```

**What Happened:**
- Bar time: `23:02` (11:02 PM on Jan 1)
- Session start: `02:00` (2:00 AM)
- Comparison: `23:02 < 02:00` = **FALSE**
- Result: Bar is considered "session-valid" and locks trading date to Jan 1

**Why This Is Wrong:**
- A bar at `23:02` on Jan 1 is **BEFORE** the session start at `02:00` on Jan 2
- But when comparing `TimeOnly` values, `23:02 >= 02:00` is `True` because 23:02 is later in the day
- This causes overnight bars (23:00-23:59) to incorrectly lock the trading date

## Root Cause

The code compares `TimeOnly` values without considering that:
- Bars from `23:00` to `23:59` on day N are actually **before** the session start at `02:00` on day N+1
- The comparison `barTimeOfDay < earliestRangeStart` fails for overnight bars because `23:02` is numerically greater than `02:00`

## Solution

We need to handle the overnight case where bars arrive between midnight and the session start. The logic should be:

1. If `barTimeOfDay >= earliestRangeStart`: Bar is session-valid (normal case)
2. If `barTimeOfDay < earliestRangeStart`: 
   - If `barTimeOfDay` is between `00:00` and `earliestRangeStart`: This is BEFORE session start, ignore
   - If `barTimeOfDay` is between `earliestRangeStart` and `23:59`: This is AFTER session start, lock date

Actually, wait - the bar is at `23:02` which is `23:02 < 02:00` = False, so it should be ignored. But it's not being ignored!

Let me check the actual comparison logic...
