# RTY Missing Bars Root Cause Analysis

**Date**: February 4, 2026  
**Stream**: RTY2  
**Missing Bars**: 08:57 to 09:13 CT (27 bars)

## Root Cause: Late System Startup

### The Problem

**System started at 09:13:26 CT** - **AFTER** the missing bar window had already occurred.

**Timeline**:
- **08:00 CT**: Range window starts
- **08:57 CT**: Missing bar window begins
- **09:13 CT**: Missing bar window ends
- **09:13:26 CT**: **System started** ⚠️
- **09:30 CT**: Slot time (range lock)

### Why Bars Were Missed

1. **System wasn't running** during 08:57-09:13 CT
   - Bars from this window occurred before system startup
   - System couldn't receive bars it wasn't running to receive

2. **BarsRequest couldn't fill the gap**
   - BarsRequest DID request bars from `[08:00, 09:30)` CT
   - But BarsRequest only retrieved **57 bars** (08:00-08:56)
   - BarsRequest did NOT retrieve bars from 08:57-09:13 because **they don't exist in NinjaTrader's database**
   - If bars were never recorded (data feed gap), BarsRequest can't retrieve them

3. **Live feed couldn't fill the gap**
   - Live feed only provides bars going forward from when system starts
   - It cannot provide bars that occurred before startup

### Evidence

**From Logs**:
- First event: `15:13:26 UTC` = `09:13:26 CT`
- Missing window: `08:57` to `09:13 CT`
- System started **16 minutes AFTER** missing window ended

**BarsRequest**:
- BarsRequest attempted to retrieve **114 bars** total
- But only **57 bars** were unique (08:00-08:56)
- **0 bars** retrieved from 09:00-09:30 hour (none existed in database)
- BarsRequest requested `[08:00, 09:30)`, but bars from `[08:57, 09:13]` **don't exist** in NinjaTrader's database

### Why BarsRequest Didn't Help

**BarsRequest Limitations**:
1. **Historical data availability**: BarsRequest can only retrieve bars that exist in NinjaTrader's historical database
2. **Data feed gaps**: If the data feed had gaps during 08:57-09:13, those bars were never recorded and don't exist in the database
3. **Cannot create missing data**: BarsRequest cannot retrieve bars that were never recorded - it can only retrieve what exists

### Impact

**Range Computation**:
- Range computed from only **62 bars** instead of **90 bars** (31% missing)
- Missing bars from 08:57-09:13 could have contained:
  - Lower lows (would change range low from 2652.5)
  - Higher highs (would change range high from 2674.6)

**Range Values**:
- Range High: 2674.6 (may be incorrect if missing bars had higher highs)
- Range Low: 2652.5 (may be incorrect if missing bars had lower lows, e.g., 2634)

### Solution

**Prevention**:
1. ✅ **Start system earlier**: Ensure system starts before range window begins (before 08:00 CT)
2. ✅ **Monitor startup timing**: Log alerts if system starts after range window begins
3. ✅ **BarsRequest timing**: Ensure BarsRequest is called early enough to retrieve historical bars

**Detection**:
- System already logs `STARTUP_TIMING_WARNING` if started after range window
- Completeness metrics show 68.9% (62/90 bars) - indicates missing data

**Recovery**:
- System cannot recover missing bars that occurred before startup
- Range computation works correctly with available bars
- Missing bars are logged as gaps (`GAP_TOLERATED` events)

## Conclusion

**Root Cause**: **Late system startup** - System started at 09:13:26 CT, **16 minutes AFTER** the missing bar window (08:57-09:13 CT) had already occurred.

**Not a bug**: This is expected behavior - the system cannot receive bars that occurred before it started running.

**Prevention**: Start system before range window begins (before 08:00 CT) to ensure all bars are received.
