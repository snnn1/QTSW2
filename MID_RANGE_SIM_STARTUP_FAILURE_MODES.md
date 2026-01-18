# Mid-Range SIM Startup Failure Modes Analysis

## Context
Starting SIM strategy mid-range means starting after `range_start` (02:00 for S1, 08:00 for S2) but before `slot_time`. The system uses "RESTART_FULL_RECONSTRUCTION" policy - it requests historical bars and reconstructs the range.

## Critical Failure Modes

### 1. BarsRequest Returns Zero Bars
**Location**: `modules/robot/core/RobotEngine.cs:682-701`, `modules/robot/ninjatrader/RobotSimStrategy.cs:328-350`

**Causes**:
- NinjaTrader "Days to load" setting too low (default may be 14 days, but needs today's data)
- No historical data available for today's date
- Wrong trading hours template configured
- Data provider connection issues
- Instrument not configured correctly

**Symptoms**:
- `BARSREQUEST_ZERO_BARS_DIAGNOSTIC` log event
- `PRE_HYDRATION_TIMEOUT_NO_BARS` log event (SIM mode)
- Range computation relies on live bars only - may be incomplete

**Impact**: Range may be incorrect if insufficient live bars arrive before slot_time

**Detection**: Check logs for `BARSREQUEST_ZERO_BARS_DIAGNOSTIC` event

---

### 2. All Bars Filtered Out (Too Recent)
**Location**: `modules/robot/core/RobotEngine.cs:610-647`

**Causes**:
- Starting very close to "now" (within 1 minute)
- BarsRequest returns bars that are too recent (partial/in-progress)
- System filters bars < 1 minute old to prevent partial bar contamination

**Symptoms**:
- `BARSREQUEST_FILTER_SUMMARY` shows `accepted_bar_count = 0`
- `filtered_partial_count > 0` in logs
- `PRE_HYDRATION_NO_BARS_AFTER_FILTER` event

**Impact**: No historical bars loaded, range computed from live bars only

**Detection**: Check `BARSREQUEST_FILTER_SUMMARY` log - if `accepted_bar_count = 0` and `filtered_partial_count > 0`, bars were too recent

---

### 3. BarsRequest Exception/Complete Failure
**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs:260-330`

**Causes**:
- `NinjaTraderBarRequest` class not found (compilation issue)
- `RequestBarsForTradingDate` method not found
- Reflection failure accessing `LogEngineEvent`
- NinjaTrader API exception during request

**Symptoms**:
- `InvalidOperationException` thrown
- Strategy fails to start (critical error)
- Error message: "CRITICAL: Failed to request historical bars from NinjaTrader"

**Impact**: Strategy cannot start - complete failure

**Detection**: Check NinjaTrader compilation errors, ensure `NinjaTraderBarRequest.cs` is in project

---

### 4. Range Computation Fails (No Bars in Window)
**Location**: `modules/robot/core/StreamStateMachine.cs:2071-2137`

**Causes**:
- Bars exist but from wrong trading date
- Bars exist but outside `[RangeStartChicagoTime, SlotTimeChicagoTime)` window
- Timezone mismatch (bars in UTC but window in Chicago time)
- Trading date rollover confusion

**Symptoms**:
- `RANGE_COMPUTE_NO_BARS_DIAGNOSTIC` log event
- `RANGE_COMPUTE_FAILED` log event
- Range never computed, stream stuck in RANGE_BUILDING

**Impact**: Range never locks, trading blocked for that stream

**Detection**: Check `RANGE_COMPUTE_NO_BARS_DIAGNOSTIC` - look for `bars_from_wrong_date` flag

---

### 5. Starting After Slot Time
**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs:209-213`, `modules/robot/core/StreamStateMachine.cs:814-846`

**Causes**:
- Starting after `slot_time` has passed
- Range window already closed

**Symptoms**:
- "RESTART_POLICY: Restarting after slot time" log message
- BarsRequest limited to slot_time (not "now")
- `RANGE_COMPUTE_MISSED_SLOT_TIME` error if range not computed
- Late range computation attempted

**Impact**: Range may differ from uninterrupted operation (reconstruction vs. incremental)

**Detection**: Check restart time vs. slot_time in `MID_SESSION_RESTART_DETECTED` log

---

### 6. Data Gaps in Historical Bars
**Location**: `modules/robot/core/StreamStateMachine.cs:1407-1467`

**Causes**:
- Missing bars in middle of range window
- Data feed gaps during historical period
- Exchange halts or data provider issues

**Symptoms**:
- `GAP_TOLERANCE_VIOLATION` log event
- `RANGE_INVALIDATED` notification
- Range marked as invalid, trading blocked

**Impact**: Range invalidated, no trading for that stream that day

**Detection**: Check `GAP_TOLERANCE_VIOLATION` logs - look for gap minutes exceeding thresholds

**Gap Rules**:
- `MAX_SINGLE_GAP_MINUTES` (default: 15 minutes)
- `MAX_TOTAL_GAP_MINUTES` (default: 30 minutes)
- `MAX_GAP_LAST_10_MINUTES` (default: 5 minutes) - stricter rule for last 10 minutes before slot_time

---

### 7. Stream State Mismatch
**Location**: `modules/robot/core/RobotEngine.cs:738-753`

**Causes**:
- Stream already progressed past PRE_HYDRATION state
- Stream in RANGE_LOCKED or DONE state
- Bars arrive but stream cannot buffer them

**Symptoms**:
- `PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE` log event
- Bars not buffered, range computation fails

**Impact**: Bars lost, range incomplete

**Detection**: Check stream state in `PRE_HYDRATION_BARS_SKIPPED_STREAM_STATE` log

---

### 8. Timezone/Date Confusion
**Location**: `modules/robot/core/StreamStateMachine.cs:2040-2047`, `modules/robot/core/StreamStateMachine.cs:2103-2134`

**Causes**:
- DST transition confusion
- Chicago time vs UTC mismatch
- Trading date rollover edge cases
- Bars timestamped in wrong timezone

**Symptoms**:
- `RANGE_COMPUTE_NO_BARS_DIAGNOSTIC` with `bars_from_wrong_date = true`
- Bars filtered out due to date mismatch
- Range window empty despite bars existing

**Impact**: Range computation fails, trading blocked

**Detection**: Check `bar_buffer_date_range` vs `expected_trading_date` in diagnostic logs

---

### 9. Incomplete Range (Partial Bars)
**Location**: `modules/robot/core/StreamStateMachine.cs:770-813`

**Causes**:
- Starting mid-range, only partial historical bars available
- Range computed from `range_start` to "now" (not slot_time)
- Will be updated incrementally, but may miss early bars

**Symptoms**:
- `RANGE_INITIALIZED_FROM_HISTORY` log event
- `computed_up_to_chicago` < `slot_time_chicago`
- Range high/low may differ from full-range computation

**Impact**: Range may be incomplete if early bars missing, but system attempts recovery

**Detection**: Check `RANGE_INITIALIZED_FROM_HISTORY` - compare `computed_up_to_chicago` to `slot_time_chicago`

---

### 10. Multiple Streams, Different Slot Times
**Location**: `modules/robot/core/RobotEngine.cs:1342-1375` (GetBarsRequestTimeRange)

**Causes**:
- S1 and S2 streams enabled with different slot times
- BarsRequest covers earliest range_start to latest slot_time
- Some streams may receive bars outside their window

**Symptoms**:
- BarsRequest requests wider range than needed
- Each stream filters to its own window correctly
- No error, but inefficient

**Impact**: None (streams filter correctly), but may request unnecessary data

**Detection**: Check `BARSREQUEST_REQUESTED` log - verify range covers all streams

---

## Recovery Mechanisms

### Automatic Recovery
1. **Late Range Computation**: If slot_time passed without range, system attempts late computation (`RANGE_COMPUTE_MISSED_SLOT_TIME`)
2. **Incremental Updates**: Range updates incrementally as live bars arrive until slot_time
3. **Partial Range**: System accepts partial ranges if some bars available

### Manual Intervention Required
1. **Zero Bars**: Check NinjaTrader "Days to load", data provider connection
2. **Range Invalidated**: Check gap logs, may need to skip trading for that stream
3. **Stream State Mismatch**: May indicate timing bug, check logs

---

## Recommended Pre-Flight Checks

Before starting mid-range tomorrow:

1. **Verify NinjaTrader Settings**:
   - "Days to load" >= 14 days (includes today)
   - Historical data available for today's date
   - Trading hours template correct for instrument

2. **Check Logs for Previous Day**:
   - Look for `BARSREQUEST_ZERO_BARS_DIAGNOSTIC` events
   - Verify bars were loaded successfully

3. **Verify File Structure**:
   - `NinjaTraderBarRequest.cs` in NinjaTrader project
   - `RobotCore_For_NinjaTrader` files compiled correctly

4. **Monitor First Few Minutes**:
   - Watch for `BARSREQUEST_REQUESTED` and `BARSREQUEST_RAW_RESULT` logs
   - Verify `accepted_bar_count > 0` in `BARSREQUEST_FILTER_SUMMARY`
   - Check `RANGE_INITIALIZED_FROM_HISTORY` or `RANGE_COMPUTE_COMPLETE` events

5. **Check Stream States**:
   - Verify streams transition: PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED
   - Watch for `MID_SESSION_RESTART_DETECTED` log

---

## Log Events to Monitor

**Critical (Strategy Won't Work)**:
- `BARSREQUEST_ZERO_BARS_DIAGNOSTIC`
- `RANGE_COMPUTE_FAILED`
- `GAP_TOLERANCE_VIOLATION`
- `RANGE_INVALIDATED`

**Warning (May Work But Suboptimal)**:
- `PRE_HYDRATION_TIMEOUT_NO_BARS`
- `RANGE_INITIALIZED_FROM_HISTORY` (partial range)
- `PRE_HYDRATION_BARS_FILTERED` (some bars filtered)

**Informational (Normal Operation)**:
- `BARSREQUEST_REQUESTED`
- `BARSREQUEST_RAW_RESULT`
- `BARSREQUEST_FILTER_SUMMARY`
- `MID_SESSION_RESTART_DETECTED`
- `RANGE_COMPUTE_COMPLETE`
