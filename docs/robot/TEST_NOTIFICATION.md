# Test Notification Guide

## How to Send a Test Notification

### Option 1: From NinjaTrader Strategy (Recommended)

If you have access to the RobotSimStrategy instance in NinjaTrader:

```csharp
// In NinjaTrader strategy or console
strategy.SendTestNotification();
```

### Option 2: Verify After Manual Trigger

1. Trigger the test notification using any method available
2. Run the verification script:
   ```bash
   python tools/test_notification.py
   ```

## What to Look For

After sending a test notification, check logs for:

1. **TEST_NOTIFICATION_SENT** event
   - Confirms the test notification method was called
   - Includes run_id and notification details

2. **PUSHOVER_NOTIFY_ENQUEUED** event
   - Confirms notification was enqueued to Pushover
   - Should appear within 1-2 seconds of TEST_NOTIFICATION_SENT

3. **PUSHOVER_ENDPOINT** event (if available)
   - Shows actual HTTP response from Pushover API
   - Status code 200 = success

4. **Push notification on your phone**
   - Title: "Robot Test Notification"
   - Message includes run_id

## Verification Script

Run `tools/test_notification.py` to automatically check for:
- Test notification events
- Pushover enqueue events
- Endpoint responses
- Timing correlation

## Troubleshooting

- **TEST_NOTIFICATION_SKIPPED**: Health monitor disabled or notification service not configured
- **No PUSHOVER_NOTIFY_ENQUEUED**: Notification service not started or queue full
- **No push received**: Check Pushover credentials, network connectivity, or notification_errors.log
