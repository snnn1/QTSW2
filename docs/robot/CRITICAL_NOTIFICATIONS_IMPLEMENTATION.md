# Critical Notifications Implementation

**Date**: 2026-01-22  
**Status**: ✅ **IMPLEMENTED**

## Summary

Added `HealthMonitor.ReportCritical()` method to enable push notifications for critical events. Only two event types are whitelisted for safety and determinism.

## Implementation

### 1. HealthMonitor.ReportCritical() Method

**Location**: `modules/robot/core/HealthMonitor.cs`

**Signature**:
```csharp
public void ReportCritical(string eventType, Dictionary<string, object> payload, string? tradingDate = null)
```

**Features**:
- **Whitelist enforcement**: Only allows `EXECUTION_GATE_INVARIANT_VIOLATION` and `DISCONNECT_FAIL_CLOSED_ENTERED`
- **Deduplication**: One notification per `(eventType, run_id)` - deterministic and auditable
- **Emergency priority**: Priority 2 (bypasses rate limiting)
- **Immediate enqueue**: Skip rate limit for critical events
- **Rejection logging**: Logs `CRITICAL_NOTIFICATION_REJECTED` if event type not whitelisted

**Deduplication Logic**:
- Extracts `run_id` from payload
- Falls back to `tradingDate` if `run_id` not present
- Uses key format: `"{eventType}:{runId}"`
- Tracks sent notifications in `_criticalNotificationsSent` HashSet

### 2. StreamStateMachine Integration

**Location**: `modules/robot/core/StreamStateMachine.cs`

**Changes**:
- Added `_reportCriticalCallback` field
- Added `SetReportCriticalCallback()` method
- Wired `EXECUTION_GATE_INVARIANT_VIOLATION` to call callback

**Code**:
```csharp
// After logging EXECUTION_GATE_INVARIANT_VIOLATION
_reportCriticalCallback?.Invoke("EXECUTION_GATE_INVARIANT_VIOLATION", payload, TradingDate);
```

### 3. RobotEngine Integration

**Location**: `modules/robot/core/RobotEngine.cs`

**Changes**:
- Set `ReportCriticalCallback` when creating new streams
- Wired `DISCONNECT_FAIL_CLOSED_ENTERED` to call `HealthMonitor.ReportCritical()`

**Code**:
```csharp
// When creating new stream
if (_healthMonitor != null)
{
    newSm.SetReportCriticalCallback((eventType, payload, tradingDate) =>
    {
        _healthMonitor.ReportCritical(eventType, payload, tradingDate);
    });
}

// When entering DISCONNECT_FAIL_CLOSED state
_healthMonitor?.ReportCritical("DISCONNECT_FAIL_CLOSED_ENTERED", payload, TradingDateString);
```

### 4. Event Type Registry

**Location**: `modules/robot/core/RobotEventTypes.cs`

**New Events**:
- `CRITICAL_EVENT_REPORTED` (INFO level)
- `CRITICAL_NOTIFICATION_REJECTED` (WARN level)

## Whitelist

**Allowed Event Types**:
1. `EXECUTION_GATE_INVARIANT_VIOLATION`
2. `DISCONNECT_FAIL_CLOSED_ENTERED`

**Rejected Event Types**: All others are rejected with `CRITICAL_NOTIFICATION_REJECTED` event

## Notification Behavior

### EXECUTION_GATE_INVARIANT_VIOLATION

**Title**: "EXECUTION GATE INVARIANT VIOLATION"

**Message Format**:
```
Execution Gate Invariant Violation. Instrument: {instrument}. Stream: {stream}. Error: {error}. Details: {message}
```

**Deduplication**: One per `(EXECUTION_GATE_INVARIANT_VIOLATION, trading_date)`

**Priority**: 2 (Emergency)

---

### DISCONNECT_FAIL_CLOSED_ENTERED

**Title**: "DISCONNECT FAIL CLOSED ENTERED"

**Message Format**:
```
Robot Entered Fail-Closed Mode. Connection: {connection_name}. Active Streams: {count}. All execution blocked - requires operator intervention
```

**Deduplication**: One per `(DISCONNECT_FAIL_CLOSED_ENTERED, trading_date)`

**Priority**: 2 (Emergency)

---

## Safety Guarantees

### ✅ Deterministic Behavior

- **One notification per (eventType, run_id)**: Prevents notification spam
- **Whitelist enforcement**: Only approved events can trigger notifications
- **Auditable**: All attempts logged (accepted and rejected)

### ✅ Emergency Priority

- **Priority 2**: Bypasses Pushover quiet hours
- **No rate limiting**: Immediate enqueue
- **Background worker**: Non-blocking send

### ✅ Fail-Safe

- **HealthMonitor disabled**: No notifications sent (fail-safe)
- **NotificationService null**: No notifications sent (fail-safe)
- **Event type not whitelisted**: Logged but not sent (fail-safe)

## Testing

### Test Scenarios

1. **EXECUTION_GATE_INVARIANT_VIOLATION**:
   - Trigger: Wait for slot_time to pass with `can_detect_entries=false`
   - Expected: One notification per trading_date
   - Verify: Check `CRITICAL_EVENT_REPORTED` event in logs

2. **DISCONNECT_FAIL_CLOSED_ENTERED**:
   - Trigger: Disconnect NinjaTrader connection
   - Expected: One notification per trading_date
   - Verify: Check `CRITICAL_EVENT_REPORTED` event in logs

3. **Deduplication**:
   - Trigger: Same event multiple times in same trading_date
   - Expected: Only first notification sent
   - Verify: Check `_criticalNotificationsSent` HashSet

4. **Whitelist Rejection**:
   - Trigger: Call `ReportCritical()` with non-whitelisted event type
   - Expected: `CRITICAL_NOTIFICATION_REJECTED` logged, no notification sent
   - Verify: Check logs for rejection event

## Files Modified

1. `modules/robot/core/HealthMonitor.cs` - Added `ReportCritical()` method
2. `modules/robot/core/StreamStateMachine.cs` - Added callback and wired EXECUTION_GATE_INVARIANT_VIOLATION
3. `modules/robot/core/RobotEngine.cs` - Wired callbacks and DISCONNECT_FAIL_CLOSED_ENTERED
4. `modules/robot/core/RobotEventTypes.cs` - Added new event types
5. `RobotCore_For_NinjaTrader/*` - Synced all changes

## Next Steps

1. **Test notifications**: Verify notifications are sent for critical events
2. **Monitor logs**: Check `CRITICAL_EVENT_REPORTED` events
3. **Verify deduplication**: Ensure only one notification per (eventType, run_id)
4. **Monitor Pushover**: Verify notifications are received

## Related Documentation

- `docs/robot/CURRENT_PUSH_NOTIFICATIONS.md` - Current notification status
- `docs/robot/NOTIFICATION_SYSTEM_STATUS.md` - Notification system assessment
