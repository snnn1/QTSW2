# Current Push Notifications

**Last Updated**: 2026-01-22

## Summary

**Total Active Notifications**: **2**  
**Total Disabled Notifications**: **2**

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

## ❌ Disabled Notifications (Not Currently Sent)

### 3. Data Loss Detected

**Status**: **DISABLED** (Log only)

**Would Trigger**: No bars received for an instrument for ≥ 180 seconds (configurable)

**Why Disabled**: 
- Handled by gap tolerance + range invalidation
- Comment: "Notification suppressed - handled by gap tolerance + range invalidation (log only)"

**Code Location**: `HealthMonitor.cs` lines 338-373 (line 369 commented out)

**Event Logged**: `DATA_LOSS_DETECTED` (but no notification sent)

---

### 4. Timetable Poll Stall

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

### 5. EXECUTION_GATE_INVARIANT_VIOLATION
- **Status**: Logged but no notification
- **Impact**: High - indicates execution logic bug
- **Occurrences**: 58 events on Jan 22

### 6. DISCONNECT_FAIL_CLOSED_ENTERED
- **Status**: Logged but no notification
- **Impact**: Critical - robot entered fail-closed mode
- **Occurrences**: 1 event on Jan 22

### 7. RANGE_COMPUTE_FAILED (Actionable)
- **Status**: Logged but no notification
- **Impact**: Medium - range computation failed with actionable reason
- **Occurrences**: 2337 events on Jan 22 (most are benign)

### 8. Complete System Crash
- **Status**: Cannot detect
- **Impact**: Critical - NinjaTrader crashed completely
- **Problem**: Requires external monitoring (process watchdog)

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

- `modules/robot/core/HealthMonitor.cs` - Notification logic
- `modules/robot/core/Notifications/NotificationService.cs` - Background worker
- `modules/robot/core/Notifications/PushoverClient.cs` - Pushover API client
- `configs/robot/health_monitor.secrets.json` - Pushover credentials
- `configs/robot/health_monitor.json` - Health monitor configuration
