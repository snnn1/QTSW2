# Enabled Streams Status Report - Today

**Date**: 2026-01-30  
**Analysis Time**: 11:25 UTC  
**Timetable Source**: `data/timetable/timetable_current.json`  
**Total Events Analyzed**: 212,982

---

## Executive Summary

✅ **All enabled streams are active and functioning correctly**

- **Total Enabled Streams**: 7
- **Active**: 7 (100%)
- **Stale**: 0
- **Inactive**: 0
- **No Events**: 0
- **Streams with Issues**: 0

---

## Enabled Streams Status

### 1. ✅ ES1
- **Status**: ACTIVE
- **Current State**: RANGE_BUILDING
- **Latest Event**: BAR_BUFFER_ADD_COMMITTED (0.1 min ago)
- **Total Events**: 2,141
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ❌ No (building range)
- **BarsRequest**: ✅ Not waiting
- **Issues**: None

### 2. ✅ GC2 (MGC execution instrument)
- **Status**: ACTIVE
- **Current State**: RANGE_LOCKED
- **Latest Event**: BAR_ADMISSION_PROOF (0.1 min ago)
- **Total Events**: 18,991
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ✅ Yes
- **BarsRequest**: ⚠️ Waiting (but functioning normally)
- **Execution Events**: 1,695
- **Issues**: None

### 3. ✅ NG1
- **Status**: ACTIVE
- **Current State**: RANGE_BUILDING
- **Latest Event**: BAR_BUFFER_ADD_COMMITTED (0.6 min ago)
- **Total Events**: 1,935
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ❌ No (building range)
- **BarsRequest**: ✅ Not waiting
- **Issues**: None

### 4. ✅ NG2
- **Status**: ACTIVE
- **Current State**: RANGE_LOCKED
- **Latest Event**: BAR_ADMISSION_PROOF (0.6 min ago)
- **Total Events**: 18,542
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ✅ Yes
- **BarsRequest**: ✅ Not waiting
- **Execution Events**: 6,487
- **Issues**: None

### 5. ✅ NQ2
- **Status**: ACTIVE
- **Current State**: RANGE_LOCKED
- **Latest Event**: BAR_ADMISSION_PROOF (0.1 min ago)
- **Total Events**: 29,211
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ✅ Yes
- **BarsRequest**: ⚠️ Waiting (but functioning normally)
- **Execution Events**: 1,889
- **Issues**: None

### 6. ✅ YM1
- **Status**: ACTIVE
- **Current State**: RANGE_BUILDING
- **Latest Event**: BAR_BUFFER_ADD_COMMITTED (0.1 min ago)
- **Total Events**: 2,141
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ❌ No (building range)
- **BarsRequest**: ✅ Not waiting
- **Issues**: None

### 7. ✅ YM2
- **Status**: ACTIVE
- **Current State**: RANGE_LOCKED
- **Latest Event**: BAR_ADMISSION_PROOF (0.1 min ago)
- **Total Events**: 30,384
- **Pre-Hydration**: ✅ Complete
- **Armed**: ✅ Yes
- **Range Locked**: ✅ Yes
- **BarsRequest**: ✅ Not waiting
- **Execution Events**: 6,027
- **Issues**: None

---

## Streams by State

- **RANGE_LOCKED**: 4 streams (GC2, NG2, NQ2, YM2)
- **RANGE_BUILDING**: 3 streams (ES1, NG1, YM1)

**Note**: Streams in RANGE_BUILDING are actively building their trading ranges. This is normal operation before range locks.

---

## Key Observations

### ✅ All Streams Healthy
- All 7 enabled streams are active and processing bars
- No stuck states detected
- All streams have completed pre-hydration
- All streams are ARMED

### ⚠️ BarsRequest Status
- **GC2** and **NQ2** show "waiting" for BarsRequest
- However, both streams are functioning normally:
  - GC2: RANGE_LOCKED, processing bars, executing orders
  - NQ2: RANGE_LOCKED, processing bars, executing orders
- **Conclusion**: Streams can operate on live feed while BarsRequest completes in background

### 📊 Activity Levels
- **Highest Activity**: YM2 (30,384 events), NQ2 (29,211 events)
- **Moderate Activity**: GC2 (18,991 events), NG2 (18,542 events)
- **Lower Activity**: ES1, NG1, YM1 (~2,000 events each)

**Note**: Activity levels vary based on:
- Bar frequency for each instrument
- Range building vs. range locked state
- Execution activity

### 🎯 Execution Activity
- **GC2**: 1,695 execution events
- **NG2**: 6,487 execution events
- **NQ2**: 1,889 execution events
- **YM2**: 6,027 execution events
- **ES1, NG1, YM1**: 0 execution events (in RANGE_BUILDING, not yet trading)

---

## Timetable Information

- **Trading Date**: 2026-01-30
- **Timetable Generated**: 2026-01-30T05:24:58 (Chicago time)
- **Enabled Streams**: 7

---

## Recommendations

### ✅ No Action Required
- All streams are functioning correctly
- No issues detected
- System is healthy

### 📊 Monitoring
- Continue monitoring stream activity
- Watch for any streams transitioning to INACTIVE status
- Monitor BarsRequest completion for GC2 and NQ2 (though not critical)

### 🔄 Next Steps
- Streams in RANGE_BUILDING will transition to RANGE_LOCKED when ranges are established
- Execution will begin when:
  - Range is locked
  - Trading conditions are met
  - RiskGate allows execution

---

## Diagnostic Script

**Script**: `check_enabled_streams_today.py`

**Usage**:
```bash
python check_enabled_streams_today.py
```

**Output**: Comprehensive status report for all enabled streams, including:
- Current state
- Activity status
- Hydration status
- BarsRequest status
- Event counts
- Issues (if any)

---

## Conclusion

✅ **All enabled streams are healthy and functioning correctly.**

- 7/7 streams active (100%)
- All streams have completed pre-hydration
- All streams are ARMED
- 4 streams have locked ranges and are ready for trading
- 3 streams are building ranges (normal operation)
- No issues detected

**System Status**: 🟢 **HEALTHY**
