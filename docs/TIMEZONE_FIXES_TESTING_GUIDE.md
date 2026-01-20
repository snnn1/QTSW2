# Timezone Fixes Testing Guide

## Overview

This guide provides testing strategies for the timezone fixes **without requiring live trading or open ranges**. All tests can be performed using the UI, logs, and manual verification.

---

## Test 1: Frontend Date Initialization (No Trading Required)

### Purpose
Verify that `currentTradingDay` uses Chicago timezone, not browser timezone.

### Steps

1. **Change Browser Timezone** (to simulate different user locations):
   - **Chrome/Edge**: Settings → Time zone → Change to different timezone (e.g., EST, PST, UTC)
   - **Firefox**: Install timezone extension or use developer tools
   - **Alternative**: Use browser DevTools → Console → Run:
     ```javascript
     // This doesn't actually change timezone, but you can verify behavior
     console.log('Browser timezone:', Intl.DateTimeFormat().resolvedOptions().timeZone)
     ```

2. **Open Timetable Tab**:
   - Navigate to timetable tab in the UI
   - Check what date is displayed at the top

3. **Verify Chicago Date**:
   - Calculate what Chicago date should be (use online timezone converter or Python):
     ```python
     from datetime import datetime
     import pytz
     chicago_tz = pytz.timezone("America/Chicago")
     chicago_now = datetime.now(chicago_tz)
     print(f"Chicago date: {chicago_now.date()}")
     ```
   - **Expected**: UI should show Chicago date, NOT browser date
   - **If wrong**: Date will be off by 1 day for users in certain timezones

### Success Criteria
- ✅ UI displays Chicago date regardless of browser timezone
- ✅ Date matches calculated Chicago date (not browser date)

---

## Test 2: Date String Conversion (No Trading Required)

### Purpose
Verify that dates are converted to `YYYY-MM-DD` without UTC conversion.

### Steps

1. **Open Browser Console** (F12 → Console tab)

2. **Check Date Conversion**:
   ```javascript
   // Test the dateToYYYYMMDD function
   const testDate = new Date(2026, 0, 19, 23, 0, 0) // Jan 19, 2026 11pm local
   console.log('Test date:', testDate)
   console.log('toISOString().split("T")[0]:', testDate.toISOString().split('T')[0])
   
   // Should use dateToYYYYMMDD instead
   const year = testDate.getFullYear()
   const month = String(testDate.getMonth() + 1).padStart(2, '0')
   const day = String(testDate.getDate()).padStart(2, '0')
   const correct = `${year}-${month}-${day}`
   console.log('dateToYYYYMMDD result:', correct)
   ```

3. **Verify Worker Messages**:
   - Open Network tab → Filter by "worker"
   - Look for `CALCULATE_TIMETABLE` messages
   - Check `currentTradingDay` value in message payload
   - **Expected**: Should be `YYYY-MM-DD` string, not ISO UTC string

### Success Criteria
- ✅ Date strings are `YYYY-MM-DD` format
- ✅ No time component in date strings
- ✅ Dates don't shift based on browser timezone

---

## Test 3: Bar Time Interpretation Locking (Requires Strategy Running)

### Purpose
Verify that bar time interpretation is detected once and locked.

### Steps

1. **Start Strategy** (SIM or DRYRUN mode):
   - Load strategy in NinjaTrader
   - Start with live data or replay

2. **Check Logs for Detection Event**:
   - Look for `BAR_TIME_INTERPRETATION_DETECTED` event
   - Should appear **only once** (on first bar)
   - Check log fields:
     - `raw_times_value`: Raw bar time from NinjaTrader
     - `raw_times_kind`: DateTimeKind (Unspecified, UTC, or Local)
     - `chosen_interpretation`: "UTC" or "CHICAGO"
     - `reason`: Why this interpretation was chosen
     - `bar_age_if_utc`: Bar age if treated as UTC
     - `bar_age_if_chicago`: Bar age if treated as Chicago

3. **Verify Locking**:
   - Process multiple bars (wait for 10+ bars)
   - Check logs: Should **NOT** see `BAR_TIME_INTERPRETATION_DETECTED` again
   - All subsequent bars should use the locked interpretation

4. **Check for Mismatch Alerts** (should never occur):
   - Look for `BAR_TIME_INTERPRETATION_MISMATCH` events
   - **Expected**: Should never appear (if it does, it's a bug)

### Success Criteria
- ✅ `BAR_TIME_INTERPRETATION_DETECTED` appears exactly once
- ✅ No subsequent detection events
- ✅ No `BAR_TIME_INTERPRETATION_MISMATCH` events
- ✅ Bar ages are reasonable (0-10 minutes for recent bars)

---

## Test 4: Backend Timetable Format (No Trading Required)

### Purpose
Verify that backend emits `trading_date` in correct format.

### Steps

1. **Check Timetable JSON File**:
   - Open `data/timetable/timetable_current.json`
   - Check `trading_date` field

2. **Verify Format**:
   ```json
   {
     "trading_date": "2026-01-19",  // ✅ Correct: YYYY-MM-DD
     "timezone": "America/Chicago",
     "streams": [...]
   }
   ```

3. **Verify Content**:
   - `trading_date` should be date-only (no time)
   - Should match Chicago date (not UTC, not local)
   - Format should be exactly `YYYY-MM-DD`

### Success Criteria
- ✅ `trading_date` is `YYYY-MM-DD` format
- ✅ No time component
- ✅ No timezone suffix
- ✅ Matches Chicago date

---

## Test 5: Date Picker Behavior (No Trading Required)

### Purpose
Verify that date picker selections are handled correctly.

### Steps

1. **Select a Date**:
   - Use date picker in timetable UI
   - Select a specific date (e.g., "2026-01-15")

2. **Check Worker Messages**:
   - Open Console → Network tab
   - Filter for worker messages
   - Find `CALCULATE_TIMETABLE` message
   - Check `currentTradingDay` payload

3. **Verify Date Used**:
   - Selected date should be passed as `YYYY-MM-DD` string
   - Should match exactly what was selected
   - Should not be converted through UTC

### Success Criteria
- ✅ Selected date is passed correctly
- ✅ No timezone conversion occurs
- ✅ Date matches selection exactly

---

## Test 6: Cross-Timezone Verification (Manual)

### Purpose
Verify system works correctly for users in different timezones.

### Steps

1. **Calculate Expected Chicago Date**:
   ```python
   from datetime import datetime
   import pytz
   
   chicago_tz = pytz.timezone("America/Chicago")
   chicago_now = datetime.now(chicago_tz)
   chicago_date = chicago_now.date().isoformat()
   print(f"Chicago date: {chicago_date}")
   ```

2. **Test Different Scenarios**:
   - **User in EST (UTC-5)**: Should see Chicago date, not EST date
   - **User in PST (UTC-8)**: Should see Chicago date, not PST date
   - **User in UTC**: Should see Chicago date, not UTC date

3. **Verify UI Display**:
   - Check timetable header date
   - Check date picker default value
   - Both should show Chicago date

### Success Criteria
- ✅ All users see Chicago date regardless of their timezone
- ✅ No date shifts based on browser timezone

---

## Test 7: Log Analysis (Post-Deployment)

### Purpose
Verify fixes are working in production by analyzing logs.

### Steps

1. **Check for Detection Events**:
   ```bash
   # Search logs for bar time interpretation detection
   grep "BAR_TIME_INTERPRETATION_DETECTED" logs/robot/*.jsonl
   ```
   - Should see one per instrument per strategy start
   - Should NOT see multiple detections for same instrument

2. **Check for Mismatch Alerts**:
   ```bash
   # Search for mismatch alerts (should be empty)
   grep "BAR_TIME_INTERPRETATION_MISMATCH" logs/robot/*.jsonl
   ```
   - **Expected**: No results (empty)
   - **If found**: This indicates a bug

3. **Verify Bar Ages**:
   ```bash
   # Check bar ages are reasonable
   grep "bar_age" logs/robot/*.jsonl | head -20
   ```
   - Bar ages should be positive (0-10 minutes for recent bars)
   - Should not see negative bar ages

### Success Criteria
- ✅ One detection event per instrument per start
- ✅ No mismatch alerts
- ✅ All bar ages are positive and reasonable

---

## Quick Verification Checklist

### Frontend (Can Test Now)
- [ ] Open timetable tab, verify date displayed
- [ ] Check browser console for date conversion
- [ ] Select date in picker, verify it's passed correctly
- [ ] Check Network tab for worker messages with correct date format

### Backend (Can Test Now)
- [ ] Check `timetable_current.json` file
- [ ] Verify `trading_date` is `YYYY-MM-DD` format
- [ ] Verify no time component in `trading_date`

### Strategy (Requires Running Strategy)
- [ ] Start strategy, check logs for `BAR_TIME_INTERPRETATION_DETECTED`
- [ ] Verify detection happens only once
- [ ] Process multiple bars, verify no re-detection
- [ ] Check for `BAR_TIME_INTERPRETATION_MISMATCH` (should be none)

---

## Manual Test Script

Run this in browser console to verify date handling:

```javascript
// Test date conversion
function testDateConversion() {
  console.log('=== Date Conversion Test ===')
  
  // Test date: Jan 19, 2026 11pm (could be different date in UTC)
  const testDate = new Date(2026, 0, 19, 23, 0, 0)
  console.log('Test date (local):', testDate.toString())
  console.log('Test date (ISO UTC):', testDate.toISOString())
  
  // WRONG way (old code)
  const wrong = testDate.toISOString().split('T')[0]
  console.log('WRONG (toISOString):', wrong)
  
  // RIGHT way (new code)
  const year = testDate.getFullYear()
  const month = String(testDate.getMonth() + 1).padStart(2, '0')
  const day = String(testDate.getDate()).padStart(2, '0')
  const right = `${year}-${month}-${day}`
  console.log('RIGHT (dateToYYYYMMDD):', right)
  
  // Check if they differ (they will if timezone offset shifts date)
  if (wrong !== right) {
    console.warn('⚠️ Date conversion differs! This would cause timezone bugs.')
  } else {
    console.log('✅ Date conversion matches (no timezone shift)')
  }
}

testDateConversion()
```

---

## Expected Results Summary

| Test | Expected Result | How to Verify |
|------|----------------|---------------|
| Frontend Date | Shows Chicago date | Check UI display |
| Date Conversion | No UTC conversion | Check console/network |
| Bar Interpretation | Locked after first bar | Check logs |
| Backend Format | `YYYY-MM-DD` format | Check JSON file |
| Date Picker | Passes date correctly | Check network messages |
| Cross-Timezone | All users see Chicago date | Test with different timezones |
| Log Analysis | One detection, no mismatches | Search logs |

---

## Troubleshooting

### If dates are wrong:
1. Check browser console for errors
2. Verify `dateUtils.js` is imported correctly
3. Check that `getChicagoDateNow()` is being called
4. Verify date picker is passing strings, not Date objects

### If bar interpretation flips:
1. Check logs for `BAR_TIME_INTERPRETATION_MISMATCH`
2. Verify locking mechanism is working
3. Check that `_barTimeInterpretationLocked` is set to true
4. Verify interpretation is reused on subsequent bars

### If tests fail:
1. Check browser console for JavaScript errors
2. Verify all imports are correct
3. Check that helper functions are defined
4. Verify C# code compiles without errors
