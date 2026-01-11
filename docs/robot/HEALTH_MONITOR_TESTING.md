# Health Monitor Testing Guide

## Quick Test Checklist

- [ ] **Configuration Test**: Verify config loads correctly
- [ ] **Monitoring Windows Test**: Verify windows computed from timetable
- [ ] **Data Stall Test**: Simulate missing bars → receive alert
- [ ] **Robot Stall Test**: Simulate engine freeze → receive alert
- [ ] **Pushover Test**: Verify notifications arrive on phone
- [ ] **Rate Limiting Test**: Verify no spam (max 1 notification per 10 min)
- [ ] **Recovery Test**: Verify recovery logged when issue resolves

---

## 1. Configuration Test

**Purpose**: Verify health monitor loads config correctly and starts.

**Steps**:
1. Ensure `configs/robot/health_monitor.json` exists with valid config
2. Start robot (NinjaTrader or harness)
3. Check logs for `HEALTH_MONITOR_STARTED` event

**Expected Log Event**:
```json
{
  "event_type": "HEALTH_MONITOR_STARTED",
  "config": {
    "enabled": true,
    "data_stall_seconds": 180,
    "robot_stall_seconds": 30,
    "grace_period_seconds": 300,
    "min_notify_interval_seconds": 600,
    "pushover_enabled": true,
    "pushover_configured": true,
    "pushover_priority": 0
  }
}
```

**Verification**:
- ✅ Event appears in logs
- ✅ Config values match your `health_monitor.json`
- ✅ Secrets (`pushover_user_key`, `pushover_app_token`) are NOT logged

**If config is missing/invalid**:
- ✅ No `HEALTH_MONITOR_STARTED` event
- ✅ No errors logged (fail-closed)
- ✅ Robot continues normally

---

## 2. Monitoring Windows Test

**Purpose**: Verify monitoring windows computed correctly from timetable.

**Steps**:
1. Load timetable with enabled streams (e.g., `ES1`, `ES2`)
2. Check logs for `HEALTH_MONITOR_WINDOWS_COMPUTED` event
3. Verify windows match expected session ranges

**Expected Log Event**:
```json
{
  "event_type": "HEALTH_MONITOR_WINDOWS_COMPUTED",
  "trading_date": "2026-01-09",
  "windows": [
    {
      "start_chicago": "2026-01-09T02:00:00-06:00",
      "end_chicago": "2026-01-09T09:05:00-06:00",
      "start_utc": "2026-01-09T08:00:00Z",
      "end_utc": "2026-01-09T15:05:00Z",
      "streams": ["ES1"]
    },
    {
      "start_chicago": "2026-01-09T08:00:00-06:00",
      "end_chicago": "2026-01-09T10:05:00-06:00",
      "start_utc": "2026-01-09T14:00:00Z",
      "end_utc": "2026-01-09T16:05:00Z",
      "streams": ["ES2"]
    }
  ]
}
```

**Verification**:
- ✅ Windows computed from enabled streams only
- ✅ Windows include grace period (slot_time + grace_period_seconds)
- ✅ Overlapping windows merged correctly
- ✅ Windows logged once per timetable update

**Manual Window Check**:
- During active window: `IsMonitoringActiveNow()` should return `true`
- Outside window: `IsMonitoringActiveNow()` should return `false`

---

## 3. Data Stall Test

**Purpose**: Verify DATA_STALL alert triggers when bars stop arriving.

### Test A: Quick Test (30 seconds)

**Steps**:
1. Set `data_stall_seconds: 30` in `health_monitor.json`
2. Set `enabled: true` and `pushover_enabled: true`
3. Start robot during active monitoring window
4. Let robot receive bars normally for 10 seconds
5. **Stop data feed** (disable strategy or disconnect NinjaTrader)
6. Wait 35 seconds (past threshold)
7. Check for `DATA_STALL_DETECTED` event and Pushover notification

**Expected Log Event**:
```json
{
  "event_type": "DATA_STALL_DETECTED",
  "instrument": "ES",
  "last_bar_utc": "2026-01-09T14:30:00Z",
  "now_utc": "2026-01-09T14:30:35Z",
  "elapsed_seconds": 35,
  "threshold_seconds": 30,
  "monitoring_active": true
}
```

**Expected Pushover Notification**:
- Title: `QTSW2 Robot: DATA_STALL`
- Message: `ES: No bars for 35s (threshold: 30s)`
- Priority: 1 (high)

**Verification**:
- ✅ Alert triggers after threshold exceeded
- ✅ Only triggers during active monitoring window
- ✅ Pushover notification received on phone
- ✅ Only one notification per incident (until recovery)

### Test B: Recovery Test

**Steps**:
1. After DATA_STALL detected, resume data feed
2. Wait for next bar to arrive
3. Check for `DATA_STALL_RECOVERED` event

**Expected Log Event**:
```json
{
  "event_type": "DATA_STALL_RECOVERED",
  "instrument": "ES",
  "recovery_bar_utc": "2026-01-09T14:31:00Z",
  "stall_duration_seconds": 60
}
```

**Verification**:
- ✅ Recovery logged when bars resume
- ✅ No recovery notification (only detection gets notification)
- ✅ Can trigger again if stall recurs (after `min_notify_interval_seconds`)

---

## 4. Robot Stall Test

**Purpose**: Verify ROBOT_STALL alert triggers when engine stops ticking.

### Test A: Quick Test (30 seconds)

**Steps**:
1. Set `robot_stall_seconds: 30` in `health_monitor.json`
2. Set `enabled: true` and `pushover_enabled: true`
3. Start robot
4. Let robot tick normally for 10 seconds
5. **Freeze robot** (pause NinjaTrader or kill process)
6. Wait 35 seconds (past threshold)
7. Check for `ROBOT_STALL_DETECTED` event and Pushover notification

**Expected Log Event**:
```json
{
  "event_type": "ROBOT_STALL_DETECTED",
  "last_tick_utc": "2026-01-09T14:30:00Z",
  "now_utc": "2026-01-09T14:30:35Z",
  "elapsed_seconds": 35,
  "threshold_seconds": 30
}
```

**Expected Pushover Notification**:
- Title: `QTSW2 Robot: ROBOT_STALL`
- Message: `Engine tick stopped for 35s (threshold: 30s)`
- Priority: 2 (emergency)

**Verification**:
- ✅ Alert triggers after threshold exceeded
- ✅ **NOT gated by monitoring windows** (robot should always tick)
- ✅ Pushover notification received on phone
- ✅ Only one notification per incident (until recovery)

### Test B: Recovery Test

**Steps**:
1. After ROBOT_STALL detected, resume robot (unpause NinjaTrader)
2. Wait for next tick
3. Check for `ROBOT_STALL_RECOVERED` event

**Expected Log Event**:
```json
{
  "event_type": "ROBOT_STALL_RECOVERED",
  "recovery_tick_utc": "2026-01-09T14:31:00Z",
  "stall_duration_seconds": 60
}
```

**Verification**:
- ✅ Recovery logged when ticks resume
- ✅ No recovery notification (only detection gets notification)

---

## 5. Pushover Notification Test

**Purpose**: Verify Pushover integration works end-to-end.

**Steps**:
1. Configure Pushover credentials in `health_monitor.json`
2. Set `pushover_enabled: true`
3. Trigger a DATA_STALL or ROBOT_STALL (see tests above)
4. Check phone for notification

**Expected Behavior**:
- ✅ Notification appears on phone within 5-10 seconds
- ✅ Title includes robot name and incident type
- ✅ Message includes instrument (if DATA_STALL) and timing details
- ✅ Priority matches incident type (DATA_STALL=1, ROBOT_STALL=2)

**If notification doesn't arrive**:
1. Check `PUSHOVER_NOTIFY_SENT` event in logs (confirms enqueue)
2. Check Pushover credentials are correct
3. Check internet connectivity
4. Check Pushover app is installed and logged in on phone
5. Check notification queue isn't full (max 1000)

**Log Event**:
```json
{
  "event_type": "PUSHOVER_NOTIFY_SENT",
  "incident_type": "DATA_STALL",
  "instrument": "ES",
  "priority": 1,
  "queue_size": 1
}
```

---

## 6. Rate Limiting Test

**Purpose**: Verify notifications aren't spammed.

**Steps**:
1. Set `min_notify_interval_seconds: 60` (for quick test)
2. Trigger DATA_STALL (stop data feed)
3. Wait for first notification
4. Keep data feed stopped for 2 minutes
5. Verify only **one notification** received (not multiple)

**Expected Behavior**:
- ✅ First notification arrives after threshold exceeded
- ✅ No additional notifications for same incident
- ✅ After recovery + new incident, new notification allowed

**Verification**:
- ✅ Check logs: Only one `PUSHOVER_NOTIFY_SENT` per incident key
- ✅ Check phone: Only one notification per incident
- ✅ Rate limit key: `DATA_STALL:{instrument}` or `ROBOT_STALL`

---

## 7. Session Gating Test

**Purpose**: Verify DATA_STALL only triggers during active monitoring windows.

**Steps**:
1. Set `data_stall_seconds: 60` (1 minute)
2. Start robot **outside** monitoring window (e.g., 12:00 Chicago time)
3. Stop data feed
4. Wait 2 minutes
5. Verify **NO** DATA_STALL alert (monitoring inactive)
6. Start robot **during** monitoring window (e.g., 08:30 Chicago time)
7. Stop data feed
8. Wait 2 minutes
9. Verify **DATA_STALL alert** triggers (monitoring active)

**Expected Behavior**:
- ✅ DATA_STALL only triggers when `IsMonitoringActiveNow() == true`
- ✅ ROBOT_STALL triggers regardless of monitoring window (always active)

**Verification**:
- ✅ Check logs: `monitoring_active: false` in DATA_STALL events outside windows
- ✅ Check logs: `monitoring_active: true` in DATA_STALL events during windows

---

## 8. Fail-Closed Test

**Purpose**: Verify monitoring failures don't crash robot.

**Test Scenarios**:

### A. Missing Config File
1. Rename `health_monitor.json` to `health_monitor.json.bak`
2. Start robot
3. Verify robot starts normally
4. Verify no errors logged
5. Verify no monitoring events logged

### B. Invalid Config File
1. Edit `health_monitor.json` with invalid JSON
2. Start robot
3. Verify robot starts normally
4. Verify no errors logged
5. Verify no monitoring events logged

### C. Missing Pushover Credentials
1. Set `pushover_enabled: true` but leave credentials empty
2. Start robot
3. Verify robot starts normally
4. Verify `HEALTH_MONITOR_STARTED` event shows `pushover_configured: false`
5. Verify no Pushover notifications sent (but monitoring still active)

### D. Network Failure (Pushover API Down)
1. Configure valid Pushover credentials
2. Disconnect internet
3. Trigger DATA_STALL
4. Verify `PUSHOVER_NOTIFY_SENT` event logged (enqueued)
5. Verify robot continues normally (no crash)
6. Verify notification sent when internet restored

---

## 9. Integration Test Script

**Quick test script** (run in PowerShell):

```powershell
# Test Health Monitor Configuration
Write-Host "=== HEALTH MONITOR TEST ===" -ForegroundColor Yellow

# 1. Check config exists
$configPath = "configs\robot\health_monitor.json"
if (Test-Path $configPath) {
    Write-Host "✓ Config file exists" -ForegroundColor Green
    $config = Get-Content $configPath | ConvertFrom-Json
    Write-Host "  Enabled: $($config.enabled)" -ForegroundColor Gray
    Write-Host "  Data stall threshold: $($config.data_stall_seconds)s" -ForegroundColor Gray
    Write-Host "  Robot stall threshold: $($config.robot_stall_seconds)s" -ForegroundColor Gray
    Write-Host "  Pushover enabled: $($config.pushover_enabled)" -ForegroundColor Gray
} else {
    Write-Host "✗ Config file missing" -ForegroundColor Red
}

# 2. Check recent logs for health monitor events
$logPath = "logs\robot\robot_skeleton.jsonl"
if (Test-Path $logPath) {
    Write-Host "`n✓ Checking recent logs..." -ForegroundColor Green
    $recentLogs = Get-Content $logPath -Tail 100 | ConvertFrom-Json | Where-Object { $_.event_type -like "*HEALTH*" -or $_.event_type -like "*STALL*" }
    if ($recentLogs) {
        Write-Host "  Found $($recentLogs.Count) health monitor events" -ForegroundColor Gray
        $recentLogs | Select-Object -Last 5 event_type, timestamp | Format-Table
    } else {
        Write-Host "  No health monitor events found (robot may not be running)" -ForegroundColor Yellow
    }
} else {
    Write-Host "✗ Log file not found" -ForegroundColor Red
}

# 3. Test recommendations
Write-Host "`n=== TEST RECOMMENDATIONS ===" -ForegroundColor Cyan
Write-Host "1. Start robot and check for HEALTH_MONITOR_STARTED event" -ForegroundColor Gray
Write-Host "2. Load timetable and check for HEALTH_MONITOR_WINDOWS_COMPUTED" -ForegroundColor Gray
Write-Host "3. Stop data feed for $(if ($config) { $config.data_stall_seconds + 10 } else { 190 })s to test DATA_STALL" -ForegroundColor Gray
Write-Host "4. Pause robot for $(if ($config) { $config.robot_stall_seconds + 10 } else { 40 })s to test ROBOT_STALL" -ForegroundColor Gray
Write-Host "5. Verify Pushover notifications arrive on phone" -ForegroundColor Gray
```

**Save as**: `tests/test_health_monitor.ps1`

---

## 10. Automated Test (Python)

**Create**: `tests/test_health_monitor.py`

```python
"""Test health monitor configuration and log events."""
import json
from pathlib import Path
from datetime import datetime, timedelta

def test_config_exists():
    """Test: Config file exists and is valid JSON."""
    config_path = Path("configs/robot/health_monitor.json")
    assert config_path.exists(), "health_monitor.json missing"
    
    with open(config_path) as f:
        config = json.load(f)
    
    assert "enabled" in config
    assert "data_stall_seconds" in config
    assert "robot_stall_seconds" in config
    return config

def test_config_values(config):
    """Test: Config values are reasonable."""
    assert config["data_stall_seconds"] >= 30, "data_stall_seconds too low"
    assert config["robot_stall_seconds"] >= 10, "robot_stall_seconds too low"
    assert config["min_notify_interval_seconds"] >= 60, "min_notify_interval too low"
    
    if config.get("pushover_enabled"):
        assert config.get("pushover_user_key"), "pushover_user_key missing"
        assert config.get("pushover_app_token"), "pushover_app_token missing"

def test_log_events():
    """Test: Check logs for health monitor events."""
    log_path = Path("logs/robot/robot_skeleton.jsonl")
    if not log_path.exists():
        print("⚠ Log file not found (robot may not be running)")
        return
    
    events = []
    with open(log_path) as f:
        for line in f:
            try:
                event = json.loads(line)
                if "HEALTH" in event.get("event_type", "") or "STALL" in event.get("event_type", ""):
                    events.append(event)
            except:
                continue
    
    print(f"Found {len(events)} health monitor events")
    return events

if __name__ == "__main__":
    print("=== Health Monitor Tests ===")
    
    config = test_config_exists()
    print("✓ Config file valid")
    
    test_config_values(config)
    print("✓ Config values reasonable")
    
    events = test_log_events()
    if events:
        print(f"✓ Found {len(events)} health monitor events in logs")
    else:
        print("⚠ No health monitor events found (robot may not be running)")
    
    print("\n=== Manual Tests Required ===")
    print("1. Start robot → Check for HEALTH_MONITOR_STARTED")
    print("2. Load timetable → Check for HEALTH_MONITOR_WINDOWS_COMPUTED")
    print("3. Stop data feed → Check for DATA_STALL_DETECTED")
    print("4. Pause robot → Check for ROBOT_STALL_DETECTED")
    print("5. Verify Pushover notifications arrive")
```

**Run**: `python tests/test_health_monitor.py`

---

## Troubleshooting

### No notifications received
1. Check `pushover_enabled: true` in config
2. Check Pushover credentials are correct
3. Check phone has Pushover app installed and logged in
4. Check internet connectivity
5. Check logs for `PUSHOVER_NOTIFY_SENT` events (confirms enqueue)
6. Check notification queue size (max 1000)

### False alarms (too sensitive)
1. Increase `data_stall_seconds` if using 5-minute bars (recommend 600s)
2. Increase `robot_stall_seconds` if robot has legitimate pauses
3. Verify bar interval matches threshold (2-3x bar interval recommended)

### Alerts not triggering
1. Check `enabled: true` in config
2. Check monitoring windows computed correctly (check logs)
3. Verify `IsMonitoringActiveNow()` returns true during test
4. Check threshold values are reasonable

### Monitoring windows incorrect
1. Check timetable has enabled streams
2. Check ParitySpec sessions configured correctly
3. Check `grace_period_seconds` added correctly
4. Verify Chicago timezone conversion (DST-aware)

---

## Summary

**Quick Test Sequence**:
1. ✅ Config loads → Check `HEALTH_MONITOR_STARTED`
2. ✅ Windows computed → Check `HEALTH_MONITOR_WINDOWS_COMPUTED`
3. ✅ Data stall → Stop feed, wait threshold+10s, check alert
4. ✅ Robot stall → Pause robot, wait threshold+10s, check alert
5. ✅ Pushover → Verify notification on phone
6. ✅ Rate limit → Verify only one notification per incident
7. ✅ Recovery → Resume feed/robot, check recovery event

**Expected Results**:
- ✅ All events logged correctly
- ✅ Pushover notifications arrive
- ✅ No false alarms
- ✅ No spam (rate limiting works)
- ✅ Robot continues normally if monitoring fails
