# RTY Stop Price Calculation Explanation

**Date**: February 4, 2026
**Stream**: RTY2
**Question**: Why was stop placed at 2652.4 when range low was 2634?

## Actual Values from Logs

### Range Values
- **Range Low**: 2652.5 (not 2634)
- **Range High**: 2674.6
- **Range Size**: 22.1 points

### Short Trade Values
- **Entry Price**: 2652.4 (breakout short level)
- **Stop Price**: 2674.5 (protective stop)
- **Target Price**: 2642.4

### Long Trade Values
- **Entry Price**: 2674.7 (breakout long level)
- **Stop Price**: 2652.6 (protective stop)
- **Target Price**: 2684.7

## Stop Price Calculation Formula

**Formula**: `Stop Loss = min(range_size, 3 * target_pts)` in points

**For RTY**:
- Base target: 10.0 points
- Max stop points: 3 × 10.0 = 30.0 points
- Range size: 22.1 points
- **Stop points used**: min(22.1, 30.0) = **22.1 points** (range size was smaller)

**For Short Trade**:
- Entry: 2652.4
- Stop points: 22.1
- **Stop price**: Entry + Stop Points = 2652.4 + 22.1 = **2674.5** ✅

**For Long Trade**:
- Entry: 2674.7
- Stop points: 22.1
- **Stop price**: Entry - Stop Points = 2674.7 - 22.1 = **2652.6** ✅

## Clarification

**2652.4 is the ENTRY price, not the stop price.**

- **Entry Price (2652.4)**: Breakout short level = Range Low (2652.5) - Tick Size (0.1)
- **Stop Price (2674.5)**: Protective stop = Entry (2652.4) + Range Size (22.1)

## Why Range Size Was Used

The stop loss formula uses the **minimum** of:
1. Range size (22.1 points)
2. 3 × target points (30.0 points)

Since range size (22.1) < 3 × target (30.0), **range size was used**.

This means:
- Stop is placed **one full range size** away from entry
- This protects against the range being invalidated (price moving back through the range)

## If Range Low Was 2634

If range low was actually 2634 (not 2652.5), then:
- Range size would be: 2674.6 - 2634 = 40.6 points
- Max stop: 3 × 10.0 = 30.0 points
- **Stop points**: min(40.6, 30.0) = **30.0 points** (3 × target would be used)
- Short stop: Entry + 30.0 = 2652.4 + 30.0 = 2682.4

But the logs show range low was **2652.5**, not 2634.

## Conclusion

The stop price calculation is **correct**:
- Stop is calculated from **entry price**, not range low
- Stop distance = min(range_size, 3 × target_pts)
- For this trade: Stop = Entry (2652.4) + Range Size (22.1) = **2674.5**

**2652.4 is the entry price** (breakout short level), not the stop price.
