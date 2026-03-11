# Push Notifications System Summary & Improvement Plan

## Current System Overview

### Architecture
- **Service**: Pushover (push notification service)
- **Implementation**: `HealthMonitor.cs` in robot core
- **Configuration**: `configs/robot/health_monitor.json`
- **Background Worker**: `NotificationService` singleton handles actual sending

### Current Notification Triggers

#### 1. **CONNECTION_LOST_SUSTAINED** ✅ (Active)
- **Trigger**: NinjaTrader connection lost for 60+ seconds
- **Priority**: Emergency (2)
- **Rate Limit**: None (skipRateLimit: true)
- **Message**: Includes connection name, elapsed time, active streams status
- **Status**: Working correctly

#### 2. **ENGINE_TICK_STALL_DETECTED** ✅ (Active)
- **Trigger**: Engine Tick() not called for 120+ seconds during active trading
- **Priority**: Emergency (2)
- **Rate Limit**: None (skipRateLimit: true)
- **Message**: Includes last tick timestamp, elapsed seconds, threshold
- **Status**: Working correctly

#### 3. **EXECUTION_GATE_INVARIANT_VIOLATION** ✅ (Active)
- **Trigger**: Via `ReportCritical()` callback from StreamStateMachine
- **Priority**: Emergency (2)
- **Rate Limit**: Emergency rate limit (5 minutes per event type via NotificationService)
- **Deduplication**: One notification per (eventType, run_id)
- **Message**: Includes instrument, stream, error details
- **Status**: Working correctly

#### 4. **DISCONNECT_FAIL_CLOSED_ENTERED** ✅ (Active)
- **Trigger**: Via `ReportCritical()` callback from RobotEngine
- **Priority**: Emergency (2)
- **Rate Limit**: Emergency rate limit (5 minutes per event type)
- **Deduplication**: One notification per (eventType, run_id)
- **Message**: Includes connection name, active stream count
- **Status**: Working correctly

### Events That Do NOT Trigger Notifications

#### 1. **DUPLICATE_INSTANCE_DETECTED** ❌ (Missing)
- **Current Behavior**: Only logged to `robot_ENGINE.jsonl`
- **Location**: `RobotSimStrategy.cs` line 170
- **Impact**: Critical deployment issue goes unnoticed via push notifications
- **Should Trigger**: YES - Critical deployment validation failure

#### 2. **EXECUTION_POLICY_VALIDATION_FAILED** ❌ (Missing)
- **Current Behavior**: Only logged to `robot_ENGINE.jsonl`
- **Location**: `RobotEngine.cs` lines 587, 599, 611, 2729
- **Impact**: Critical execution blocking issue goes unnoticed via push notifications
- **Should Trigger**: YES - Critical execution policy failure

#### 3. **DATA_LOSS_DETECTED** ⚠️ (Intentionally Disabled)
- **Current Behavior**: Log only (notifications disabled)
- **Reason**: Handled by gap tolerance + range invalidation
- **Status**: Intentional design decision

#### 4. **TIMETABLE_POLL_STALL_DETECTED** ⚠️ (Intentionally Disabled)
- **Current Behavior**: Log only (notifications disabled)
- **Reason**: Non-execution-critical
- **Status**: Intentional design decision

## Critical Gap Analysis

### Missing Notifications for New Engine Events

The updated engine emits two new critical events that are **NOT** currently triggering push notifications:

#### 1. **DUPLICATE_INSTANCE_DETECTED**
- **Severity**: CRITICAL
- **Impact**: Invalid deployment - multiple strategy instances running on same account/instrument
- **Current State**: Only logged, no notification
- **Recommendation**: Add to HealthMonitor whitelist and report via `ReportCritical()`

#### 2. **EXECUTION_POLICY_VALIDATION_FAILED**
- **Severity**: CRITICAL
- **Impact**: Execution completely blocked - no trades can occur
- **Current State**: Only logged, no notification
- **Recommendation**: Add to HealthMonitor whitelist and report via `ReportCritical()`

## Current Whitelist

```csharp
private static readonly HashSet<string> ALLOWED_CRITICAL_EVENT_TYPES = new(StringComparer.OrdinalIgnoreCase)
{
    "EXECUTION_GATE_INVARIANT_VIOLATION",
    "DISCONNECT_FAIL_CLOSED_ENTERED"
};
```

**Missing**: `DUPLICATE_INSTANCE_DETECTED`, `EXECUTION_POLICY_VALIDATION_FAILED`

## Notification Rate Limiting

### Current Rate Limits
1. **Emergency Notifications** (Priority 2):
   - Rate limit: 5 minutes per event type (via NotificationService singleton)
   - Cross-run persistence (survives engine restarts)
   - Applied to: EXECUTION_GATE_INVARIANT_VIOLATION, DISCONNECT_FAIL_CLOSED_ENTERED

2. **Regular Notifications** (Priority < 2):
   - Rate limit: `min_notify_interval_seconds` (default: 600 seconds = 10 minutes)
   - Per notification key
   - Applied to: CONNECTION_LOST_SUSTAINED, ENGINE_TICK_STALL_DETECTED

### Deduplication Strategy
- **Per-Run Deduplication**: One notification per (eventType, run_id)
- **Fallback**: Uses trading_date if run_id unavailable
- **Last Resort**: Uses "UNKNOWN_RUN" with warning

## Configuration

### Current Config (`configs/robot/health_monitor.json`)
```json
{
  "enabled": true,
  "data_stall_seconds": 180,
  "min_notify_interval_seconds": 600,
  "pushover_enabled": true,
  "pushover_user_key": "...",
  "pushover_app_token": "...",
  "pushover_priority": 0
}
```

### Pushover Priority Levels
- **0**: Normal priority (default)
- **1**: High priority (bypass quiet hours)
- **2**: Emergency priority (bypass quiet hours + retry until acknowledged)

## Improvement Recommendations

### 1. Add Missing Critical Events to Whitelist ⚠️ **HIGH PRIORITY**

**File**: `modules/robot/core/HealthMonitor.cs`

**Change**: Add new event types to `ALLOWED_CRITICAL_EVENT_TYPES`:
```csharp
private static readonly HashSet<string> ALLOWED_CRITICAL_EVENT_TYPES = new(StringComparer.OrdinalIgnoreCase)
{
    "EXECUTION_GATE_INVARIANT_VIOLATION",
    "DISCONNECT_FAIL_CLOSED_ENTERED",
    "DUPLICATE_INSTANCE_DETECTED",           // NEW
    "EXECUTION_POLICY_VALIDATION_FAILED"    // NEW
};
```

### 2. Report DUPLICATE_INSTANCE_DETECTED ⚠️ **HIGH PRIORITY**

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Change**: After logging the event (line 170), add:
```csharp
// Report to HealthMonitor for push notification
if (_engine != null)
{
    var notificationService = _engine.GetNotificationService();
    if (notificationService != null)
    {
        var payload = new Dictionary<string, object>
        {
            ["account"] = accountName,
            ["execution_instrument"] = executionInstrumentFullName,
            ["instance_id"] = _instanceId,
            ["error"] = errorMsg,
            ["action"] = "STAND_DOWN"
        };
        _engine.GetHealthMonitor()?.ReportCritical("DUPLICATE_INSTANCE_DETECTED", payload);
    }
}
```

**Note**: Requires adding `GetHealthMonitor()` method to RobotEngine if not already present.

### 3. Report EXECUTION_POLICY_VALIDATION_FAILED ⚠️ **HIGH PRIORITY**

**File**: `modules/robot/core/RobotEngine.cs`

**Change**: After logging `EXECUTION_POLICY_VALIDATION_FAILED` events (lines 587, 599, 611, 2729), add:
```csharp
// Report to HealthMonitor for push notification
var payload = new Dictionary<string, object>
{
    ["errors"] = quantityValidationErrors,  // or ex.Message for single errors
    ["unique_execution_instruments"] = uniqueExecutionInstruments.ToList(),  // if available
    ["file_path"] = _executionPolicyPath,
    ["note"] = "Execution blocked due to execution policy validation failures"
};
_healthMonitor?.ReportCritical("EXECUTION_POLICY_VALIDATION_FAILED", payload);
```

### 4. Enhance Message Building for New Events ⚠️ **MEDIUM PRIORITY**

**File**: `modules/robot/core/HealthMonitor.cs`

**Change**: Update `BuildCriticalEventMessage()` method to handle new event types:
```csharp
else if (eventType == "DUPLICATE_INSTANCE_DETECTED")
{
    parts.Add("CRITICAL: Duplicate Strategy Instance Detected");
    if (payload.TryGetValue("account", out var account) && account != null)
        parts.Add($"Account: {account}");
    if (payload.TryGetValue("execution_instrument", out var inst) && inst != null)
        parts.Add($"Instrument: {inst}");
    if (payload.TryGetValue("instance_id", out var instanceId) && instanceId != null)
        parts.Add($"Instance ID: {instanceId}");
    if (payload.TryGetValue("error", out var error) && error != null)
        parts.Add($"Error: {error}");
    parts.Add("Action: Strategy standing down - requires operator intervention");
}
else if (eventType == "EXECUTION_POLICY_VALIDATION_FAILED")
{
    parts.Add("CRITICAL: Execution Policy Validation Failed");
    if (payload.TryGetValue("errors", out var errors) && errors != null)
    {
        if (errors is List<string> errorList)
            parts.Add($"Errors: {string.Join("; ", errorList)}");
        else
            parts.Add($"Error: {errors}");
    }
    if (payload.TryGetValue("unique_execution_instruments", out var instruments) && instruments != null)
    {
        if (instruments is List<string> instList)
            parts.Add($"Affected Instruments: {string.Join(", ", instList)}");
    }
    parts.Add("Execution blocked - requires operator intervention");
}
```

### 5. Optional: Add Notification for Stuck Streams ⚠️ **LOW PRIORITY**

**Consideration**: Watchdog detects stuck streams (PRE_HYDRATION > 30 min, ARMED > 2 hours). Could add notification for:
- Streams stuck in PRE_HYDRATION > 1 hour
- Multiple streams stuck simultaneously

**Implementation**: Would require Python watchdog → C# HealthMonitor communication (complex, may not be worth it).

### 6. Optional: Notification Testing Improvements ⚠️ **LOW PRIORITY**

**Current**: `SendTestNotification()` method exists
**Enhancement**: Add test methods for each critical event type to verify notifications work correctly.

## Implementation Priority

1. **HIGH**: Add DUPLICATE_INSTANCE_DETECTED and EXECUTION_POLICY_VALIDATION_FAILED to whitelist
2. **HIGH**: Wire up ReportCritical() calls for both new events
3. **MEDIUM**: Enhance message building for better notification clarity
4. **LOW**: Consider additional notifications for stuck streams (if needed)

## Testing Checklist

After implementing changes:
- [ ] Test DUPLICATE_INSTANCE_DETECTED notification (trigger duplicate instance scenario)
- [ ] Test EXECUTION_POLICY_VALIDATION_FAILED notification (corrupt execution policy file)
- [ ] Verify rate limiting works correctly (5-minute emergency rate limit)
- [ ] Verify deduplication works (one notification per run_id)
- [ ] Verify message clarity (readable notification messages)
- [ ] Verify existing notifications still work (CONNECTION_LOST, ENGINE_TICK_STALL, etc.)

## Summary

**Current State**: Push notification system is functional but missing coverage for two critical new engine events.

**Key Gap**: `DUPLICATE_INSTANCE_DETECTED` and `EXECUTION_POLICY_VALIDATION_FAILED` are logged but not triggering push notifications, meaning critical deployment and execution blocking issues go unnoticed.

**Recommended Action**: Add both events to HealthMonitor whitelist and wire up `ReportCritical()` calls in the appropriate locations. This is a high-priority fix to ensure critical issues are properly alerted.
