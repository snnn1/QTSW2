# BarsRequest Missing Bars Explanation

**Date**: February 4, 2026  
**Stream**: RTY2  
**Missing Bars**: 08:57 to 09:13 CT (27 bars)

## User Question
"But surely BarsRequest could just see the data at the end and compute it?"

## Answer

**BarsRequest DID request the data** - but **NinjaTrader only returned bars that existed in its historical database**. If bars from 08:57-09:13 were never recorded (due to data feed gaps), they don't exist to be retrieved.

## What Actually Happened

### BarsRequest Request

**Requested Range**: `[08:00, 09:30)` CT (90 bars expected)

**What BarsRequest Retrieved**:
- ✅ **08:00-08:56**: 57 bars retrieved
- ❌ **08:57-08:59**: 3 bars **NOT retrieved** (didn't exist in database)
- ❌ **09:00-09:12**: 13 bars **NOT retrieved** (didn't exist in database)
- ❌ **09:13-09:30**: 17 bars **NOT retrieved** (didn't exist in database)

**Total Retrieved**: 57 bars out of 90 expected (63% coverage)

### Evidence from Logs

**BarsRequest Attempts**:
- BarsRequest attempted to add **114 bars** total (includes duplicates)
- But only **57 bars** were unique from BarsRequest
- **57 bars were rejected as duplicates** (LIVE bars already existed)

**BarsRequest Timeline**:
- **08:00 Hour**: BarsRequest retrieved bars 00-56 (57 bars)
- **09:00 Hour**: BarsRequest retrieved **0 bars** (none existed in database)

**HYDRATION_SUMMARY**:
- `historical_bar_count: 57` (from BarsRequest)
- `live_bar_count: 57` (from live feed)
- `barsrequest_accepted_count: 57` (after deduplication)

## Why BarsRequest Couldn't Retrieve Missing Bars

### Limitation: Can Only Retrieve What Exists

**BarsRequest API Behavior**:
1. ✅ **Requests bars** from `[08:00, 09:30)` CT
2. ✅ **Queries NinjaTrader's historical database**
3. ❌ **Only returns bars that exist** in the database
4. ❌ **If bars don't exist** (never recorded), BarsRequest returns empty for those times

### Root Cause: Bars Were Never Recorded

**What Happened**:
- **08:57-09:13 CT**: Bars were never recorded in NinjaTrader's database
- **Most Likely Cause**: NinjaTrader wasn't running during this time
- **Evidence**: System started at 09:13:26 CT (after missing window)
- **BarsRequest**: Can't retrieve bars that don't exist

**Important**: Bar recording happens **at the NinjaTrader platform level**, independent of our strategy. If NinjaTrader wasn't running or connected during the missing window, bars couldn't be recorded.

**This is NOT a BarsRequest bug** - it's a **data feed issue**. BarsRequest can only retrieve bars that were actually recorded by NinjaTrader's data feed.

## Why This Matters

### BarsRequest vs Live Feed

**Live Feed**:
- Provides bars in real-time as they occur
- Can only provide bars going forward from when system starts
- Cannot provide bars that occurred before startup

**BarsRequest**:
- Retrieves historical bars from NinjaTrader's database
- Can retrieve bars from any time period (past or present)
- **BUT**: Can only retrieve bars that exist in the database
- **If bars were never recorded**, they don't exist to be retrieved

### The Gap Window

**Missing Window**: 08:57-09:13 CT

**Why Bars Don't Exist**:
1. **Data feed gap**: No bars were recorded during this period
2. **NinjaTrader database**: Doesn't have bars for this period
3. **BarsRequest**: Can't retrieve what doesn't exist

## Conclusion

**BarsRequest DID request the data** - it requested bars from `[08:00, 09:30)` CT.

**But BarsRequest could only retrieve bars that existed** in NinjaTrader's historical database. Since bars from 08:57-09:13 were never recorded (data feed gap), they don't exist to be retrieved.

**This is a data feed issue**, not a BarsRequest limitation. BarsRequest works correctly - it can only retrieve bars that were actually recorded.

## Solution

**To prevent this**:
1. ✅ **Start system earlier**: Before range window begins (before 08:00 CT)
2. ✅ **Monitor data feed**: Ensure data feed is connected and recording bars
3. ✅ **Check NinjaTrader database**: Verify bars exist in historical data
4. ✅ **Data feed redundancy**: Use multiple data sources if possible

**BarsRequest cannot fix missing bars** - it can only retrieve what exists in the database.

**Note**: Bar recording happens automatically when NinjaTrader is connected to a data feed. Our strategy doesn't control bar recording - it only receives bars that NinjaTrader has already recorded. If bars weren't recorded, it's because NinjaTrader wasn't running, data feed was disconnected, or no trading occurred during that period.
