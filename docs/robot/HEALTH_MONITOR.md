# Health Monitor + Pushover Alerts

## Overview

The Health Monitor is a fail-closed monitoring system that detects:
- **DATA_STALL**: No bars received for an instrument for X minutes during active monitoring windows
- **ROBOT_STALL**: Engine tick heartbeat not updating for Y seconds

Monitoring windows are automatically computed from enabled timetable streams using ParitySpec session ranges.

## Configuration

**File**: `configs/robot/health_monitor.json`

```json
{
  "enabled": false,
  "data_stall_seconds": 180,
  "robot_stall_seconds": 30,
  "grace_period_seconds": 300,
  "min_notify_interval_seconds": 600,
  "pushover_enabled": false,
  "pushover_user_key": "",
  "pushover_app_token": "",
  "pushover_priority": 0
}
```

### Configuration Options

- **enabled** (bool): Enable/disable health monitoring. Default: `false` (monitoring disabled).
- **data_stall_seconds** (int): Seconds without bars before DATA_STALL alert. Default: `180` (3 minutes).
- **robot_stall_seconds** (int): Seconds without tick heartbeat before ROBOT_STALL alert. Default: `30`.
- **grace_period_seconds** (int): Additional seconds added to monitoring window end time. Default: `300` (5 minutes).
- **min_notify_interval_seconds** (int): Minimum seconds between notifications for same incident. Default: `600` (10 minutes).
- **pushover_enabled** (bool): Enable Pushover notifications. Default: `false`.
- **pushover_user_key** (string): Pushover user key (from pushover.net).
- **pushover_app_token** (string): Pushover application token (create app at pushover.net).
- **pushover_priority** (int, optional): Notification priority (0=normal, 1=high, 2=emergency). Default: `0`.

### Fail-Closed Behavior

- If config file is missing: Monitoring disabled (no alerts)
- If config file is invalid: Monitoring disabled (no alerts)
- If `enabled=false`: Monitoring disabled (no alerts)
- Secrets (`pushover_user_key`, `pushover_app_token`) are never logged

## Monitoring Window Computation

Monitoring windows are computed automatically from enabled timetable streams:

1. **For each enabled stream** in the timetable:
   - Get session (S1 or S2) from stream directive
   - Get `range_start_time` from ParitySpec sessions
   - Get `slot_time` from timetable directive
   - Compute window: `[RangeStartChicagoTime, SlotTimeChicagoTime + grace_period]`

2. **Merge overlapping windows**: If multiple streams have overlapping windows, they are merged into a single continuous window.

3. **Log computed windows**: Once per timetable update, an `HEALTH_MONITOR_WINDOWS_COMPUTED` event is logged with all computed windows.

### Example

If timetable has:
- ES1 (S1, slot_time=09:00) → Window: [02:00, 09:00 + 5min] = [02:00, 09:05]
- ES2 (S2, slot_time=10:00) → Window: [08:00, 10:00 + 5min] = [08:00, 10:05]

Result: Two separate windows (non-overlapping).

If timetable has:
- ES1 (S1, slot_time=09:00) → Window: [02:00, 09:05]
- RTY1 (S1, slot_time=09:30) → Window: [02:00, 09:35]

Result: Merged into single window [02:00, 09:35] (overlapping).

## Incident Detection

### DATA_STALL

**Trigger Condition**:
- `nowUtc - lastBarUtcByInstrument[instrument] > data_stall_seconds`
- AND `IsMonitoringActiveNow(utcNow) == true` (within monitoring window)

**Behavior**:
- First detection: Mark incident active, send notification (priority 1 = high)
- Recovery: Clear incident flag, log recovery (no notification)
- Only one notification per incident until recovery

**Rate Limiting**: `min_notify_interval_seconds` per instrument (key: `DATA_STALL:{instrument}`)

### ROBOT_STALL

**Trigger Condition**:
- `nowUtc - lastEngineTickUtc > robot_stall_seconds`

**Behavior**:
- First detection: Mark incident active, send notification (priority 2 = emergency)
- Recovery: Clear incident flag, log recovery (no notification)
- Only one notification per incident until recovery

**Rate Limiting**: `min_notify_interval_seconds` (key: `ROBOT_STALL`)

## Session Gating

DATA_STALL alerts are only triggered when monitoring is active:

- `IsMonitoringActiveNow()` converts `utcNow` to Chicago time
- Checks if current Chicago time falls within union of monitoring windows
- Only triggers DATA_STALL alerts when `IsMonitoringActiveNow() == true`

ROBOT_STALL alerts are **not** gated by monitoring windows (robot should always be ticking).

## Rate Limiting

### Evaluation Rate Limit
- `Evaluate()` is called every tick (1 second)
- Internally rate-limited to max 1 evaluation per second
- Prevents excessive staleness checks

### Notification Rate Limit
- Per-incident: `min_notify_interval_seconds` between notifications for same key
- Keys: `DATA_STALL:{instrument}`, `ROBOT_STALL`
- Prevents notification spam for same incident

### Backpressure
- Notification queue max size: 1000
- If queue full: Drop INFO-level notifications (priority 0)
- Never drop ERROR-level notifications (priority 1, 2)

## Pushover Setup

1. **Create Pushover account**: https://pushover.net
2. **Create application**: https://pushover.net/apps/build
3. **Get credentials**:
   - User key: Found on Pushover dashboard
   - App token: Generated when creating application
4. **Configure**:
   - Set `pushover_enabled: true`
   - Set `pushover_user_key` to your user key
   - Set `pushover_app_token` to your app token
   - Optionally set `pushover_priority` (0=normal, 1=high, 2=emergency)

## Logging Events

All events are logged as ENGINE events (use `RobotEvents.EngineBase()`):

- **HEALTH_MONITOR_STARTED**: Config snapshot (no secrets) when monitor starts
- **HEALTH_MONITOR_WINDOWS_COMPUTED**: Computed monitoring windows (once per timetable update)
- **DATA_STALL_DETECTED**: Instrument, last bar time, elapsed, thresholds
- **DATA_STALL_RECOVERED**: Instrument recovered
- **ROBOT_STALL_DETECTED**: Last tick time, elapsed, threshold
- **ROBOT_STALL_RECOVERED**: Robot tick resumed
- **PUSHOVER_NOTIFY_SENT**: Notification enqueued (no secrets)

**Secrets Handling**: Never log `pushover_user_key` or `pushover_app_token`. Log `pushover_configured: true/false` instead.

## Architecture

```
RobotEngine
  ├── Tick() → HealthMonitor.OnEngineTick()
  ├── OnBar() → HealthMonitor.OnBar()
  └── ReloadTimetableIfChanged() → HealthMonitor.UpdateTimetable()

HealthMonitor
  ├── Evaluate() [periodic, rate-limited]
  ├── ComputeMonitoringWindows() [on timetable update]
  └── IsMonitoringActiveNow() [checks union of windows]

NotificationService [singleton]
  ├── EnqueueNotification()
  └── Background worker sends via PushoverClient

PushoverClient
  └── SendAsync() [HTTP POST to Pushover API]
```

## Non-Blocking Guarantees

- ✅ **No synchronous network calls** on bar/tick paths
- ✅ **Notifications are enqueued** (non-blocking)
- ✅ **Background worker** sends notifications asynchronously
- ✅ **Fail-closed**: Notification failures never crash the robot

## Testing

### Manual Test

1. Set `data_stall_seconds=30` in config
2. Enable monitoring: `enabled: true`
3. Configure Pushover credentials
4. Stop data feed or disable strategy
5. Wait 30+ seconds
6. Confirm phone receives push notification

### Monitoring Window Test

1. Load timetable with enabled streams
2. Check logs for `HEALTH_MONITOR_WINDOWS_COMPUTED` event
3. Verify windows computed correctly
4. Verify `IsMonitoringActiveNow()` returns true only during windows

## Default Behavior

When config is missing or invalid:
- Monitoring disabled (no alerts)
- No errors logged (fail-closed)
- Robot continues normal operation

This ensures monitoring is opt-in and never causes issues when disabled.
