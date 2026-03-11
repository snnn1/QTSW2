# Bar Rejection Summary - Why Bars Aren't Being Accepted

## Current Situation

**Date:** January 20, 2026  
**Current Time:** 16:03 CST (after market close)  
**Trading Date:** 2026-01-20

## Session Window Definition

For trading date **Jan 20**, the session window is:
- **Start:** Jan 19 at **17:00 CST** (previous day evening)
- **End:** Jan 20 at **16:00 CST** (trading date market close)
- **Window:** `[Jan 19 17:00 CST, Jan 20 16:00 CST)`

This means bars are **ACCEPTED** if they fall within this window, **REJECTED** if outside.

## Why Bars Are Being Rejected

### 1. All Bars in Logs Are Outside Session Window

**Bars Being Rejected:**
- **Time Range:** Jan 19 11:51-12:00 CST
- **Rejection Reason:** `BEFORE_SESSION_START`
- **Count:** 15,871 BAR_DATE_MISMATCH events

**Why They're Rejected:**
- Session starts at **17:00 CST** on Jan 19
- These bars are from **11:51-12:00 CST** (about 5 hours before session start)
- They are correctly identified as outside the session window

### 2. Current Time Is After Session End

**Current Status:**
- Current time: **16:03 CST** (Jan 20)
- Session end: **16:00 CST** (Jan 20)
- **Status:** After session end - any bars received now will be rejected with `AFTER_SESSION_END`

### 3. No Bars Within Session Window Have Been Received

**What Should Be Accepted (but hasn't been received yet):**
- Bars from **Jan 19 17:00-23:59 CST** (evening session)
- Bars from **Jan 20 00:00-15:59 CST** (day session)

**What We're Seeing:**
- Only bars from **Jan 19 11:51-12:00 CST** (before session start)
- These are likely from **BarsRequest** (historical pre-hydration)
- No live bars within the session window have been received

## The Fix Is Working Correctly

✅ **Session window validation is working:**
- Bars outside the window are correctly rejected
- Rejection reasons are correct (`BEFORE_SESSION_START`)
- Session window fields are present in all BAR_DATE_MISMATCH events

✅ **No bars incorrectly rejected:**
- All rejected bars are legitimately outside the session window
- No bars within the window have been incorrectly rejected

## Why No Bars Are Accepted

**Primary Reason:** No bars within the session window have been received yet.

**Possible Explanations:**
1. **BarsRequest loaded historical bars** from before session start (11:51-12:00 CST)
2. **Strategy restarted** and hasn't received live bars yet
3. **Current time is after session end** (16:03 CST > 16:00 CST session end)
4. **No live data feed** during the session window period

## What Will Happen Next

**Tomorrow (Jan 21):**
- Session window for Jan 21: `[Jan 20 17:00 CST, Jan 21 16:00 CST)`
- Bars from **Jan 20 17:00 CST onwards** will be **ACCEPTED** for trading date Jan 21
- The fix will continue to work correctly

**If Strategy Receives Bars Within Window:**
- Bars from Jan 19 17:00-23:59 CST → **ACCEPTED**
- Bars from Jan 20 00:00-15:59 CST → **ACCEPTED**
- These will show as `BAR_ACCEPTED` events

## Conclusion

**The fix is working correctly.** Bars are being rejected because:
1. They are legitimately outside the session window (before 17:00 CST)
2. Current time is after session end (16:03 CST > 16:00 CST)
3. No bars within the session window have been received yet

**This is expected behavior** - the system is correctly rejecting bars that don't belong to the trading session.
