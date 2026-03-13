# Current Push Notifications

**Last Updated**: 2026-03-12

## Summary

**Robot (HealthMonitor)**: 3 active notifications  
**Watchdog**: 6 alert types (2 overlap with Robot, suppressible via config)  
**Total Disabled**: 2 (Robot only)

### Credential Unification

Robot and Watchdog should use the **same Pushover credentials** to avoid duplicate devices:
- Robot: `configs/robot/health_monitor.secrets.json`
- Watchdog: `configs/watchdog/notifications.secrets.json`

## ✅ Active Notifications (Currently Working)

### 1. Connection Lost (Sustained)

**Trigger**: NinjaTrader connection lost for 60+ seconds during active trading

**Conditions**:
- Connection status is `ConnectionLost`, `Disconnected`, or `ConnectionError`
- Disconnection sustained for ≥ 60 seconds
- Streams are active (ARMED, RANGE_BUILDING, or RANGE_LOCKED)
- HealthMonitor is enabled

**Notification**:
- **Title**: "Connection Lost (Sustained)"
- **Message**: "NinjaTrader connection lost for {elapsed}s: {connectionName}. Status: {status}"
- **Priority**: 2 (Emergency - bypasses rate limiting)
- **Rate Limit**: None (skipRateLimit: true)

**Code Location**: `HealthMonitor.cs` lines 89-162

**Event Logged**: `CONNECTION_LOST_SUSTAINED`

---

### 2. Engine Tick Stall

**Trigger**: Engine Tick() method has not been called for 120+ seconds during active trading

**Conditions**:
- Engine has been started (`_lastEngineTickUtc != MinValue`)
- Streams are active (ARMED, RANGE_BUILDING, or RANGE_LOCKED)
- No Tick() call for ≥ 120 seconds
- HealthMonitor is enabled

**Notification**:
- **Title**: "Engine Tick Stall Detected"
- **Message**: "Engine Tick() has not been called for {elapsed}s. Last tick: {timestamp} UTC. Threshold: 120s"
- **Priority**: 2 (Emergency - bypasses rate limiting)
- **Rate Limit**: None (skipRateLimit: true)

**Code Location**: `HealthMonitor.cs` lines 257-288

**Event Logged**: `ENGINE_TICK_STALL_DETECTED`

---

### 3. Mid-Session Restart

**Trigger**: NinjaTrader/robot restarted while a stream was active (mid-session restart detected on next startup)

**Conditions**:
- Journal exists for stream (stream was initialized before)
- Journal is not committed (stream was active when process exited)
- Current time is after range start (session had begun)
- HealthMonitor is enabled

**Why Needed**: When NinjaTrader crashes or restarts, `OnConnectionStatusUpdate` never fires because the process exits. This notification is sent on the *next* startup when the robot detects it was restarted mid-session.

**Notification**:
- **Title**: "NinjaTrader Mid-Session Restart"
- **Message**: "Robot restarted mid-session. Stream: {streamId}, trading date: {tradingDate}. Previous state: {previousState}. Restart at {time} UTC."
- **Priority**: 2 (Emergency - bypasses rate limiting)
- **Rate Limit**: None (skipPerKeyRateLimit: true)

**Code Location**: `HealthMonitor.cs` `OnMidSessionRestartDetected()`, called from `StreamStateMachine` when `isMidSessionRestart` is true

**Event Logged**: `MID_SESSION_RESTART_NOTIFICATION`

---

## Watchdog Notifications (External Monitor)

Watchdog runs as a separate Python service and sends alerts when Robot/NinjaTrader are unreachable or when log feeds stall.

### Alert Types

| Alert Type | Trigger | Suppressible |
|------------|---------|--------------|
| NINJATRADER_PROCESS_STOPPED | NinjaTrader.exe not running | No |
| ROBOT_HEARTBEAT_LOST | No engine ticks for 120s | Yes (Robot sends ENGINE_TICK_STALL) |
| CONNECTION_LOST_SUSTAINED | Connection lost 60+s | Yes (Robot sends CONNECTION_LOST) |
| POTENTIAL_ORPHAN_POSITION | Process down or heartbeat lost with active intents | No |
| CONFIRMED_ORPHAN_POSITION | Engine dead, no ticks, market open | No |
| LOG_FILE_STALLED | Log file not updated for threshold | No |

### suppress_robot_overlap

When Robot is running and sends its own CONNECTION_LOST / ENGINE_TICK_STALL notifications, Watchdog can suppress the equivalent alerts to avoid duplicates. Configure in `configs/watchdog/notifications.json`:

```json
"suppress_robot_overlap": {
  "CONNECTION_LOST_SUSTAINED": true,
  "ROBOT_HEARTBEAT_LOST": true
}
```

- **When Robot is running**: Robot sends first; Watchdog skips CONNECTION_LOST_SUSTAINED and ROBOT_HEARTBEAT_LOST.
- **When Robot/NinjaTrader crashed**: Watchdog still sends NINJATRADER_PROCESS_STOPPED, POTENTIAL_ORPHAN_POSITION, CONFIRMED_ORPHAN_POSITION.

### Rate Limits (Watchdog)

- `min_resend_interval_seconds`: 300 (5 min between resends for same alert)
- `max_alerts_per_hour`: 30

---

## ❌ Disabled Notifications (Not Currently Sent)

### 4. Data Loss Detected

**Status**: **DISABLED** (Log only)

**Would Trigger**: No bars received for an instrument for ≥ 180 seconds (configurable)

**Why Disabled**: 
- Handled by gap tolerance + range invalidation
- Comment: "Notification suppressed - handled by gap tolerance + range invalidation (log only)"

**Code Location**: `HealthMonitor.cs` lines 338-373 (line 369 commented out)

**Event Logged**: `DATA_LOSS_DETECTED` (but no notification sent)

---

### 5. Timetable Poll Stall

**Status**: **DISABLED** (Log only)

**Would Trigger**: Timetable polling has not occurred for ≥ 60 seconds

**Why Disabled**:
- Non-execution-critical
- Comment: "Notification suppressed - non-execution-critical (log only)"

**Code Location**: `HealthMonitor.cs` lines 294-321 (line 318 commented out)

**Event Logged**: `TIMETABLE_POLL_STALL_DETECTED` (but no notification sent)

---

## 🚫 Missing Notifications (Not Implemented)

These critical events are **NOT** wired to send notifications:

### 6. EXECUTION_GATE_INVARIANT_VIOLATION
- **Status**: Logged but no notification
- **Impact**: High - indicates execution logic bug
- **Occurrences**: 58 events on Jan 22

### 7. DISCONNECT_FAIL_CLOSED_ENTERED
- **Status**: Logged but no notification
- **Impact**: Critical - robot entered fail-closed mode
- **Occurrences**: 1 event on Jan 22

### 8. RANGE_COMPUTE_FAILED (Actionable)
- **Status**: Logged but no notification
- **Impact**: Medium - range computation failed with actionable reason
- **Occurrences**: 2337 events on Jan 22 (most are benign)

### 9. Complete System Crash (Now Partially Addressed)
- **Status**: Cannot detect
- **Impact**: Critical - NinjaTrader crashed completely
- **Partial Fix**: Mid-session restart notification (#3 above) fires when NinjaTrader restarts and robot comes back up - you get notified on the *next* startup. For immediate detection during crash, external process watchdog still needed.

---

## Notification Configuration

### Rate Limiting

**Default Rate Limit**: 600 seconds (10 minutes) between notifications of the same type

**Exceptions**:
- Emergency notifications (priority 2) bypass rate limiting
- Connection Lost: No rate limit
- Engine Tick Stall: No rate limit

**Configuration**: `health_monitor.json` → `min_notify_interval_seconds`

### Priority Levels

- **Priority 0**: Normal (default Pushover priority)
- **Priority 1**: High priority (bypasses quiet hours)
- **Priority 2**: Emergency (bypasses quiet hours + rate limiting)

---

## Notification Flow

1. **HealthMonitor** detects condition
2. **SendNotification()** called with key, title, message, priority
3. **Rate limiting** checked (unless priority 2)
4. **NotificationService.EnqueueNotification()** called
5. **Background worker** sends via PushoverClient
6. **Event logged**: `PUSHOVER_NOTIFY_ENQUEUED`

---

## Testing Notifications

To test if notifications work:

1. **Connection Lost Test**:
   - Disconnect NinjaTrader data feed
   - Wait 60+ seconds during active trading
   - Should receive: "Connection Lost (Sustained)"

2. **Engine Tick Stall Test**:
   - Stop NinjaTrader Tick() calls (not easily testable)
   - Wait 120+ seconds during active trading
   - Should receive: "Engine Tick Stall Detected"

---

## Limitations

### What Cannot Be Detected

1. **Complete System Crash**: If NinjaTrader crashes, robot code doesn't run
2. **Windows Crash**: System-level failures
3. **Process Termination**: If NinjaTrader is killed
4. **Critical Events**: EXECUTION_GATE_INVARIANT_VIOLATION, DISCONNECT_FAIL_CLOSED, etc.

### What Requires External Monitoring

- Process watchdog (check if NinjaTrader.exe is running)
- Windows Event Log monitoring
- Heartbeat monitoring (if robot stops sending heartbeats)

---

## Recommendations

### Immediate Improvements

1. **Add ReportCritical() method** to HealthMonitor
2. **Wire EXECUTION_GATE_INVARIANT_VIOLATION** to notifications
3. **Wire DISCONNECT_FAIL_CLOSED** to notifications
4. **Wire RANGE_COMPUTE_FAILED** (actionable only) to notifications

### Long-Term Improvements

1. **External Process Monitor**: Watchdog service to detect NinjaTrader crashes
2. **Heartbeat Monitoring**: If robot stops sending heartbeats, send alert
3. **Notification Policy Config**: Map event types to notification priorities
4. **Notification History**: Track all notification attempts and delivery status

---

## Related Files

- `modules/robot/core/HealthMonitor.cs` - Robot notification logic
- `modules/robot/core/Notifications/NotificationService.cs` - Robot background worker
- `modules/robot/core/Notifications/PushoverClient.cs` - Pushover API client
- `modules/watchdog/notifications/notification_service.py` - Watchdog notification service
- `modules/watchdog/aggregator.py` - Watchdog alert raising logic
- `configs/robot/health_monitor.secrets.json` - Robot Pushover credentials
- `configs/robot/health_monitor.json` - Robot health monitor configuration
- `configs/watchdog/notifications.json` - Watchdog notification config (suppress_robot_overlap, rate_limits)
- `configs/watchdog/notifications.secrets.json` - Watchdog Pushover credentials

**Credential unification**: Use the same Pushover `user_key` and `app_token` in both Robot and Watchdog secrets so all alerts go to the same device.
