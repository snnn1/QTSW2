# Migration Guide: Old Scheduler → New Pipeline System

## Overview

This guide helps you migrate from the old monolithic scheduler (`daily_data_pipeline_scheduler.py`) to the new refactored pipeline system.

## What Changed

### Old System
- Single 1,565-line file with 10+ responsibilities
- Global state variables (`EVENT_LOG_PATH`, `stage_results`)
- Complex subprocess management embedded in orchestrator
- Infinite loop scheduler
- GUI logic mixed with pipeline logic

### New System
- Modular architecture with single-responsibility components
- No global state (configuration injected)
- Reusable services (ProcessSupervisor, FileManager, EventLogger)
- Simple runner script (can be called by OS scheduler)
- Headless-only (no GUI dependencies)

## Migration Steps

### Step 1: Verify New System Works

Run the dry run test (already done):
```bash
python -m automation.test_dry_run
```

Expected output: All tests pass ✅

### Step 2: Test Actual Pipeline Run

Run the new pipeline manually to verify it works:
```bash
python -m automation.pipeline_runner
```

This will:
- Check for raw files in `data/raw/`
- Run translator if raw files found
- Run analyzer if processed files found
- Run merger if analyzer succeeded
- Generate audit report
- Exit with appropriate code (0=success, 1=partial, 2=failure, 3=exception)

### Step 3: Set Up Windows Task Scheduler

#### Option A: Using Task Scheduler GUI

1. Open **Task Scheduler** (search in Windows Start menu)

2. Click **Create Basic Task** (or **Create Task** for advanced options)

3. **General Tab:**
   - Name: `Pipeline Runner`
   - Description: `Runs data pipeline every 15 minutes`
   - Check: **Run whether user is logged on or not**
   - Check: **Run with highest privileges**

4. **Triggers Tab:**
   - Click **New**
   - Begin: `On a schedule`
   - Settings: **Daily**
   - Start: Today's date, 00:00:00
   - Recur every: `15 minutes`
   - Repeat task every: `15 minutes`
   - For a duration of: **Indefinitely**
   - Click **OK**

5. **Actions Tab:**
   - Click **New**
   - Action: **Start a program**
   - Program/script: `python` (or full path: `C:\Users\jakej\AppData\Local\Programs\Python\Python313\python.exe`)
   - Add arguments: `-m automation.pipeline_runner`
   - Start in: `C:\Users\jakej\QTSW2`
   - Click **OK**

6. **Conditions Tab:**
   - Uncheck: **Start the task only if the computer is on AC power**
   - Check: **Wake the computer to run this task** (optional)

7. **Settings Tab:**
   - Check: **Allow task to be run on demand**
   - Check: **Run task as soon as possible after a scheduled start is missed**
   - If the task fails, restart every: `5 minutes`
   - Attempt to restart up to: `3 times`
   - Click **OK**

8. **Test the Task:**
   - Right-click the task → **Run**
   - Check the task history to verify it ran successfully

#### Option B: Using PowerShell Script

Create a PowerShell script to set up the task:

```powershell
# Create Task Scheduler entry for pipeline runner
$action = New-ScheduledTaskAction -Execute "python" -Argument "-m automation.pipeline_runner" -WorkingDirectory "C:\Users\jakej\QTSW2"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 15) -RepetitionDuration (New-TimeSpan -Days 365)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Highest

Register-ScheduledTask -TaskName "Pipeline Runner" -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description "Runs data pipeline every 15 minutes"
```

### Step 4: Stop Old Scheduler

If the old scheduler is running:

1. **Via Dashboard:**
   - Go to dashboard
   - Check scheduler status
   - Stop if running

2. **Via Process Manager:**
   - Open Task Manager
   - Find `python` process running `daily_data_pipeline_scheduler.py`
   - End task

3. **Via Command Line:**
   ```powershell
   Get-Process python | Where-Object {$_.CommandLine -like "*daily_data_pipeline_scheduler*"} | Stop-Process
   ```

### Step 5: Monitor New System

#### Via Dashboard
- Events will appear automatically in the dashboard
- Event log format is identical, so no dashboard changes needed
- Check "Live Events" panel for real-time updates

#### Via Log Files
- Pipeline logs: `automation/logs/pipeline_YYYYMMDD_HHMMSS.log`
- Event logs: `automation/logs/events/pipeline_{run_id}.jsonl`
- Audit reports: `automation/logs/pipeline_report_YYYYMMDD_HHMMSS_{run_id}.json`

#### Via Task Scheduler
- Open Task Scheduler
- Find "Pipeline Runner" task
- Right-click → **History** to see execution history
- Check **Last Run Result**: Should be `0x0` (success)

## Rollback Plan

If you need to rollback to the old system:

1. **Stop Task Scheduler task:**
   - Task Scheduler → Right-click "Pipeline Runner" → **Disable**

2. **Restart old scheduler:**
   ```bash
   python automation/daily_data_pipeline_scheduler.py --wait-for-schedule
   ```

3. **Or via dashboard:**
   - Use the scheduler start endpoint in the dashboard

## Verification Checklist

After migration, verify:

- [ ] Task Scheduler shows "Pipeline Runner" task
- [ ] Task runs every 15 minutes (check history)
- [ ] Events appear in dashboard "Live Events" panel
- [ ] Event log files created in `automation/logs/events/`
- [ ] Audit reports generated in `automation/logs/`
- [ ] Pipeline processes files correctly
- [ ] No errors in Task Scheduler history

## Troubleshooting

### Task Scheduler Not Running

**Problem:** Task shows "Last Run Result: 0x1" (failure)

**Solutions:**
1. Check Python path is correct in task action
2. Verify working directory is `C:\Users\jakej\QTSW2`
3. Check task runs with correct user permissions
4. Review task history for error details

### Events Not Appearing in Dashboard

**Problem:** Pipeline runs but events don't show in dashboard

**Solutions:**
1. Verify event log files are created: `automation/logs/events/pipeline_*.jsonl`
2. Check dashboard backend is reading from correct directory
3. Verify event log format matches (should be identical)
4. Restart dashboard backend if needed

### Pipeline Not Processing Files

**Problem:** Pipeline runs but doesn't process files

**Solutions:**
1. Check raw files exist in `data/raw/`
2. Verify file permissions
3. Check pipeline logs for errors
4. Verify translator/analyzer scripts are accessible

## Benefits of New System

✅ **Maintainable** - Clear separation of concerns  
✅ **Testable** - Each component can be unit tested  
✅ **Reliable** - No global state, deterministic behavior  
✅ **Survivable** - OS handles scheduling, not our code  
✅ **Debuggable** - Clear logging and error handling  
✅ **Production-Ready** - Follows quant pipeline best practices  

## Support

If you encounter issues:
1. Check pipeline logs: `automation/logs/pipeline_*.log`
2. Check audit reports: `automation/logs/pipeline_report_*.json`
3. Review Task Scheduler history
4. Check dashboard "Live Events" for error messages



