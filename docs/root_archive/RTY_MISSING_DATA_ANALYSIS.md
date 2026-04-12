# RTY2 Missing Data Analysis

**Date**: February 4, 2026
**Stream**: RTY2

## Missing Data Indicators

### Bar Count Analysis

**Expected Bars**: 90 bars (08:00 to 09:30 CT = 90 minutes)
**Actual Bars at Lock**: 62 bars
**Missing Bars**: **28 bars (31% missing)**

### Completeness Metrics

**At Hydration (15:13:26)**:
- Expected Bars: 73
- Loaded Bars: 57
- Completeness: **78.08%**

**After Restart (15:24:38)**:
- Expected Bars: 84
- Loaded Bars: 57
- Completeness: **67.86%**

**At Range Lock (15:30:01)**:
- Bar Count: 62 bars
- Completeness: **68.9%** (62/90)

### Gap Analysis

**Gaps Detected**:
1. **15:14:01** - Gap of 16 minutes (08:56 to 09:13)
2. **15:25:01** - Gap of 27 minutes (08:56 to 09:24)

**At Range Lock**:
- Largest Single Gap: **27 minutes**
- Total Gap Minutes: **27 minutes**

### Bar Window

**First Bar**: 08:00:00 CT
**Last Bar Before Lock**: 08:56:00 CT (from initial hydration)
**Gap**: Missing bars from 08:57 to 09:24 (27 minutes)
**Bars After Gap**: 09:24 onwards (live bars)

## Impact on Range Calculation

### Range Computed From Available Bars

**Range High**: 2674.6
**Range Low**: 2652.5

**Question**: Could missing bars affect the range?

**Answer**: **YES** - If bars are missing, especially:
- Early bars (08:00-08:56) - Could miss true low
- Late bars (08:57-09:24) - Could miss true high/low

### Missing Bar Window

**Critical Gap**: 08:57:00 to 09:13:00 CT (**17 minutes missing**)
**Additional Missing**: 09:00 to 09:12 CT (13 minutes)
**Total Missing Window**: 08:57 to 09:12 CT (**27 minutes of bars missing**)

**Bars Present**:
- ✅ 08:00 to 08:56 (57 bars)
- ❌ **08:57 to 09:12 (MISSING - 27 bars)**
- ✅ 09:13 to 09:30 (some bars present)

**This gap occurred DURING the range building window** and missing bars could contain:
  - **Lower lows** (would change range low from 2652.5)
  - **Higher highs** (would change range high from 2674.6)

### Range Calculation Method

The range is computed as:
- `RangeHigh = max(all_bar.High)` from bars in buffer
- `RangeLow = min(all_bar.Low)` from bars in buffer

**If bars are missing**, the range will only reflect the bars that were actually received.

## Conclusion

**YES, there is missing data**:
- ✅ **28 bars missing** (31% of expected)
- ✅ **27-minute gap** during range building window
- ✅ **Completeness: 68.9%** at lock

**Potential Impact**:
- ✅ **Range low (2652.5) might not be the true low** - Missing bars from 08:57-09:12 could have had lower prices (e.g., 2634)
- ✅ **Range high (2674.6) might not be the true high** - Missing bars could have had higher prices
- ✅ **The 27-minute gap (08:57-09:12) occurred during active range building** - This is when the range was being computed

**Key Finding**:
The range was computed from only **62 bars** instead of **90 bars** (31% missing). The missing bars occurred during a critical period (08:57-09:12) when price could have moved significantly.

**If bars from 08:57-09:12 had a low of 2634**, that would explain why the user expected range low to be 2634 instead of 2652.5.

**Recommendation**:
- ✅ **Missing data confirmed** - 27 bars missing during range building window
- ⚠️ **Range may be incorrect** - Range low/high computed from incomplete data
- 🔍 **Check historical data** - Verify if bars from 08:57-09:12 exist in NinjaTrader historical data
- 🔧 **Investigate data feed** - Determine why bars were not received during this window
