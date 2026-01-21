# Comprehensive Robot Log Analysis - 2026-01-20

Generated: 2026-01-20 14:38:20 CST

## Executive Summary

- **Trading Date**: 2026-01-20
- **Streams Created**: 10
- **Ranges Computed**: 0
- **Ranges Committed**: 1
- **Total Errors**: 5352
- **Total Warnings**: 793

## 1. Engine Startup & Initialization

- **Engine Start**: 2026-01-20T11:08:52.0496350+00:00
- **Trading Date Locked**: 2026-01-20T11:08:52.0496350+00:00
  - Trading Date: 2026-01-20
- **Streams Created**: 2026-01-20T11:08:52.0496350+00:00
  - Stream Count: 10

## 2. BarsRequest Analysis

### CL
- Initialization Events: 0
- Skipped Events: 1
- Raw Result Events: 0
- Stream Status Events: 1
- Range Determined Events: 1
- Bars Loaded Events: 0
  - Skipped at 2026-01-20T11:08:51.7273131+00:00: Starting before range_start_time
    (Current: 05:08, Range Start: 08:00)

### NG
- Initialization Events: 0
- Skipped Events: 0
- Raw Result Events: 1
- Stream Status Events: 0
- Range Determined Events: 0
- Bars Loaded Events: 1
  - Raw result at 2026-01-20T11:08:51.5223240+00:00: 716 bars
    First bar: 2026-01-20T05:01:00.0000000+00:00, Last bar: 2026-01-20T17:09:00.0000000+00:00
  - Bars loaded at 2026-01-20T11:08:51.5334318+00:00: 182 bars, 2 streams fed

### RTY
- Initialization Events: 0
- Skipped Events: 1
- Raw Result Events: 0
- Stream Status Events: 1
- Range Determined Events: 1
- Bars Loaded Events: 0
  - Skipped at 2026-01-20T11:08:52.0878564+00:00: Starting before range_start_time
    (Current: 05:08, Range Start: 08:00)

## 3. Bar Acceptance/Rejection Analysis

| Instrument | Accepted | Rejected | Acceptance Rate |
|------------|----------|----------|------------------|
| CL | 0 | 8807 | 0.0% |
| ES | 0 | 282 | 0.0% |
| GC | 0 | 6386 | 0.0% |
| NG | 0 | 8716 | 0.0% |
| NQ | 0 | 4351 | 0.0% |
| RTY | 0 | 8228 | 0.0% |
| YM | 0 | 282 | 0.0% |

### Rejection Reasons

**CL**:
- BAR_DATE_MISMATCH: 8524
- BAR_PARTIAL_REJECTED: 283

**ES**:
- BAR_PARTIAL_REJECTED: 282

**GC**:
- BAR_DATE_MISMATCH: 6104
- BAR_PARTIAL_REJECTED: 282

**NG**:
- BAR_DATE_MISMATCH: 8433
- BAR_PARTIAL_REJECTED: 283

**NQ**:
- BAR_DATE_MISMATCH: 4068
- BAR_PARTIAL_REJECTED: 283

**RTY**:
- BAR_DATE_MISMATCH: 7945
- BAR_PARTIAL_REJECTED: 283

**YM**:
- BAR_PARTIAL_REJECTED: 282

## 4. Stream State Analysis

### CL2
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### ES1
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### ES2
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### GC1
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### GC2
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### NG1
- Total Transitions: 3
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |
| 2026-01-20T11:08:54.6978443+00:00 | PRE_HYDRATION | ARMED | From GAP_VIOLATIONS_SUMMARY |
| 2026-01-20T11:13:55.4424768+00:00 | ARMED | RANGE_BUILDING | From GAP_VIOLATIONS_SUMMARY |

### NG2
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### NQ1
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### NQ2
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

### RTY2
- Total Transitions: 1
| Timestamp | From State | To State | Reason |
|-----------|------------|----------|--------|
| 2026-01-20T11:08:51.6974135+00:00 | UNKNOWN | PRE_HYDRATION | From STREAMS_CREATED |

## 5. Range Computation Analysis

- RANGE_COMPUTE_START: 80
- RANGE_LOCK_ASSERT: 0
- RANGE_COMPUTE_FAILED: 5319

### Journal Files

| Stream | Instrument | Session | State | Range High | Range Low | Committed |
|--------|------------|---------|-------|------------|-----------|-----------|
| CL1 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| CL2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| ES1 | N/A | N/A | RANGE_LOCKED | N/A | N/A | False |
| ES2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| GC1 | N/A | N/A | RANGE_LOCKED | N/A | N/A | False |
| GC2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| NG1 | N/A | N/A | DONE | N/A | N/A | True |
| NG2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| NQ1 | N/A | N/A | RANGE_LOCKED | N/A | N/A | False |
| NQ2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| RTY1 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| RTY2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| YM1 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |
| YM2 | N/A | N/A | RANGE_BUILDING | N/A | N/A | False |

## 6. Error & Warning Analysis

### Errors (5352 total)

| Event Type | Count |
|------------|-------|
| RANGE_COMPUTE_FAILED | 5319 |
| GAP_VIOLATIONS_SUMMARY | 29 |
| GAP_TOLERANCE_VIOLATION | 4 |

### Warnings (793 total)

| Event Type | Count |
|------------|-------|
| STARTUP_TIMING_WARNING | 793 |

## 7. Timeline of Significant Events

| Timestamp | Event | Level | Details |
|-----------|------|-------|---------|
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:49.7942783+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:50.3761406+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:51.0853424+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:51.8781969+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:52.3982130+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:52.8341755+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:02:53.4080639+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:37:19.5983557+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:37:20.1380878+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:37:20.6180046+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=ES |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=GC |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NG |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=NQ |
| 2026-01-20T00:37:21.0788915+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=RTY |
| 2026-01-20T00:37:21.5874651+00:00 | STARTUP_TIMING_WARNING | WARN | instrument=CL |

*Showing first 100 of 6593 timeline events.*

## 8. Root Cause Analysis

### BarsRequest Never Executed

Instruments with NO BarsRequest events: ES, GC, NQ

### BarsRequest Skipped

Instruments with skipped BarsRequest: CL, RTY

### High Bar Rejection Rate

- **GC**: 100.0% rejection rate
- **NQ**: 100.0% rejection rate
- **NG**: 100.0% rejection rate
- **CL**: 100.0% rejection rate
- **RTY**: 100.0% rejection rate
- **ES**: 100.0% rejection rate
- **YM**: 100.0% rejection rate

### Streams Stuck in PRE_HYDRATION

- **CL2**: Stuck in PRE_HYDRATION
- **NQ2**: Stuck in PRE_HYDRATION
- **GC2**: Stuck in PRE_HYDRATION
- **NG2**: Stuck in PRE_HYDRATION
- **ES2**: Stuck in PRE_HYDRATION
- **RTY2**: Stuck in PRE_HYDRATION
- **NQ1**: Stuck in PRE_HYDRATION
- **GC1**: Stuck in PRE_HYDRATION
- **ES1**: Stuck in PRE_HYDRATION

## 9. Stream-by-Stream Summary

### CL1
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **BarsRequest**: 0 init, 1 skipped
- **Bars**: 0 accepted, 8807 rejected
- **State Transitions**: 0

### CL2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **BarsRequest**: 0 init, 1 skipped
- **Bars**: 0 accepted, 8807 rejected
- **State Transitions**: 1

### ES1
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_LOCKED
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 282 rejected
- **State Transitions**: 1

### ES2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 282 rejected
- **State Transitions**: 1

### GC1
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_LOCKED
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 6386 rejected
- **State Transitions**: 1

### GC2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 6386 rejected
- **State Transitions**: 1

### NG1
- **Instrument**: N/A
- **Session**: N/A
- **State**: DONE
- **Range**: N/A - N/A
- **Committed**: True
- **BarsRequest**: 0 init, 0 skipped
- **Bars**: 0 accepted, 8716 rejected
- **State Transitions**: 3

### NG2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **BarsRequest**: 0 init, 0 skipped
- **Bars**: 0 accepted, 8716 rejected
- **State Transitions**: 1

### NQ1
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_LOCKED
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 4351 rejected
- **State Transitions**: 1

### NQ2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 4351 rejected
- **State Transitions**: 1

### RTY1
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **BarsRequest**: 0 init, 1 skipped
- **Bars**: 0 accepted, 8228 rejected
- **State Transitions**: 0

### RTY2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **BarsRequest**: 0 init, 1 skipped
- **Bars**: 0 accepted, 8228 rejected
- **State Transitions**: 1

### YM1
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 282 rejected
- **State Transitions**: 0

### YM2
- **Instrument**: N/A
- **Session**: N/A
- **State**: RANGE_BUILDING
- **Range**: N/A - N/A
- **Committed**: False
- **Bars**: 0 accepted, 282 rejected
- **State Transitions**: 0

## 10. Recommendations

1. **Investigate missing BarsRequest**: Instruments ES, GC, NQ had no BarsRequest events. Check strategy initialization code.
2. **Review BarsRequest skip logic**: Instruments CL, RTY had BarsRequest skipped. Review timing logic in RobotSimStrategy.cs.
3. **Investigate bar rejection**: High rejection rates detected. Review bar age checks and date matching logic.
4. **Fix state transition logic**: Streams stuck in PRE_HYDRATION. Review transition conditions in StreamStateMachine.cs.
5. **No ranges computed**: System-wide failure. Review all root causes above.
