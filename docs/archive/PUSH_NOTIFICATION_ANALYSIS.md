# Push Notification Log Analysis

## Summary

Analysis of `logs/robot/notification_errors.log` reveals the current state of push notification system.

## Current Status (Jan 26-27, 2026)

- **Notification Worker**: ✅ Running and healthy
  - Regular heartbeats every 60 seconds
  - Queue is empty (no backlog)
  - Last successful notification: 2026-01-27T14:32:00
  - Worker uptime: ~32 minutes (1921 seconds)
  - Restart count: 0 (stable)

- **Success Rate**: 97% (211 successful vs 6 errors)

## Issues Found

### 1. Timeout Exceptions (Primary Issue)
**All recent errors (Jan 26-27) are TimeoutExceptions**

Recent timeout errors:
- `2026-01-26T16:23:12` - RANGE_INVALIDATED:NG1 (Priority 1) - Timeout after 10 seconds
- `2026-01-25T03:32:09` - DISCONNECT_FAIL_CLOSED_ENTERED - Timeout after 10 seconds
- `2026-01-24T17:47:55` - CONNECTION_LOST (Sustained) - Timeout after 10 seconds
- `2026-01-24T04:30:53` - DISCONNECT_FAIL_CLOSED_ENTERED - Timeout after 10 seconds
- `2026-01-23T21:20:48` - DISCONNECT_FAIL_CLOSED_ENTERED - Timeout after 10 seconds
- `2026-01-21T14:59:11` - CONNECTION_LOST (Sustained) - Timeout after 10 seconds

**Root Cause**: Pushover API calls are timing out after 10 seconds. The `NotificationService` has a 10-second timeout guard, but the underlying `PushoverClient.HttpClient` has a 5-second timeout configured.

### 2. Historical Issues (Fixed)
- **Jan 12, 2026**: Missing `expire` and `retry` parameters for priority=2 notifications
  - Error: `"expire must be supplied with priority=2"`
  - Error: `"retry must be supplied with priority=2"`
  - **Status**: ✅ Fixed - Code now includes these parameters (lines 65-69 in PushoverClient.cs)

### 3. TaskCanceledException (Older)
- **Jan 13, 2026**: Some notifications failed with `TaskCanceledException`
  - Likely due to cancellation token or network issues
  - **Status**: Resolved (no recent occurrences)

## Technical Details

### Timeout Configuration Mismatch

**PushoverClient.cs** (line 17):
```csharp
Timeout = TimeSpan.FromSeconds(5)  // HttpClient timeout: 5 seconds
```

**NotificationService.cs** (line 362):
```csharp
var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);  // Guard timeout: 10 seconds
```

**Problem**: The HttpClient will timeout at 5 seconds, but the NotificationService waits up to 10 seconds. This creates a race condition where:
1. HttpClient times out at 5 seconds → throws `TaskCanceledException`
2. NotificationService timeout guard triggers at 10 seconds → wraps as `TimeoutException`

### Notification Worker Health

The worker is functioning correctly:
- ✅ Heartbeats every 60 seconds
- ✅ Queue processing working (empty queue indicates successful processing)
- ✅ No worker stalls detected
- ✅ No restarts needed

## Recommendations

### 1. ✅ Fix Timeout Configuration (APPLIED)
**Fix Applied**: Increased HttpClient timeout to 12 seconds to match guard timeout
```csharp
// In PushoverClient.cs (both copies)
Timeout = TimeSpan.FromSeconds(12)  // Slightly longer than guard timeout (10s) to ensure guard timeout triggers first
```

**Files Updated**:
- `modules/robot/core/Notifications/PushoverClient.cs`
- `RobotCore_For_NinjaTrader/Notifications/PushoverClient.cs`

This ensures the NotificationService guard timeout (10 seconds) will trigger before the HttpClient timeout, providing consistent error handling and better timeout messages.

### 2. Monitor Network Connectivity
The timeout errors suggest intermittent network issues or Pushover API slowness. Consider:
- Checking network connectivity during failures
- Monitoring Pushover API status
- Adding retry logic for transient failures

### 3. Add Retry Logic
For transient network failures, consider implementing exponential backoff retry:
- First retry: immediate
- Second retry: after 2 seconds
- Third retry: after 5 seconds
- Max 3 retries before giving up

## Log File Location

`logs/robot/notification_errors.log`

## Related Files

- `modules/robot/core/Notifications/NotificationService.cs` - Background worker
- `modules/robot/core/Notifications/PushoverClient.cs` - API client
- `modules/robot/core/HealthMonitor.cs` - Notification trigger logic

---

## Additional Issue: EXECUTION_GATE_INVARIANT_VIOLATION Rate Limiting Not Working

### Problem
**85 EXECUTION_GATE_INVARIANT_VIOLATION notifications sent in Jan 26-27, with 58 intervals < 5 minutes** (including many 0-3 seconds apart). Rate limiting should prevent notifications within 5 minutes of the same event type.

### Root Cause
**HealthMonitor instance is recreated for each RobotEngine run**, causing rate limiting state to be lost:

1. **HealthMonitor Creation**: `RobotEngine.cs` line 731 creates a new `HealthMonitor` instance per engine run
2. **Rate Limiting State**: The `_lastEmergencyNotifyUtcByEventType` dictionary is stored in HealthMonitor instance
3. **State Loss**: When a new engine run starts, a new HealthMonitor is created, resetting the rate limiting state
4. **Result**: Each engine run can send notifications even if the same event type was notified recently

### Evidence
- All 85 notifications have unique keys (different run_id hashes)
- No `CRITICAL_NOTIFICATION_SKIPPED` entries found in logs (rate limiting never triggered)
- Multiple notifications sent within seconds of each other (e.g., 6 notifications in 1 second at 20:28:51-52)

### Solution Options

**Option 1**: Make HealthMonitor a singleton (like NotificationService)
- Store rate limiting state in a persistent singleton
- Requires refactoring HealthMonitor to use singleton pattern

**Option 2**: Store rate limiting state in NotificationService (Recommended)
- NotificationService is already a singleton per project root
- Move `_lastEmergencyNotifyUtcByEventType` to NotificationService
- HealthMonitor calls NotificationService to check rate limits

**Option 3**: Persist rate limiting state to disk
- Store timestamps in a file or database
- Survives engine restarts
- More complex but most robust

**Recommendation**: Option 2 - Move rate limiting state to NotificationService singleton, as it's already designed for cross-run persistence.
