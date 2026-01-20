# Timetable Flow and Error Analysis

## How the Timetable Works

### Two Timetable Generation Paths

There are **two separate systems** that generate timetables:

#### 1. **Backend Timetable Generation** (`timetable_engine.py`)
- **When**: Called automatically when master matrix is saved (`file_manager.py` line 66)
- **Method**: `write_execution_timetable_from_master_matrix()`
- **Source**: Reads from master matrix DataFrame (latest date)
- **Output**: Writes `data/timetable/timetable_current.json`
- **Used by**: Robot engine (NinjaTrader) reads this file

#### 2. **Frontend Timetable Calculation** (`matrixWorker.js`)
- **When**: Calculated on-demand in the browser when user views timetable tab
- **Method**: `CALCULATE_TIMETABLE` message handler
- **Source**: Reads from columnar matrix data loaded in browser
- **Output**: Returns timetable array to UI for display
- **Used by**: Matrix/Timetable UI app

### Frontend Timetable Flow (Where Error Occurred)

```
1. User opens timetable tab
   ↓
2. App.jsx calls workerCalculateTimetable()
   ↓
3. Worker receives CALCULATE_TIMETABLE message
   ↓
4. Worker finds latest date in matrix data
   ↓
5. Worker determines displayed date (currentTradingDay)
   ↓
6. FIRST PASS: Loop through matrix rows
   - Filter rows to displayed date (or latest date if displayed date doesn't exist)
   - For each stream found:
     * Get Time column value
     * Get Time Change column value (if exists)
     * Use Time Change if available, otherwise use Time
     * Check filters (DOW, DOM, exclude_times)
     * Add to seenStreams map
   ↓
7. SECOND PASS: Ensure all 14 streams present
   - Loop through all 14 streams (ES1, ES2, GC1, GC2, CL1, CL2, NQ1, NQ2, NG1, NG2, YM1, YM2, RTY1, RTY2)
   - If stream NOT in seenStreams:
     * Stream missing from matrix for this date
     * Set defaultTime = first time slot for session
       - S1 streams: defaultTime = "07:30" (first in ['07:30', '08:00', '09:00'])
       - S2 streams: defaultTime = "09:30" (first in ['09:30', '10:00', '10:30', '11:00'])
     * Check if defaultTime is filtered
     * Add to seenStreams with defaultTime
   ↓
8. Convert seenStreams map to array
   ↓
9. Return timetable to UI
```

## The Error: Why ES1 Showed 07:30

### Root Cause

**ES1 rows exist in the matrix**, but the timetable was displaying "07:30" even though 07:30 is filtered for ES1.

The issue had two potential causes:

1. **Time Change column parsing**: The `Time Change` column format changed from "old -> new" (e.g., "07:30 -> 08:00") to just the new time (e.g., "08:00"). The code was only parsing the "old -> new" format, so if `Time Change` was empty or in the new format, it fell back to the `Time` column, which might have contained "07:30".

2. **Missing stream fallback**: If ES1 wasn't found in the first pass (due to date mismatch or filtering), the second pass would default to the first time slot ("07:30") without checking if it was filtered.

### The Problem

**Scenario 1: Time Change parsing issue**
- ES1 row exists with `Time = "07:30"` and `Time Change = "08:00"` (new format)
- Code only parsed "old -> new" format, so `Time Change` was ignored
- Code used `Time` column = "07:30" (filtered time)
- Filter check blocked it, but `Time` still displayed as "07:30"

**Scenario 2: Missing stream fallback**
- ES1 row exists but wasn't found in first pass (date mismatch or other issue)
- Second pass triggered: Code detects ES1 not in `seenStreams`
- Default time selected: Uses `sessionTimeSlots['S1'][0]` = `"07:30"` (BEFORE FIX)
- Filter check happens AFTER: Code checks if 07:30 is filtered and blocks it
- But Time still shows 07:30: Even though it's blocked, `Time: defaultTime` = "07:30"

### Why This Happened

The sequencer **never selects filtered times**, so if 07:30 is filtered:
- ES1 should have `Time = "08:00"` (or whatever the sequencer selected)
- OR `Time Change` should contain the new time ("08:00")
- But if `Time Change` parsing failed or ES1 wasn't found, the timetable would fall back to default logic
- This violated the principle: "if 07:30 is filtered, it should never appear"

### The Fixes

**Fix 1: Time Change column parsing** (lines 1391-1402)
Updated parsing to handle both formats:
- New format: `Time Change = "08:00"` → use directly
- Old format: `Time Change = "07:30 -> 08:00"` → parse and extract new time
- Fallback: If `Time Change` is empty, use `Time` column

```javascript
// AFTER FIX (lines 1391-1402)
let displayTime = time
if (timeChange && timeChange.trim()) {
  if (timeChange.includes('->')) {
    // Backward compatibility: parse "old -> new" format
    const parts = timeChange.split('->')
    if (parts.length === 2) {
      displayTime = parts[1].trim()
    }
  } else {
    // Current format: Time Change is just the new time
    displayTime = timeChange.trim()
  }
}
```

**Fix 2: Second pass default time selection** (lines 1525-1554)
Changed the second pass to select the **first NON-FILTERED time** instead of always using the first time:

```javascript
// AFTER FIX (lines 1525-1554)
// Collect exclude_times
const excludeTimesList = []
if (streamFilter?.exclude_times?.length > 0) {
  excludeTimesList.push(...streamFilter.exclude_times)
}
if (masterFilter?.exclude_times?.length > 0) {
  excludeTimesList.push(...masterFilter.exclude_times)
}

// Select first NON-FILTERED time
let defaultTime = ''
if (excludeTimesList.length > 0) {
  const excludeTimesNormalized = excludeTimesList.map(t => normalizeTime(t))
  const availableTimes = sessionTimeSlots[session] || []
  // Find first time that's NOT in exclude_times
  for (const timeSlot of availableTimes) {
    if (!excludeTimesNormalized.includes(normalizeTime(timeSlot))) {
      defaultTime = timeSlot  // Use 08:00 if 07:30 is filtered
      break
    }
  }
} else {
  defaultTime = sessionTimeSlots[session]?.[0] || ''  // No filters, use first
}
```

## Key Principles

1. **Sequencer is authoritative**: If a time is filtered, sequencer never selects it
2. **Matrix reflects sequencer**: Matrix `Time` column should never contain filtered times
3. **Timetable must respect filters**: When selecting default times, must skip filtered times
4. **Complete execution contract**: Timetable must include all 14 streams (enabled or blocked)

## Files Involved

- **Frontend**: `modules/matrix_timetable_app/frontend/src/matrixWorker.js` (lines 1207-1620)
- **Backend**: `modules/timetable/timetable_engine.py` (lines 445-640)
- **Matrix Save**: `modules/matrix/file_manager.py` (lines 60-70)
