# Health Monitor Quick Test Script
# Run: .\tests\test_health_monitor.ps1

Write-Host "`n=== HEALTH MONITOR TEST ===" -ForegroundColor Yellow

# 1. Check config exists
$configPath = "configs\robot\health_monitor.json"
if (Test-Path $configPath) {
    Write-Host "✓ Config file exists" -ForegroundColor Green
    try {
        $config = Get-Content $configPath | ConvertFrom-Json
        Write-Host "  Enabled: $($config.enabled)" -ForegroundColor Gray
        Write-Host "  Data stall threshold: $($config.data_stall_seconds)s" -ForegroundColor Gray
        Write-Host "  Robot stall threshold: $($config.robot_stall_seconds)s" -ForegroundColor Gray
        Write-Host "  Pushover enabled: $($config.pushover_enabled)" -ForegroundColor Gray
        Write-Host "  Grace period: $($config.grace_period_seconds)s" -ForegroundColor Gray
        Write-Host "  Min notify interval: $($config.min_notify_interval_seconds)s" -ForegroundColor Gray
        
        if ($config.pushover_enabled) {
            if ($config.pushover_user_key -and $config.pushover_app_token) {
                Write-Host "  Pushover: Configured" -ForegroundColor Green
            } else {
                Write-Host "  Pushover: Credentials missing!" -ForegroundColor Red
            }
        }
    } catch {
        Write-Host "✗ Config file invalid JSON: $_" -ForegroundColor Red
    }
} else {
    Write-Host "✗ Config file missing: $configPath" -ForegroundColor Red
}

# 2. Check recent logs for health monitor events
$logPath = "logs\robot\robot_skeleton.jsonl"
if (Test-Path $logPath) {
    Write-Host "`n✓ Checking recent logs..." -ForegroundColor Green
    try {
        $allLogs = Get-Content $logPath -Tail 500
        $healthEvents = $allLogs | ForEach-Object {
            try {
                $event = $_ | ConvertFrom-Json
                if ($event.event_type -like "*HEALTH*" -or $event.event_type -like "*STALL*") {
                    $event
                }
            } catch {
                # Skip invalid JSON lines
            }
        }
        
        if ($healthEvents) {
            Write-Host "  Found $($healthEvents.Count) health monitor events" -ForegroundColor Gray
            Write-Host "`n  Recent events:" -ForegroundColor Cyan
            $healthEvents | Select-Object -Last 5 | ForEach-Object {
                $time = if ($_.timestamp) { $_.timestamp } else { "N/A" }
                Write-Host "    [$time] $($_.event_type)" -ForegroundColor Gray
            }
        } else {
            Write-Host "  No health monitor events found (robot may not be running)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "  Error reading logs: $_" -ForegroundColor Red
    }
} else {
    Write-Host "`n⚠ Log file not found: $logPath" -ForegroundColor Yellow
    Write-Host "  (Robot may not have run yet)" -ForegroundColor Gray
}

# 3. Test recommendations
Write-Host "`n=== TEST RECOMMENDATIONS ===" -ForegroundColor Cyan
if ($config) {
    $dataStallTest = $config.data_stall_seconds + 10
    $robotStallTest = $config.robot_stall_seconds + 10
    Write-Host "1. Start robot → Check for HEALTH_MONITOR_STARTED event" -ForegroundColor Gray
    Write-Host "2. Load timetable → Check for HEALTH_MONITOR_WINDOWS_COMPUTED" -ForegroundColor Gray
    Write-Host "3. DATA_STALL test: Stop data feed for ${dataStallTest}s" -ForegroundColor Gray
    Write-Host "4. ROBOT_STALL test: Pause robot for ${robotStallTest}s" -ForegroundColor Gray
    Write-Host "5. Verify Pushover notifications arrive on phone" -ForegroundColor Gray
    Write-Host "6. Recovery test: Resume feed/robot → Check recovery events" -ForegroundColor Gray
} else {
    Write-Host "1. Create configs\robot\health_monitor.json" -ForegroundColor Gray
    Write-Host "2. Set enabled: true" -ForegroundColor Gray
    Write-Host "3. Configure Pushover credentials" -ForegroundColor Gray
    Write-Host "4. Start robot and run tests above" -ForegroundColor Gray
}

Write-Host "`n=== DETAILED TEST GUIDE ===" -ForegroundColor Cyan
Write-Host "See: docs\robot\HEALTH_MONITOR_TESTING.md" -ForegroundColor Gray
