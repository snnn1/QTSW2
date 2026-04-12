# Execution Log Routing - Implementation Summary

## Overview
All execution-related events are now routed to a dedicated `robot_EXECUTION.jsonl` log file, separate from instrument-specific logs.

**Status**: ✅ **IMPLEMENTED WITH HARDENING**

This implementation includes:
- ✅ Routing audit fields (`is_execution_event`, `execution_routing_reason`)
- ✅ Explicit allowlist for critical execution events
- ✅ Clear routing precedence (ENGINE > EXECUTION > instrument)
- ✅ File rotation support (size-based and daily)
- ✅ Removed INSTRUMENT_RESOLUTION from execution routing (now goes to ENGINE/instrument)

## Implementation

### Modified File
**`RobotLoggingService.cs`** - `FlushBatch()` method and `IsExecutionEvent()` helper

### Changes Made

1. **Added `IsExecutionEvent()` Helper Method**
   - Uses explicit allowlist first (most reliable)
   - Falls back to heuristics for unknown event types
   - Returns routing reason for audit/debugging

2. **Modified Event Routing Logic**
   - **Routing Precedence**: ENGINE > EXECUTION > instrument
   - Execution events are routed to `"EXECUTION"` instead of instrument name
   - All other events continue to route normally (by instrument or "ENGINE")

3. **Added Routing Audit Fields**
   - `is_execution_event: true` - Boolean flag for execution events
   - `execution_routing_reason` - String explaining why event was routed (e.g., "allowlist:ORDER_SUBMITTED", "prefix:ORDER_")

4. **Explicit Allowlist**
   - Critical execution events explicitly listed
   - Prevents silent misses when event names change
   - Heuristics used as fallback for unknown events

5. **File Rotation**
   - EXECUTION log rotates based on size (configurable via `max_file_size_mb`)
   - Daily rotation supported if `rotate_daily` is enabled
   - Same rotation logic as other log files

---

## Execution Event Detection

### Routing Precedence

Events are routed in this order:
1. **ENGINE** - If `stream == "__engine__"` OR `instrument` is empty
2. **EXECUTION** - If event matches execution criteria (see below)
3. **Instrument** - Otherwise route to instrument-specific file

### Detection Methods

Events are routed to `robot_EXECUTION.jsonl` using:

#### 1. Explicit Allowlist (Primary - Most Reliable)

Critical execution events explicitly listed:

**Order Events:**
- `ORDER_SUBMIT_ATTEMPT`, `ORDER_SUBMIT_SUCCESS`, `ORDER_SUBMIT_FAIL`, `ORDER_SUBMITTED`
- `ORDER_ACKNOWLEDGED`, `ORDER_REJECTED`, `ORDER_CANCELLED`
- `ORDER_CREATE_FAIL`, `ORDER_CREATED_STOPMARKET`, `ORDER_CREATED_VERIFICATION`

**Execution Events:**
- `EXECUTION_FILLED`, `EXECUTION_PARTIAL_FILL`, `EXECUTION_UPDATE_UNKNOWN_ORDER`
- `EXECUTION_ERROR`, `EXECUTION_BLOCKED`

**Protective Order Events:**
- `PROTECTIVE_ORDERS_SUBMITTED`, `PROTECTIVE_ORDERS_FAILED_FLATTENED`

**Modification Events:**
- `STOP_MODIFY_SUCCESS`, `STOP_MODIFY_FAIL`
- `TARGET_MODIFY_SUCCESS`, `TARGET_MODIFY_FAIL`

**Intent Fill Events:**
- `INTENT_FILL_UPDATE`, `INTENT_OVERFILL_EMERGENCY`

#### 2. Heuristics (Fallback - For Unknown Events)

If event is not in allowlist, these heuristics apply:

- ✅ **Starts with `ORDER_`** → Routes to EXECUTION
- ✅ **Starts with `EXECUTION_`** → Routes to EXECUTION
- ✅ **Starts with `PROTECTIVE_`** → Routes to EXECUTION
- ✅ **Starts with `INTENT_` AND contains `FILL` or `OVERFILL`** → Routes to EXECUTION
- ✅ **Contains `STOP_MODIFY` or `TARGET_MODIFY`** → Routes to EXECUTION

#### 3. Explicitly Excluded

- ❌ **`INSTRUMENT_RESOLUTION` events** - These are system health/startup events and route to ENGINE or instrument logs, NOT EXECUTION. If you need them in EXECUTION, add them to the allowlist explicitly.

---

## Log File Structure

### Before:
```
logs/robot/
├── robot_ENGINE.jsonl          (engine events)
├── robot_MES.jsonl             (MES stream + execution events)
├── robot_MNQ.jsonl             (MNQ stream + execution events)
└── robot_MGC.jsonl             (MGC stream + execution events)
```

### After:
```
logs/robot/
├── robot_ENGINE.jsonl          (engine events only)
├── robot_EXECUTION.jsonl       (ALL execution events - all instruments)
├── robot_MES.jsonl             (MES stream events only)
├── robot_MNQ.jsonl             (MNQ stream events only)
└── robot_MGC.jsonl             (MGC stream events only)
```

---

## Benefits

### 1. **Centralized Execution Logging**
- All order/execution events in one file
- Easy to grep/search for execution issues
- No need to search across multiple instrument files

### 2. **Cleaner Instrument Logs**
- Instrument files contain only stream/strategy events
- Easier to analyze per-instrument behavior
- Reduced file size per instrument

### 3. **Better Debugging**
- Execution issues visible in one place
- Easier to trace order lifecycle across instruments
- Better for analyzing execution quality

### 4. **Performance Analysis**
- All fills, slippage, latency in one file
- Easy to aggregate execution metrics
- Better for performance monitoring

---

## Example Events Routed to `robot_EXECUTION.jsonl`

```json
{"ts_utc":"2026-01-28T19:30:00Z","event":"ORDER_SUBMIT_ATTEMPT","instrument":"MES","data":{"is_execution_event":true,"execution_routing_reason":"allowlist:ORDER_SUBMIT_ATTEMPT",...}}
{"ts_utc":"2026-01-28T19:30:01Z","event":"ORDER_SUBMIT_SUCCESS","instrument":"MES","data":{"is_execution_event":true,"execution_routing_reason":"allowlist:ORDER_SUBMIT_SUCCESS",...}}
{"ts_utc":"2026-01-28T19:30:05Z","event":"EXECUTION_FILLED","instrument":"MES","data":{"is_execution_event":true,"execution_routing_reason":"allowlist:EXECUTION_FILLED",...}}
{"ts_utc":"2026-01-28T19:30:06Z","event":"PROTECTIVE_ORDERS_SUBMITTED","instrument":"MES","data":{"is_execution_event":true,"execution_routing_reason":"allowlist:PROTECTIVE_ORDERS_SUBMITTED",...}}
{"ts_utc":"2026-01-28T19:30:10Z","event":"ORDER_SUBMIT_ATTEMPT","instrument":"MNQ","data":{"is_execution_event":true,"execution_routing_reason":"allowlist:ORDER_SUBMIT_ATTEMPT",...}}
{"ts_utc":"2026-01-28T19:30:11Z","event":"EXECUTION_FILLED","instrument":"MNQ","data":{"is_execution_event":true,"execution_routing_reason":"allowlist:EXECUTION_FILLED",...}}
```

**Note**: 
- Events still contain `instrument` field for filtering
- All execution events include `is_execution_event: true` and `execution_routing_reason` for audit/debugging
- Routing reason shows why event was classified (allowlist vs heuristic)

---

## Backward Compatibility

✅ **Fully Backward Compatible**
- Existing log analysis tools can still filter by `instrument` field
- Event structure unchanged
- Only routing changed (which file events go to)

---

## Usage Examples

### View All Execution Events
```bash
# View all execution events
cat logs/robot/robot_EXECUTION.jsonl

# View execution events for specific instrument
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.instrument == "MES")'

# View only fills
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.event == "EXECUTION_FILLED")'

# View order submissions
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.event | startswith("ORDER_SUBMIT"))'

# View errors
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.level == "ERROR")'
```

### Analyze Execution Quality
```bash
# Calculate average slippage
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.event == "EXECUTION_FILLED") | .data.slippage' | awk '{sum+=$1; count++} END {print sum/count}'

# Calculate average fill latency
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.event == "EXECUTION_FILLED") | .data.fill_latency_ms' | awk '{sum+=$1; count++} END {print sum/count}'

# Count fills per instrument
cat logs/robot/robot_EXECUTION.jsonl | jq 'select(.event == "EXECUTION_FILLED") | .instrument' | sort | uniq -c
```

---

## File Rotation

The `robot_EXECUTION.jsonl` file rotates automatically:

- **Size-based rotation**: When file exceeds `max_file_size_mb` (default: 50 MB)
- **Daily rotation**: If `rotate_daily` is enabled in logging config
- **Rotated files**: Named `robot_EXECUTION_YYYYMMDD_HHMMSS.jsonl`
- **Cleanup**: Old rotated files cleaned up based on `max_rotated_files` config

**Note**: Execution logs can grow fast on busy trading days. Monitor file size and adjust rotation settings if needed.

## Watchdog / Tooling Updates

If you have tools that monitor log files, update them to check:

- **`robot_ENGINE.jsonl`** - For engine liveness/heartbeats
- **`robot_EXECUTION.jsonl`** - For broker/order health ⭐ **NEW**
- **`robot_<instrument>.jsonl`** - For stream state per instrument

### Example: Check for Order Failures

```bash
# Old way (searching all instrument files)
grep "ORDER_SUBMIT_FAIL" logs/robot/robot_*.jsonl

# New way (single execution file)
grep "ORDER_SUBMIT_FAIL" logs/robot/robot_EXECUTION.jsonl
```

## Routing Audit Fields

Every event routed to EXECUTION includes audit fields in `data`:

- `is_execution_event: true` - Confirms event was routed to execution log
- `execution_routing_reason` - Explains why (e.g., "allowlist:ORDER_SUBMITTED", "prefix:ORDER_")

These fields help:
- **Debug routing issues** - See why an event landed in EXECUTION
- **Detect misroutes** - Find events that shouldn't be in EXECUTION
- **Understand classification** - Know if event matched allowlist or heuristic

## Status: ✅ **COMPLETE WITH HARDENING**

All execution-related events are now routed to `robot_EXECUTION.jsonl`:
- ✅ Order submission events (with allowlist)
- ✅ Execution/fill events (with allowlist)
- ✅ Protective order events (with allowlist)
- ✅ Order modification events (with allowlist)
- ✅ Routing audit fields added
- ✅ Explicit precedence rules
- ✅ File rotation support
- ❌ Instrument resolution events excluded (go to ENGINE/instrument)

The execution log file will be created automatically when the first execution event is logged.
