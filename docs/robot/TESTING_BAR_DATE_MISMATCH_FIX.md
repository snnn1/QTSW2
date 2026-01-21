# Testing Guide: BAR_DATE_MISMATCH Fix

This guide explains how to test and verify the BAR_DATE_MISMATCH session window fix.

## What Was Fixed

**Before**: Bars were rejected if `bar_chicago_date != active_trading_date`  
**After**: Bars are accepted if they fall within the session window: `[previous_day 17:00 CST, trading_date 16:00 CST)`

## Testing Approaches

### 1. Quick Verification Script

Use the dedicated test script:

```bash
# Test today's logs
python test_bar_date_mismatch_fix.py

# Test specific date
python test_bar_date_mismatch_fix.py 2026-01-20
```

**What it checks:**
- ✅ All BAR_DATE_MISMATCH events have bars outside the session window
- ✅ No bars inside the session window are rejected
- ✅ Required fields (`session_start_chicago`, `session_end_chicago`, `rejection_reason`) are present
- ✅ Rejection reasons are correct (`BEFORE_SESSION_START` or `AFTER_SESSION_END`)

### 2. Comprehensive Log Analysis

Use the existing comprehensive analysis script:

```bash
python analyze_today_comprehensive_full.py
```

**What to look for:**
- **Bar Acceptance Rate**: Should be much higher (evening session bars now accepted)
- **BAR_DATE_MISMATCH Count**: Should be near zero (only bars truly outside window)
- **Stream State Progression**: Streams should progress beyond `PRE_HYDRATION`

### 3. Manual Log Inspection

Check logs directly:

```bash
# Find BAR_DATE_MISMATCH events
grep -r "BAR_DATE_MISMATCH" logs/robot/ | head -20

# Check for evening session bars being accepted
grep -r "BAR_ACCEPTED" logs/robot/ | grep "2026-01-19" | head -10
```

**What to verify:**
- Evening session bars (Jan 19 17:00-23:59 CST) appear in `BAR_ACCEPTED` events for trading date Jan 20
- `BAR_DATE_MISMATCH` events include:
  - `session_start_chicago`: ISO timestamp
  - `session_end_chicago`: ISO timestamp
  - `rejection_reason`: "BEFORE_SESSION_START" or "AFTER_SESSION_END"

### 4. Live Testing

**Steps:**
1. Deploy the fix to NinjaTrader
2. Start the robot strategy
3. Monitor logs in real-time:
   ```bash
   tail -f logs/robot/robot_ENGINE.jsonl | grep -E "BAR_DATE_MISMATCH|BAR_ACCEPTED"
   ```

**Expected behavior:**
- Evening session bars (17:00 CST onwards) are accepted immediately
- No `BAR_DATE_MISMATCH` events for bars within session window
- Streams transition from `PRE_HYDRATION` → `ARMED` → `RANGE_BUILDING`

## Verification Checklist

### ✅ Session Window Logic
- [ ] Session window computed correctly: `[previous_day 17:00 CST, trading_date 16:00 CST)`
- [ ] Bars from Jan 19 17:00-23:59 CST accepted for trading date Jan 20
- [ ] Bars from Jan 20 00:00-16:00 CST accepted for trading date Jan 20
- [ ] Bars before Jan 19 17:00 CST rejected with `BEFORE_SESSION_START`
- [ ] Bars after Jan 20 16:00 CST rejected with `AFTER_SESSION_END`

### ✅ Event Fields
- [ ] `BAR_DATE_MISMATCH` events include `session_start_chicago`
- [ ] `BAR_DATE_MISMATCH` events include `session_end_chicago`
- [ ] `BAR_DATE_MISMATCH` events include `rejection_reason` ("BEFORE_SESSION_START" or "AFTER_SESSION_END")
- [ ] All existing diagnostic fields preserved (`bar_utc`, `bar_chicago`, `active_trading_date`, etc.)

### ✅ Behavior Changes
- [ ] `BAR_DATE_MISMATCH` rate drops significantly (from ~100% to near 0%)
- [ ] Bar acceptance rate increases (evening session bars now accepted)
- [ ] Streams progress beyond `PRE_HYDRATION` state
- [ ] No regression in UTC→Chicago conversion logic

## Example Test Scenarios

### Scenario 1: Evening Session Bar (Should Be Accepted)
- **Trading Date**: 2026-01-20
- **Bar Time**: 2026-01-19 23:45:00 CST
- **Expected**: `BAR_ACCEPTED` (bar is within session window)

### Scenario 2: Morning Session Bar (Should Be Accepted)
- **Trading Date**: 2026-01-20
- **Bar Time**: 2026-01-20 09:30:00 CST
- **Expected**: `BAR_ACCEPTED` (bar is within session window)

### Scenario 3: Bar Before Session Start (Should Be Rejected)
- **Trading Date**: 2026-01-20
- **Bar Time**: 2026-01-19 16:59:00 CST
- **Expected**: `BAR_DATE_MISMATCH` with `rejection_reason: "BEFORE_SESSION_START"`

### Scenario 4: Bar After Session End (Should Be Rejected)
- **Trading Date**: 2026-01-20
- **Bar Time**: 2026-01-20 16:00:00 CST (or later)
- **Expected**: `BAR_DATE_MISMATCH` with `rejection_reason: "AFTER_SESSION_END"`

## Troubleshooting

### Issue: Still seeing many BAR_DATE_MISMATCH events
**Check:**
- Verify session window calculation (should be `[previous_day 17:00, trading_date 16:00)`)
- Check if `market_close_time` in spec is correct (should be "16:00")
- Verify UTC→Chicago conversion is working correctly

### Issue: Evening session bars still rejected
**Check:**
- Verify `GetSessionWindow()` method is being called
- Check that session start is previous day 17:00 CST (not same day)
- Verify bar timestamps are in correct timezone

### Issue: Missing session window fields in events
**Check:**
- Verify both `RobotEngine.cs` files were updated
- Check that `GetSessionWindow()` returns correct values
- Verify event logging includes new fields

## Success Criteria

✅ **Fix is successful if:**
1. Evening session bars (previous day 17:00-23:59 CST) are accepted
2. `BAR_DATE_MISMATCH` events only occur for bars outside session window
3. All `BAR_DATE_MISMATCH` events include session window fields
4. Streams progress beyond `PRE_HYDRATION`
5. No regression in other bar processing logic

## Related Files

- `modules/robot/core/RobotEngine.cs` - Core implementation
- `RobotCore_For_NinjaTrader/RobotEngine.cs` - NinjaTrader implementation
- `test_bar_date_mismatch_fix.py` - Test script
- `analyze_today_comprehensive_full.py` - Comprehensive log analysis
