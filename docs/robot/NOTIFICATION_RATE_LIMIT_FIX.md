# Notification Rate Limit Fix

**Date**: 2026-01-26  
**Issue**: Excessive push notifications for `EXECUTION_GATE_INVARIANT_VIOLATION` events

## Problem

The notification system was sending too many push notifications because:

1. **Multiple Engine Runs**: Each engine run (with a unique `run_id`) triggers its own notification for the same event type
2. **No Emergency Rate Limiting**: Emergency priority notifications (priority 2) bypassed rate limiting entirely
3. **Frequent Violations**: `EXECUTION_GATE_INVARIANT_VIOLATION` events occur frequently across multiple engine runs

**Example**: On 2026-01-26 at 16:19:18-16:19:24, 6 notifications were sent in 6 seconds for the same event type across different run_ids.

## Solution

Added **emergency notification rate limiting** that:

1. **Tracks notifications by event type** (not by run_id)
2. **Enforces a 5-minute minimum interval** between notifications of the same event type
3. **Still respects per-run_id deduplication** (prevents duplicate notifications for the same run)

### Changes Made

**File**: `modules/robot/core/HealthMonitor.cs`

1. Added emergency notification tracking:
   ```csharp
   private readonly Dictionary<string, DateTimeOffset> _lastEmergencyNotifyUtcByEventType = new(StringComparer.OrdinalIgnoreCase);
   private const int EMERGENCY_NOTIFY_MIN_INTERVAL_SECONDS = 300; // 5 minutes
   ```

2. Modified `ReportCritical()` method to:
   - Check if the same event type was notified within the last 5 minutes
   - Skip notification if rate limited (logs `CRITICAL_NOTIFICATION_SKIPPED` event)
   - Still respect per-run_id deduplication

## Behavior

### Before Fix
- Each `run_id` gets its own notification for `EXECUTION_GATE_INVARIANT_VIOLATION`
- No rate limiting for emergency notifications
- Result: Many notifications when multiple engine runs trigger the same event

### After Fix
- First occurrence of `EXECUTION_GATE_INVARIANT_VIOLATION` sends notification
- Subsequent occurrences of the same event type within 5 minutes are skipped (logged but not sent)
- After 5 minutes, the next occurrence will send a notification again
- Per-run_id deduplication still prevents duplicate notifications for the same run

## Configuration

The emergency rate limit interval is hardcoded to **300 seconds (5 minutes)**. This can be adjusted by changing `EMERGENCY_NOTIFY_MIN_INTERVAL_SECONDS` in `HealthMonitor.cs`.

## Logging

When a notification is rate-limited, a `CRITICAL_NOTIFICATION_SKIPPED` event is logged with:
- `event_type`: The event type that was skipped
- `reason`: "EMERGENCY_RATE_LIMIT"
- `min_interval_seconds`: 300
- `note`: Explanation of why it was skipped

## Testing

To verify the fix is working:

1. Check notification error log for reduced notification frequency:
   ```powershell
   Get-Content logs\robot\notification_errors.log | Select-String "Pushover notification sent successfully" | Select-String "EXECUTION_GATE_INVARIANT_VIOLATION"
   ```

2. Check for skipped notifications:
   ```powershell
   Get-Content logs\robot\robot_ENGINE*.jsonl | Select-String "CRITICAL_NOTIFICATION_SKIPPED"
   ```

3. Verify notifications are spaced at least 5 minutes apart for the same event type

## Related Files

- `modules/robot/core/HealthMonitor.cs` - Main implementation
- `modules/robot/core/Notifications/NotificationService.cs` - Background notification worker
- `configs/robot/health_monitor.json` - Health monitor configuration
- `logs/robot/notification_errors.log` - Notification send results
