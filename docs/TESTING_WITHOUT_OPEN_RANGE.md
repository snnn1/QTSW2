# Testing Timezone Fixes Without Open Range

## The BarsRequest Error (Not Critical)

The error you're seeing:
```
Cannot determine BarsRequest time range for ES. 
This indicates no enabled streams exist for this instrument, or streams not yet created.
```

**This is NOT blocking** - the strategy will continue and use file-based or live bars instead.

**Why it happens**: ES streams exist but are marked as `Committed` (from previous day's journals), so BarsRequest skips them.

## Quick Test: Verify Timezone Fixes Work

Even with this error, you can test the timezone fixes:

### Test 1: Check Bar Time Interpretation (Works Even Without BarsRequest)

1. **Start strategy** (SIM/DRYRUN mode)
2. **Check logs** for `BAR_TIME_INTERPRETATION_DETECTED`:
   ```bash
   grep "BAR_TIME_INTERPRETATION_DETECTED" logs/robot/*.jsonl | tail -1
   ```
3. **Verify**:
   - Event appears **once** (on first bar)
   - Shows `chosen_interpretation`: "UTC" or "CHICAGO"
   - Shows `bar_age_if_utc` and `bar_age_if_chicago`
   - Shows `reason` for selection

4. **Process more bars** (wait for 10+ bars)
5. **Verify no re-detection**:
   ```bash
   grep "BAR_TIME_INTERPRETATION_DETECTED" logs/robot/*.jsonl | wc -l
   ```
   - Should be **1** (only first bar)

6. **Check for mismatches** (should be none):
   ```bash
   grep "BAR_TIME_INTERPRETATION_MISMATCH" logs/robot/*.jsonl
   ```
   - Should be **empty**

### Test 2: Frontend Date Display (No Strategy Needed)

1. **Open timetable tab** in browser
2. **Check date displayed** at top
3. **Calculate Chicago date**:
   ```python
   from datetime import datetime
   import pytz
   print(datetime.now(pytz.timezone("America/Chicago")).date())
   ```
4. **Verify**: UI date matches Chicago date (not browser date)

### Test 3: Date Conversion (Browser Console)

1. **Open browser console** (F12)
2. **Run test**:
   ```javascript
   // Test date conversion
   const testDate = new Date(2026, 0, 19, 23, 0, 0)
   console.log('toISOString:', testDate.toISOString().split('T')[0])
   const year = testDate.getFullYear()
   const month = String(testDate.getMonth() + 1).padStart(2, '0')
   const day = String(testDate.getDate()).padStart(2, '0')
   console.log('dateToYYYYMMDD:', `${year}-${month}-${day}`)
   ```
3. **If they differ**: Old code would have timezone bug
4. **If they match**: No timezone shift (correct)

## Fix BarsRequest Error (Optional)

If you want BarsRequest to work:

### Option 1: Delete Committed Journals

```bash
# Find today's date
date +%Y-%m-%d

# Delete ES journals for today (replace with actual date)
rm logs/robot/journal/2026-01-20_ES1.json
rm logs/robot/journal/2026-01-20_ES2.json

# Restart strategy - streams will be created fresh
```

### Option 2: Check Timetable

Verify timetable has ES streams enabled:

```bash
cat data/timetable/timetable_current.json | jq '.streams[] | select(.stream == "ES1" or .stream == "ES2")'
```

**Expected**:
```json
{
  "stream": "ES1",
  "enabled": true,
  ...
}
{
  "stream": "ES2",
  "enabled": true,
  ...
}
```

## What to Check in Logs

### Stream Creation

```bash
grep "STREAMS_CREATED" logs/robot/*.jsonl | tail -1 | jq '.streams[] | select(.stream | startswith("ES"))'
```

**Expected**: ES1 and ES2 should be listed with `committed: false`

### Stream Status for BarsRequest

```bash
grep "BARSREQUEST_STREAM_STATUS" logs/robot/*.jsonl | tail -1 | jq '.streams[] | select(.instrument == "ES")'
```

**Check**:
- `committed`: Should be `false` for active streams
- `state`: Should be `PRE_HYDRATION` or `ARMED` (not `DONE`)

### If All Streams Committed

```bash
grep "ALL_STREAMS_COMMITTED" logs/robot/*.jsonl | tail -1
```

**If this appears**: Old journals are marking streams as done

## Summary

**For timezone testing**:
- ✅ Bar time interpretation locking: Test with strategy running (works even without BarsRequest)
- ✅ Frontend date display: Test in browser (no strategy needed)
- ✅ Date conversion: Test in browser console (no strategy needed)

**BarsRequest error**:
- Not critical - strategy continues with file-based/live bars
- Caused by committed journals from previous runs
- Can be fixed by deleting journals or ensuring fresh streams

**Bottom line**: You can test all timezone fixes **without fixing the BarsRequest error**. The error just means BarsRequest won't run, but the strategy will still work with live bars.
