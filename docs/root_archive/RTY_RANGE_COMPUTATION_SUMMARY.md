# RTY2 Range Computation Summary

**Date**: February 4, 2026
**Stream**: RTY2

## Range Values Throughout the Day

### All Range Computations Found

1. **15:13:26 UTC (09:13 CT)** - HYDRATION_SUMMARY
   - Range High: **2674.6**
   - Range Low: **2652.5**
   - Range Size: 22.10 points

2. **15:24:38 UTC (09:24 CT)** - HYDRATION_SUMMARY (after restart)
   - Range High: **2674.6**
   - Range Low: **2652.5**
   - Range Size: 22.10 points

3. **15:30:01 UTC (09:30 CT)** - RANGE_LOCK_VALIDATION_PASSED
   - Range High: **2674.6**
   - Range Low: **2652.5**
   - Range Size: 22.10 points

4. **15:30:01 UTC (09:30 CT)** - SLOT_END_SUMMARY
   - Range High: **2674.6**
   - Range Low: **2652.5**
   - Range Size: 22.10 points

## Conclusion

**The range was consistently computed as:**
- **Range High**: 2674.6
- **Range Low**: 2652.5
- **Range Size**: 22.1 points

**No other range values found** - the range remained constant throughout the day.

## Range Window

- **Range Start**: 08:00:00 CT
- **Slot Time**: 09:30:00 CT
- **Bar Count**: 62 bars (at lock)
- **First Bar**: 08:00:00 CT
- **Last Bar**: 09:28:00 CT

## Note About 2634

The value **2634** does not appear anywhere in the RTY logs. The range low was consistently **2652.5**, not 2634.

If you're seeing 2634 somewhere, it might be:
- From a different stream (NQ1, YM1, etc.)
- From a different trading day
- From a different data source (watchdog, analyzer, etc.)
