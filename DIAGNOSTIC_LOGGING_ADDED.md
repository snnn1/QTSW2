# Diagnostic Logging Added for Timezone Investigation

## Purpose

Added comprehensive diagnostic logging to verify bar timestamps and timezone conversions, helping diagnose why ranges might appear to be calculated for UTC time instead of Chicago time.

## New Diagnostic Events

### 1. BAR_RECEIVED_DIAGNOSTIC

**When:** Every 5 minutes (rate-limited) when bars are received during `RANGE_BUILDING` state

**Location:** `StreamStateMachine.OnBar()` method

**Fields:**
- `bar_utc`: UTC timestamp of the bar (as received)
- `bar_utc_kind`: DateTime.Kind of the UTC timestamp
- `bar_chicago`: Chicago-converted timestamp of the bar
- `bar_chicago_offset`: Timezone offset of Chicago time
- `range_start_chicago`: Expected range start (Chicago time)
- `range_end_chicago`: Expected range end (Chicago time)
- `range_start_utc`: Expected range start (UTC time)
- `range_end_utc`: Expected range end (UTC time)
- `in_range_window`: Boolean indicating if bar matches filter
- `bar_buffer_count`: Current number of bars in buffer
- `time_until_slot_seconds`: Seconds until slot time

**What to Look For:**
- Verify `bar_utc` matches expected UTC window (e.g., 08:00-15:00 for Chicago 02:00-09:00)
- Verify `bar_chicago` matches expected Chicago window (e.g., 02:00-09:00)
- Check if `in_range_window` is true when bars should be included

### 2. BAR_FILTERED_OUT

**When:** When bars are filtered out and within 30 minutes of the range window (rate-limited)

**Location:** `StreamStateMachine.OnBar()` method

**Fields:**
- `bar_utc`: UTC timestamp of filtered bar
- `bar_chicago`: Chicago timestamp of filtered bar
- `range_start_chicago`: Range start (Chicago)
- `range_end_chicago`: Range end (Chicago)
- `reason`: "BEFORE_RANGE_START" or "AFTER_RANGE_END"
- `minutes_from_start`: Minutes before range start (if before)
- `minutes_from_end`: Minutes after range end (if after)

**What to Look For:**
- Check if bars are being filtered incorrectly
- Verify timestamps match expected windows

### 3. Enhanced RANGE_COMPUTE_START

**When:** When range computation begins

**Location:** `StreamStateMachine.Tick()` → `RANGE_BUILDING` case

**New Fields Added:**
- `range_start_chicago`: Range start in Chicago time
- `range_end_chicago`: Range end in Chicago time
- `expected_chicago_window`: Human-readable Chicago window (e.g., "02:00 to 09:00")
- `expected_utc_window`: Human-readable UTC window (e.g., "08:00 to 15:00")
- `bar_buffer_count`: Number of bars in buffer at computation start
- `note`: Clarification that range is for Chicago time

**What to Look For:**
- Verify `expected_chicago_window` matches config (e.g., "02:00 to 09:00" for S1)
- Verify `expected_utc_window` is correct conversion (e.g., "08:00 to 15:00" for Chicago 02:00-09:00)
- Check `bar_buffer_count` - should be > 0 if bars were received

## How to Use These Diagnostics

### Step 1: Check BAR_RECEIVED_DIAGNOSTIC Events

Look for these events in logs to verify:
1. **Bar timestamps are correct:**
   - `bar_utc` should be UTC 08:00-15:00 for Chicago 02:00-09:00 window
   - `bar_chicago` should be Chicago 02:00-09:00

2. **Bar filtering is working:**
   - `in_range_window` should be `true` for bars within the window
   - `in_range_window` should be `false` for bars outside the window

3. **Bar buffer is populating:**
   - `bar_buffer_count` should increase as bars are received

### Step 2: Check BAR_FILTERED_OUT Events

Look for bars being filtered out incorrectly:
- If bars with Chicago time 02:00-09:00 are filtered out, there's a bug
- If bars with UTC time 02:00-09:00 are included, timestamps are wrong

### Step 3: Check RANGE_COMPUTE_START Events

Verify the expected windows match:
- `expected_chicago_window` should match config (e.g., "02:00 to 09:00")
- `expected_utc_window` should be correct conversion
- `bar_buffer_count` should be > 0 if bars were received

## Expected Behavior

### For GC S1 Slot 09:00:

**Config:**
- `range_start_time`: "02:00" (Chicago)
- `slot_time`: "09:00" (Chicago)

**Expected Windows:**
- Chicago: 02:00 to 09:00
- UTC: 08:00 to 15:00 (during CST/DST)

**Expected Bar Timestamps:**
- `bar_utc`: Should be UTC 08:00:00 to 14:59:00
- `bar_chicago`: Should be Chicago 02:00:00 to 08:59:00

**Expected Filtering:**
- Bars with `bar_chicago` 02:00-09:00 → `in_range_window = true`
- Bars with `bar_chicago` < 02:00 → `in_range_window = false` (BEFORE_RANGE_START)
- Bars with `bar_chicago` >= 09:00 → `in_range_window = false` (AFTER_RANGE_END)

## Troubleshooting

### If bars have UTC timestamps of 02:00-09:00:

**Problem:** Bars are being received with wrong timestamps (UTC 02:00 instead of UTC 08:00)

**Solution:** Fix NinjaTrader bar timestamp conversion in `RobotSimStrategy.OnBarUpdate()`

### If bars are filtered out incorrectly:

**Problem:** Bar filtering logic is wrong or timezone conversion is incorrect

**Solution:** Check `ConvertUtcToChicago()` function and bar filtering logic

### If bar_buffer_count is 0 at computation time:

**Problem:** Bars are not being received or are all filtered out

**Solution:** Check `BAR_RECEIVED_DIAGNOSTIC` events to see if bars are being received

## Files Modified

1. `modules/robot/core/StreamStateMachine.cs`
   - Added `BAR_RECEIVED_DIAGNOSTIC` logging
   - Added `BAR_FILTERED_OUT` logging
   - Enhanced `RANGE_COMPUTE_START` logging

2. `RobotCore_For_NinjaTrader/StreamStateMachine.cs`
   - Same changes as above

## Next Steps

1. **Deploy and run** the strategy
2. **Monitor logs** for the new diagnostic events
3. **Analyze** bar timestamps and filtering behavior
4. **Identify** the root cause of timezone issues
5. **Fix** any bugs found
