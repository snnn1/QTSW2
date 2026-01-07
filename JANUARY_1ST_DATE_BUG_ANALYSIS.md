# January 1st Date Bug Analysis

## Problem

Slot times are being calculated with **January 1st, 2026** instead of **January 6th, 2026**.

**Evidence:**
- `slot_time_utc: "2026-01-01T15:30:00.0000000+00:00"` (WRONG - should be 2026-01-06)
- `trading_date: ""` (EMPTY in some events)
- Timetable correctly shows `trading_date: "2026-01-06"`

## Code Flow Analysis

### Stream Creation (`StreamStateMachine` Constructor)

1. **Constructor receives:** `string tradingDate` parameter (line 91)
2. **Parses date:** `TimeService.TryParseDateOnly(tradingDate, out var dateOnly)` (line 117)
3. **Calculates slot time:** `SlotTimeUtc = time.ConvertChicagoLocalToUtc(dateOnly, SlotTimeChicago)` (line 122)

**If `tradingDate` is empty or invalid:**
- `TryParseDateOnly` returns `false`
- Constructor throws `InvalidOperationException`
- Stream should NOT be created

**But streams ARE being created**, so `tradingDate` must be valid at creation time.

### Stream Update (`ApplyDirectiveUpdate`)

1. **Receives:** `DateOnly tradingDate` parameter (line 151)
2. **Recalculates:** `SlotTimeUtc = _time.ConvertChicagoLocalToUtc(tradingDate, newSlotTimeChicago)` (line 155)

**This should fix the slot time** if called with correct date.

### Timetable Loading (`ReloadTimetableIfChanged`)

1. **Parses timetable:** `timetable.trading_date` → `DateOnly tradingDate` (line 246)
2. **Calls:** `ParseTimetable(timetable, tradingDate, utcNow)` (line 300)
3. **In ParseTimetable:** Creates/updates streams with `tradingDateStr = tradingDate.ToString("yyyy-MM-dd")` (line 353)

## Root Cause Hypothesis

### Hypothesis 1: Streams Created Before Timetable Loaded

**Scenario:**
- Streams created with default/empty trading date
- Timetable loaded later with correct date
- `ApplyDirectiveUpdate` not called, or called with wrong date

**Evidence Against:**
- Timetable loads at startup (line 96: `ReloadTimetableIfChanged(utcNow, force: true)`)
- Streams created in `ParseTimetable` which is called AFTER timetable loaded

### Hypothesis 2: DateOnly Default Value Issue

**Scenario:**
- `DateOnly` struct defaults to `default(DateOnly)` = `DateTime.MinValue` = `0001-01-01`
- But logs show `2026-01-01`, not `0001-01-01`

**Evidence Against:**
- DateOnly default would be `0001-01-01`, not `2026-01-01`

### Hypothesis 3: Stale Stream State Persisting

**Scenario:**
- Streams created earlier with January 1st date
- Streams persist in `_streams` dictionary
- `ApplyDirectiveUpdate` not being called for existing streams
- Or `ApplyDirectiveUpdate` called but with wrong date

**Evidence For:**
- `_streams` dictionary persists across timetable reloads
- If stream exists, `ApplyDirectiveUpdate` is called (line 425)
- But if `tradingDate` parameter is wrong, slot time stays wrong

### Hypothesis 4: Date Parsing Issue

**Scenario:**
- `timetable.trading_date` is correct ("2026-01-06")
- But `TryParseDateOnly` somehow returns January 1st
- Or date is being modified somewhere

**Evidence Needed:**
- Check what `timetable.trading_date` actually contains
- Check what `tradingDate` parameter contains when streams created

## Most Likely Root Cause

**Streams are being created/updated with a `tradingDate` that is January 1st, 2026 instead of January 6th, 2026.**

**Possible causes:**
1. **Timetable has wrong date** - But logs show correct date
2. **Date parsing returns wrong value** - Need to verify
3. **Streams created before timetable loaded** - Unlikely (timetable loads first)
4. **ApplyDirectiveUpdate not being called** - Need to verify
5. **ApplyDirectiveUpdate called with wrong date** - Most likely

## Next Steps

1. **Add logging** to show what `tradingDate` is when streams are created
2. **Add logging** to show what `tradingDate` is when `ApplyDirectiveUpdate` is called
3. **Verify** timetable.trading_date is correct when parsed
4. **Check** if streams are being created before timetable is loaded
