# Range Computation Summary - January 6, 2026

## Overview

**Total Attempts:** 72  
**Successful:** 4  
**Failed:** 68  
**Success Rate:** 5.6%

---

## ✅ Successful Range Computations

### 1. GC (Gold) - Session S1, Slot 09:00
- **Time:** 2026-01-06T15:00:06 UTC (09:00 Chicago)
- **Range High:** 4486.0
- **Range Low:** 4458.1
- **Range Size:** 27.9 points
- **Bar Count:** 420 bars
- **Freeze Close:** 4479.6 (Source: BAR_CLOSE)
- **Range Window (Chicago):** 02:00:00 to 09:00:00
- **Range Window (UTC):** 08:00:00 to 15:00:00
- **Duration:** 6.51 ms

### 2. NG (Natural Gas) - Session S1, Slot 07:30
- **Time:** 2026-01-06T13:30:06 UTC (07:30 Chicago)
- **Range High:** 3.48
- **Range Low:** 3.418
- **Range Size:** 0.062 points
- **Bar Count:** 289 bars
- **Freeze Close:** 3.43 (Source: BAR_CLOSE)
- **Range Window (Chicago):** 02:00:00 to 07:30:00
- **Range Window (UTC):** 08:00:00 to 13:30:00
- **Duration:** 10.76 ms

### 3. NQ (E-mini NASDAQ) - Session S1, Slot 09:00
- **Time:** 2026-01-06T15:00:06 UTC (09:00 Chicago)
- **Range High:** 25674.25
- **Range Low:** 25527.5
- **Range Size:** 146.75 points
- **Bar Count:** 419 bars
- **Freeze Close:** 25574.75 (Source: BAR_CLOSE)
- **Range Window (Chicago):** 02:00:00 to 09:00:00
- **Range Window (UTC):** 08:00:00 to 15:00:00
- **Duration:** 3.00 ms

### 4. YM (E-mini Dow) - Session S1, Slot 09:00
- **Time:** 2026-01-06T15:00:06 UTC (09:00 Chicago)
- **Range High:** 49270.0
- **Range Low:** 49103.0
- **Range Size:** 167.0 points
- **Bar Count:** 412 bars
- **Freeze Close:** 49127.0 (Source: BAR_CLOSE)
- **Range Window (Chicago):** 02:00:00 to 09:00:00
- **Range Window (UTC):** 08:00:00 to 15:00:00
- **Duration:** 2.00 ms

---

## ❌ Failed Range Computations

### ES (E-mini S&P 500)
- **Attempts:** 15
- **Slots:** 11:00 (S2 session)
- **Reason:** NO_BARS_IN_WINDOW (bar_count: 0)
- **Range Window:** 08:00-11:00 Chicago (14:00-17:00 UTC)

### CL (Crude Oil)
- **Attempts:** 8
- **Slots:** 07:30 (S1), 10:30 (S2)
- **Reason:** NO_BARS_IN_WINDOW (bar_count: 0)
- **Range Windows:** 
  - S1: 08:00-07:30 Chicago (08:00-13:30 UTC)
  - S2: 08:00-10:30 Chicago (14:00-16:30 UTC)

### RTY (E-mini Russell 2000)
- **Attempts:** 7
- **Slots:** 09:30 (S2 session)
- **Reason:** NO_BARS_IN_WINDOW (bar_count: 0)
- **Range Window:** 08:00-09:30 Chicago (14:00-15:30 UTC)

---

## Observations

### Success Pattern
- ✅ **All successful ranges:** Session S1, slots 07:30 or 09:00
- ✅ **All successful ranges:** Morning session (02:00-09:00 Chicago)
- ✅ **All successful ranges:** Had 289-420 bars in buffer

### Failure Pattern
- ❌ **All failed ranges:** Session S2 (afternoon session)
- ❌ **All failed ranges:** Slots 10:30, 11:00, 09:30
- ❌ **All failed ranges:** Bar buffer empty (0 bars)

### Key Insight
**Morning session (S1) ranges computed successfully, but afternoon session (S2) ranges all failed.**

This suggests:
1. Bars are being received during morning hours
2. Bars are NOT being received during afternoon hours, OR
3. Bars are being filtered out incorrectly for afternoon session

---

## Summary Table

| Instrument | Session | Slot | Status | Range High | Range Low | Range Size | Bars |
|------------|---------|------|--------|------------|-----------|------------|------|
| GC | S1 | 09:00 | ✅ Success | 4486.0 | 4458.1 | 27.9 | 420 |
| NG | S1 | 07:30 | ✅ Success | 3.48 | 3.418 | 0.062 | 289 |
| NQ | S1 | 09:00 | ✅ Success | 25674.25 | 25527.5 | 146.75 | 419 |
| YM | S1 | 09:00 | ✅ Success | 49270.0 | 49103.0 | 167.0 | 412 |
| ES | S2 | 11:00 | ❌ Failed | - | - | - | 0 |
| CL | S1 | 07:30 | ❌ Failed | - | - | - | 0 |
| CL | S2 | 10:30 | ❌ Failed | - | - | - | 0 |
| RTY | S2 | 09:30 | ❌ Failed | - | - | - | 0 |

---

## Next Steps

1. **Investigate why S2 session bars are not being captured**
2. **Verify bar reception during afternoon hours**
3. **Check if Chicago time filtering is excluding afternoon bars**
4. **Review timing of `Tick()` calls relative to bar arrival**
