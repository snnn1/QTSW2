# Execution Log Routing - Hardening Implementation

## Summary

All hardening recommendations have been implemented for the execution log routing feature.

---

## ✅ 1. Routing Audit Safety Net

**Implemented**: Every event routed to EXECUTION now includes audit fields.

### Fields Added to `data`:
- `is_execution_event: true` - Boolean flag confirming execution routing
- `execution_routing_reason` - String explaining why event was routed (e.g., `"allowlist:ORDER_SUBMITTED"`, `"prefix:ORDER_"`)

### Benefits:
- **Debuggability**: See exactly why an event landed in EXECUTION log
- **Misdirection Detection**: Identify events that shouldn't be in EXECUTION
- **Audit Trail**: Track routing decisions for compliance/debugging

### Example:
```json
{
  "ts_utc": "2026-01-28T19:30:00Z",
  "event": "ORDER_SUBMIT_ATTEMPT",
  "instrument": "MES",
  "data": {
    "is_execution_event": true,
    "execution_routing_reason": "allowlist:ORDER_SUBMIT_ATTEMPT",
    ...
  }
}
```

---

## ✅ 2. Explicit Allowlist for Critical Events

**Implemented**: Explicit `HashSet<string>` allowlist for critical execution events.

### Allowlist Includes:
- **Order Events**: `ORDER_SUBMIT_ATTEMPT`, `ORDER_SUBMIT_SUCCESS`, `ORDER_SUBMIT_FAIL`, `ORDER_SUBMITTED`, `ORDER_ACKNOWLEDGED`, `ORDER_REJECTED`, `ORDER_CANCELLED`, `ORDER_CREATE_FAIL`, `ORDER_CREATED_STOPMARKET`, `ORDER_CREATED_VERIFICATION`
- **Execution Events**: `EXECUTION_FILLED`, `EXECUTION_PARTIAL_FILL`, `EXECUTION_UPDATE_UNKNOWN_ORDER`, `EXECUTION_ERROR`, `EXECUTION_BLOCKED`
- **Protective Orders**: `PROTECTIVE_ORDERS_SUBMITTED`, `PROTECTIVE_ORDERS_FAILED_FLATTENED`
- **Modifications**: `STOP_MODIFY_SUCCESS`, `STOP_MODIFY_FAIL`, `TARGET_MODIFY_SUCCESS`, `TARGET_MODIFY_FAIL`
- **Intent Fills**: `INTENT_FILL_UPDATE`, `INTENT_OVERFILL_EMERGENCY`

### Benefits:
- **Prevents Silent Misses**: If event names change, allowlist must be updated explicitly
- **Most Reliable**: Checked first before heuristics
- **Easy to Extend**: Add new events to allowlist as needed

### Fallback:
- If event not in allowlist, heuristics are used as fallback
- Routing reason indicates whether allowlist or heuristic matched

---

## ✅ 3. Routing Precedence Rules

**Implemented**: Explicit precedence prevents ENGINE/EXECUTION collisions.

### Precedence Order:
1. **ENGINE** - If `stream == "__engine__"` OR `instrument` is empty
   - Heartbeats, startup events, system health
   - Takes precedence over everything
   
2. **EXECUTION** - If event matches execution criteria
   - Order submission, fills, broker activity
   - Takes precedence over instrument routing
   
3. **Instrument** - Default fallback
   - Stream state, strategy events
   - Only used if not ENGINE or EXECUTION

### Benefits:
- **No Collisions**: Clear rules prevent ambiguous routing
- **Predictable**: Always know where an event will go
- **Debuggable**: Routing reason explains decision

### Example:
```csharp
// ENGINE wins (even if event type matches execution)
stream == "__engine__" → ENGINE

// EXECUTION wins (even if instrument is set)
event == "ORDER_SUBMITTED" → EXECUTION

// Instrument fallback
event == "RANGE_LOCKED" → instrument file
```

---

## ✅ 4. Watchdog / Tooling Updates

**Documented**: Updated documentation with file monitoring guidance.

### Files to Monitor:
- **`robot_ENGINE.jsonl`** - Engine liveness/heartbeats
- **`robot_EXECUTION.jsonl`** - Broker/order health ⭐ **NEW**
- **`robot_<instrument>.jsonl`** - Stream state per instrument

### Example Commands:
```bash
# Check for order failures (old way - multiple files)
grep "ORDER_SUBMIT_FAIL" logs/robot/robot_*.jsonl

# Check for order failures (new way - single file)
grep "ORDER_SUBMIT_FAIL" logs/robot/robot_EXECUTION.jsonl

# Monitor execution health
tail -f logs/robot/robot_EXECUTION.jsonl | grep ERROR

# Check engine liveness
tail -f logs/robot/robot_ENGINE.jsonl | grep HEARTBEAT
```

---

## ✅ 5. File Growth & Rotation

**Already Implemented**: File rotation was already in place and works for EXECUTION.

### Rotation Features:
- **Size-based**: Rotates when file exceeds `max_file_size_mb` (default: 50 MB)
- **Daily rotation**: If `rotate_daily` is enabled in config
- **Rotated files**: Named `robot_EXECUTION_YYYYMMDD_HHMMSS.jsonl`
- **Cleanup**: Old rotated files cleaned up based on `max_rotated_files`

### Configuration:
Set in `LoggingConfig.json`:
```json
{
  "max_file_size_mb": 50,
  "rotate_daily": true,
  "max_rotated_files": 10
}
```

### Benefits:
- **Prevents Giant Files**: Automatic rotation keeps files manageable
- **Daily Archives**: Easy to find logs for specific dates
- **Performance**: Smaller files are faster to search/analyze

---

## ✅ 6. INSTRUMENT_RESOLUTION Routing Fix

**Implemented**: Removed INSTRUMENT_RESOLUTION from execution routing.

### Change:
- **Before**: `INSTRUMENT_RESOLUTION` events routed to EXECUTION
- **After**: `INSTRUMENT_RESOLUTION` events route to ENGINE or instrument logs

### Rationale:
- These are system health/startup events, not execution events
- Better fits in ENGINE log for system diagnostics
- If needed in EXECUTION, add explicitly to allowlist

### Note:
If you need `INSTRUMENT_RESOLUTION` events in EXECUTION log, add them to the allowlist:
```csharp
"INSTRUMENT_RESOLUTION_FAILED",
"INSTRUMENT_RESOLUTION_ERROR",
```

---

## Implementation Details

### Code Location
- **File**: `RobotLoggingService.cs`
- **Method**: `FlushBatch()` - Routing logic
- **Method**: `IsExecutionEvent()` - Classification logic

### Key Changes:
1. Added `ExecutionEventAllowlist` HashSet with critical events
2. Modified `IsExecutionEvent()` to return routing reason
3. Updated `FlushBatch()` to add audit fields and enforce precedence
4. Removed INSTRUMENT_RESOLUTION from execution routing

### Testing Recommendations:
1. **Verify Routing**: Check that execution events go to `robot_EXECUTION.jsonl`
2. **Check Audit Fields**: Verify `is_execution_event` and `execution_routing_reason` are present
3. **Test Precedence**: Ensure ENGINE events don't go to EXECUTION
4. **Monitor Rotation**: Verify file rotation works for EXECUTION log
5. **Check Allowlist**: Verify critical events match allowlist

---

## Status: ✅ **ALL HARDENING COMPLETE**

All recommendations implemented:
- ✅ Routing audit fields
- ✅ Explicit allowlist
- ✅ Routing precedence rules
- ✅ Watchdog documentation
- ✅ File rotation support
- ✅ INSTRUMENT_RESOLUTION fix

The execution log routing is now production-ready with full hardening.
