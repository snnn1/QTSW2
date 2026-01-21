# Range Building Logging Enhancements

## Overview

This document proposes additional logging to make the range building process more readable and debuggable while it's running. Current logging is comprehensive but could benefit from more frequent progress updates and clearer visual indicators.

## Current Logging Gaps

### 1. **Bar Arrival During RANGE_BUILDING**
**Issue**: No per-bar logs when bars arrive during range building.

**Current Behavior**: 
- Bars are silently added to buffer
- Only aggregated in `RANGE_COMPUTE_COMPLETE`

**Proposed Enhancement**:
- Rate-limited bar arrival logs (e.g., every 10 bars or every 30 seconds)
- Show: bar count, time remaining until slot, current range high/low if computed

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_BAR_ARRIVAL",
  "data": {
    "bar_count": 45,
    "bars_remaining_estimate": 45,
    "time_until_slot_minutes": 45.2,
    "current_range_high": 2345.50,
    "current_range_low": 2340.25,
    "range_size": 5.25,
    "last_bar_time_chicago": "08:14:30",
    "progress_percent": 50.0
  }
}
```

---

### 2. **Progress Toward Slot Time**
**Issue**: No clear indication of progress through the range building window.

**Current Behavior**:
- Only `SLOT_GATE_DIAGNOSTIC` logs (diagnostic only, rate-limited)
- No percentage or time remaining indicators

**Proposed Enhancement**:
- Periodic progress logs (every 5-10 minutes or at key milestones)
- Show: time elapsed, time remaining, percentage complete, bar count

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_PROGRESS",
  "data": {
    "time_elapsed_minutes": 45.0,
    "time_remaining_minutes": 45.0,
    "progress_percent": 50.0,
    "bar_count": 45,
    "expected_bar_count": 90,
    "range_start_chicago": "07:30:00",
    "slot_time_chicago": "09:00:00",
    "current_time_chicago": "08:15:00"
  }
}
```

---

### 3. **Incremental Range Updates**
**Issue**: When range is computed incrementally, updates aren't logged.

**Current Behavior**:
- `RANGE_INITIALIZED_FROM_HISTORY` logged once
- No updates when range high/low changes from new bars

**Proposed Enhancement**:
- Log when range high/low changes significantly (e.g., > 0.5 points)
- Show: old high/low, new high/low, bar that caused change

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_RANGE_UPDATE",
  "data": {
    "previous_high": 2345.50,
    "new_high": 2346.25,
    "previous_low": 2340.25,
    "new_low": 2340.00,
    "update_reason": "New bar extended range high",
    "triggering_bar_time_chicago": "08:20:00",
    "bar_count": 50
  }
}
```

---

### 4. **Bar Filtering Details**
**Issue**: Bar filtering is logged but not easily visible.

**Current Behavior**:
- `RANGE_COMPUTE_BAR_FILTERING` logged (diagnostic only, rate-limited)
- Only shows counts, not examples

**Proposed Enhancement**:
- More frequent filtering logs with examples
- Show: sample filtered bars with reasons

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_BAR_FILTERED",
  "data": {
    "bar_time_chicago": "06:30:00",
    "filter_reason": "Before range start",
    "range_start_chicago": "07:30:00",
    "total_filtered_count": 5,
    "total_accepted_count": 45
  }
}
```

---

### 5. **Gap Tracking Progress**
**Issue**: Gap violations are logged, but gap accumulation isn't visible.

**Current Behavior**:
- `GAP_TOLERANCE_VIOLATION` logged when threshold exceeded
- `GAP_TOLERATED` logged for gaps within tolerance (WARN level)
- No running total visible

**Proposed Enhancement**:
- Periodic gap status logs (every 10 minutes or when gap detected)
- Show: current gap totals, remaining tolerance, risk level

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_GAP_STATUS",
  "data": {
    "largest_single_gap_minutes": 2.5,
    "total_gap_minutes": 4.2,
    "max_single_gap_allowed": 3.0,
    "max_total_gap_allowed": 6.0,
    "gap_status": "WITHIN_TOLERANCE",
    "risk_level": "LOW",
    "bars_since_last_gap": 15
  }
}
```

---

### 6. **Time Milestone Logs**
**Issue**: No logs at key time milestones (e.g., halfway point, 10 minutes remaining).

**Current Behavior**:
- Only logs at slot time

**Proposed Enhancement**:
- Log at milestones: 25%, 50%, 75%, 90% complete, 10 min remaining, 5 min remaining

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_MILESTONE",
  "data": {
    "milestone": "50_PERCENT_COMPLETE",
    "time_elapsed_minutes": 45.0,
    "time_remaining_minutes": 45.0,
    "bar_count": 45,
    "range_status": "BUILDING",
    "gap_status": "HEALTHY"
  }
}
```

---

### 7. **Range Computation Attempts**
**Issue**: Range computation attempts aren't logged until success/failure.

**Current Behavior**:
- Only logs `RANGE_COMPUTE_COMPLETE` or `RANGE_COMPUTE_FAILED`
- No indication that computation is being attempted

**Proposed Enhancement**:
- Log when computation starts (especially at slot time)
- Show: bar count, expected result, computation type (initial/incremental/final)

**Example Log**:
```json
{
  "event": "RANGE_COMPUTE_START",
  "data": {
    "computation_type": "FINAL",
    "bar_count": 90,
    "expected_bar_count": 90,
    "range_start_chicago": "07:30:00",
    "range_end_chicago": "09:00:00",
    "computation_time_utc": "2026-01-20T14:00:00Z"
  }
}
```

---

### 8. **State Transition Visibility**
**Issue**: State transitions are logged but could be more prominent.

**Current Behavior**:
- `STREAM_STATE_TRANSITION` logged
- Contains all details but could be clearer

**Proposed Enhancement**:
- Add summary log with key info highlighted
- Show: old state → new state, reason, time, next expected state

**Example Log**:
```json
{
  "event": "RANGE_BUILDING_STATE_CHANGE",
  "data": {
    "from_state": "ARMED",
    "to_state": "RANGE_BUILDING",
    "reason": "Range window started",
    "transition_time_chicago": "07:30:00",
    "next_expected_state": "RANGE_LOCKED",
    "expected_transition_time_chicago": "09:00:00"
  }
}
```

---

## Implementation Recommendations

### Priority 1 (High Value, Easy to Add):
1. **Progress logs** - Every 5-10 minutes showing time remaining
2. **Milestone logs** - At 25%, 50%, 75%, 90% complete
3. **Range computation start** - When computation begins

### Priority 2 (High Value, Moderate Effort):
4. **Bar arrival logs** - Rate-limited (every 10 bars or 30 seconds)
5. **Gap status logs** - Periodic gap tracking updates
6. **Range update logs** - When range high/low changes significantly

### Priority 3 (Nice to Have):
7. **Bar filtering examples** - Sample filtered bars with reasons
8. **State transition summaries** - More prominent transition logs

---

## Rate Limiting Strategy

To prevent log spam while maintaining visibility:

- **Progress logs**: Every 5-10 minutes
- **Bar arrival logs**: Every 10 bars OR every 30 seconds (whichever comes first)
- **Gap status logs**: Every 10 minutes OR when gap detected
- **Range update logs**: Only when change > threshold (e.g., 0.5 points)
- **Milestone logs**: Once per milestone (25%, 50%, 75%, 90%, 10 min remaining, 5 min remaining)

---

## Example Enhanced Log Flow

```
07:30:00 - RANGE_BUILDING_STATE_CHANGE: ARMED → RANGE_BUILDING
07:30:00 - RANGE_COMPUTE_START: Initial computation from history
07:30:01 - RANGE_INITIALIZED_FROM_HISTORY: High=2345.50, Low=2340.25, Bars=30
07:35:00 - RANGE_BUILDING_PROGRESS: 5 min elapsed, 85 min remaining, 5.6% complete
07:40:00 - RANGE_BUILDING_BAR_ARRIVAL: Bar count=35, Range stable
07:45:00 - RANGE_BUILDING_MILESTONE: 25% complete
07:50:00 - RANGE_BUILDING_PROGRESS: 20 min elapsed, 70 min remaining, 22.2% complete
08:00:00 - RANGE_BUILDING_MILESTONE: 50% complete
08:00:00 - RANGE_BUILDING_GAP_STATUS: Total gaps=2.1 min, Status=HEALTHY
08:15:00 - RANGE_BUILDING_RANGE_UPDATE: High updated 2345.50 → 2346.25
08:30:00 - RANGE_BUILDING_MILESTONE: 75% complete
08:45:00 - RANGE_BUILDING_PROGRESS: 75 min elapsed, 15 min remaining, 83.3% complete
08:50:00 - RANGE_BUILDING_MILESTONE: 90% complete, 10 minutes remaining
08:55:00 - RANGE_BUILDING_MILESTONE: 5 minutes remaining
09:00:00 - RANGE_COMPUTE_START: Final computation, Bar count=90
09:00:01 - RANGE_COMPUTE_COMPLETE: High=2346.25, Low=2340.00, Bars=90
09:00:01 - RANGE_BUILDING_STATE_CHANGE: RANGE_BUILDING → RANGE_LOCKED
```

---

## Code Locations for Implementation

### 1. Progress Logs
**Location**: `HandleRangeBuildingState()` method
**When**: Every 5-10 minutes (use `_lastHeartbeatUtc` or new `_lastProgressLogUtc`)

### 2. Bar Arrival Logs
**Location**: `OnBar()` method, when `State == RANGE_BUILDING`
**When**: Rate-limited (every 10 bars or 30 seconds)

### 3. Range Update Logs
**Location**: `HandleArmedState()` and `HandleRangeBuildingState()` after incremental computation
**When**: When `RangeHigh` or `RangeLow` changes significantly

### 4. Milestone Logs
**Location**: `HandleRangeBuildingState()` method
**When**: Calculate progress percentage, log at milestones

### 5. Gap Status Logs
**Location**: `OnBar()` method, after gap tracking
**When**: Every 10 minutes or when gap detected

### 6. Range Compute Start
**Location**: `HandleRangeBuildingState()` before calling `ComputeRangeRetrospectively()`
**When**: Every time computation starts

---

## Configuration Options

Add to `configs/robot/logging.json`:

```json
{
  "enable_diagnostic_logs": true,
  "range_building_logging": {
    "enable_progress_logs": true,
    "progress_log_interval_minutes": 5,
    "enable_bar_arrival_logs": true,
    "bar_arrival_log_rate_limit_seconds": 30,
    "bar_arrival_log_rate_limit_count": 10,
    "enable_range_update_logs": true,
    "range_update_threshold_points": 0.5,
    "enable_milestone_logs": true,
    "enable_gap_status_logs": true,
    "gap_status_log_interval_minutes": 10
  }
}
```

---

## Benefits

1. **Real-time Visibility**: See progress as it happens
2. **Easier Debugging**: Know exactly when/why things happen
3. **Performance Monitoring**: Track bar arrival rates, computation times
4. **Issue Detection**: Spot problems early (gaps, missing bars, etc.)
5. **User Confidence**: Clear indication that system is working

---

## Considerations

- **Log Volume**: Rate limiting prevents excessive logs
- **Performance**: Logging overhead is minimal (JSON serialization)
- **Diagnostic Mode**: Some logs only enabled when `enable_diagnostic_logs = true`
- **Backward Compatibility**: New logs don't affect existing functionality
