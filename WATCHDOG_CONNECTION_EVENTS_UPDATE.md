# Watchdog Connection Events Update Assessment

## Summary

Updated watchdog to track new connection notification events added in the notification disconnect handling fixes.

## Changes Made

### 1. Added CONNECTION_RECOVERED_NOTIFICATION Event Type

**Files Updated:**
- `modules/watchdog/config.py` - Added to LIVE_CRITICAL_EVENT_TYPES
- `modules/watchdog/event_processor.py` - Added to connection status handler
- `modules/watchdog/aggregator.py` - Added to important_types and warning severity

**Rationale:**
- New event type `CONNECTION_RECOVERED_NOTIFICATION` was added to HealthMonitor
- Watchdog needs to track this event to show recovery notifications in UI
- Event is informational (warning severity), not critical

### 2. Event Processing

The watchdog now handles:
- `CONNECTION_LOST` → Updates connection status to "ConnectionLost"
- `CONNECTION_LOST_SUSTAINED` → Updates connection status to "ConnectionLost"  
- `CONNECTION_RECOVERED` → Updates connection status to "Connected"
- `CONNECTION_RECOVERED_NOTIFICATION` → Updates connection status to "Connected" (NEW)

All these events update the connection status in state_manager and are included in important events feed.

## Current Status Check

### Robot Engine Status
- ✅ ENGINE_TICK_CALLSITE events found: 9,557 events in last 6 hours
- ⚠️ Latest tick: 33 minutes ago (exceeds 15s threshold) - Engine appears stopped
- ✅ Connection events found in raw logs: CONNECTION_LOST (1), CONNECTION_RECOVERED (7)
- ⚠️ CONNECTION_RECOVERED_NOTIFICATION: 0 events (expected - only fires after sustained disconnect ≥60s)

### Watchdog Feed Status
- ✅ Feed file exists: `logs/robot/frontend_feed.jsonl` (9,660 lines)
- ✅ Connection events in feed: CONNECTION_LOST (6), CONNECTION_RECOVERED (35)
- ✅ CONNECTION_RECOVERED_NOTIFICATION configured correctly in LIVE_CRITICAL_EVENT_TYPES
- ⚠️ CONNECTION_RECOVERED_NOTIFICATION: 0 events in feed (expected - hasn't occurred yet)

### Watchdog Backend Status
- ✅ Watchdog backend IS running (port 8002)
- ⚠️ Connection status shows "Unknown" (state_manager may need initialization)
- ⚠️ Engine status shows "Alive: False" (no recent ENGINE_TICK_CALLSITE processed)
- ⚠️ Recent events API shows 318 events but 0 connection events (may be filtered or not processed)

## What the Watchdog Tracks

### Connection Events (from robot logs):
1. **CONNECTION_LOST** - Initial disconnect detected
2. **CONNECTION_LOST_SUSTAINED** - Disconnect lasted ≥60 seconds (triggers notification)
3. **CONNECTION_RECOVERED** - Connection restored (log event)
4. **CONNECTION_RECOVERED_NOTIFICATION** - Recovery notification sent (NEW - after sustained disconnect)

### Recovery State Events:
1. **DISCONNECT_FAIL_CLOSED_ENTERED** - Engine entered fail-closed mode
2. **DISCONNECT_RECOVERY_STARTED** - Recovery process started
3. **DISCONNECT_RECOVERY_COMPLETE** - Recovery completed, execution unblocked
4. **DISCONNECT_RECOVERY_ABORTED** - Recovery aborted due to new disconnect

## Testing Checklist

### 1. Verify Watchdog Backend is Running
```bash
# Check if running
curl http://localhost:8000/api/watchdog/debug

# Or start it
python -m modules.watchdog.backend.main
# Or use batch file
batch\START_WATCHDOG_BACKEND.bat
```

### 2. Verify Connection Events are Tracked
```bash
# Check connection events in logs
python check_connection_events.py

# Check watchdog status
python check_watchdog_status.py
```

### 3. Test New Event Type
- Trigger a sustained disconnect (≥60 seconds)
- Verify CONNECTION_RECOVERED_NOTIFICATION appears in logs
- Verify watchdog shows recovery notification in UI

## Assessment Results

### ✅ What's Working
1. **Watchdog backend is running** (port 8002)
2. **Connection events are in feed file** (CONNECTION_LOST: 6, CONNECTION_RECOVERED: 35)
3. **CONNECTION_RECOVERED_NOTIFICATION is configured** in LIVE_CRITICAL_EVENT_TYPES
4. **Event processor updated** to handle CONNECTION_RECOVERED_NOTIFICATION

### ⚠️ Issues Found
1. **Connection status shows "Unknown"** in watchdog status API
   - State manager defaults to "Connected" but status shows "Unknown"
   - This suggests state_manager may not be initialized from recent events
   - Connection events exist in feed but may not be processed into state

2. **Engine status shows "Alive: False"**
   - Latest ENGINE_TICK_CALLSITE was 33 minutes ago
   - Robot engine appears to be stopped or not running
   - This is separate from connection event tracking

3. **Recent events API shows 0 connection events**
   - Feed has 35 CONNECTION_RECOVERED events
   - But `/api/watchdog/events` returns 0 connection events
   - Events may be filtered out or cursor is ahead

## Next Steps

1. **Verify State Manager Initialization**
   - Check if state_manager needs to rebuild connection status from recent events
   - Connection events exist in feed but state shows "Unknown"

2. **Check Event Processing**
   - Verify connection events are being processed by event_processor
   - Check if cursor position is preventing event processing

3. **Test Recovery Notification** - Trigger sustained disconnect (≥60s) to test new event
   - Should see CONNECTION_RECOVERED_NOTIFICATION in logs
   - Should appear in watchdog feed and UI

4. **Restart Robot Engine** - Engine appears stopped (no ticks for 33 minutes)
   - Once engine is running, watchdog should detect ticks and connection events

## Files Modified

1. `modules/watchdog/config.py` - Added CONNECTION_RECOVERED_NOTIFICATION to LIVE_CRITICAL_EVENT_TYPES
2. `modules/watchdog/event_processor.py` - Added CONNECTION_RECOVERED_NOTIFICATION to connection status handler
3. `modules/watchdog/aggregator.py` - Added CONNECTION_RECOVERED_NOTIFICATION to important_types and warning severity
4. `modules/watchdog/aggregator.py` - Added `_rebuild_connection_status_from_recent_events()` method to initialize connection status from feed
5. `modules/watchdog/aggregator.py` - Added automatic rebuild check in `_process_feed_events_sync()` and `start()` methods

## Connection Status Rebuild

### New Feature: Automatic Connection Status Initialization

Added `_rebuild_connection_status_from_recent_events()` method that:
- Scans the last 5000 lines of the feed file for connection events
- Finds the most recent connection event (CONNECTION_LOST, CONNECTION_LOST_SUSTAINED, CONNECTION_RECOVERED, CONNECTION_RECOVERED_NOTIFICATION)
- Processes that event to initialize connection status in state_manager
- Defaults to "Connected" if no connection events are found

**When it runs:**
1. **On startup** - Called in `start()` method if connection status is Unknown or uninitialized
2. **During event processing** - Called in `_process_feed_events_sync()` if connection status is Unknown or no connection events have been seen

This ensures connection status is always initialized from recent events, similar to how stream states are rebuilt.

## Notes

- The new CONNECTION_RECOVERED_NOTIFICATION event only fires after a sustained disconnect (≥60 seconds)
- It uses the same per-event-type rate limiter (5 minutes) to prevent spam
- Watchdog treats it as a warning-level event (informational, not critical)
- Both CONNECTION_RECOVERED and CONNECTION_RECOVERED_NOTIFICATION update connection status to "Connected"
- Connection status is now automatically rebuilt from recent feed events if Unknown or uninitialized
