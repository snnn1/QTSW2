# Why Windows Task Scheduler Auto-Disables Tasks

## Root Cause

Windows Task Scheduler has **built-in behavior** that automatically disables tasks after they fail. This is a **Windows feature, not a bug**, designed to prevent repeatedly failing tasks from consuming system resources.

## When Windows Auto-Disables Tasks

Windows Task Scheduler will automatically disable a task if:

1. **Multiple consecutive failures**: If a task fails (exits with non-zero code) multiple times in a row, Windows disables it
2. **After retry attempts exhausted**: With our current settings (`-RestartCount 3`), if a task fails 3 times (initial attempt + 3 retries), Windows will disable it
3. **Policy-based disabling**: System policies can disable tasks under certain conditions
4. **Resource exhaustion**: If a task consistently exceeds execution time limits or memory

## Current Configuration

Our PowerShell setup script (`automation/setup_task_scheduler.ps1`) includes:

```powershell
-RestartCount 3 `
-RestartInterval (New-TimeSpan -Minutes 5) `
-ExecutionTimeLimit (New-TimeSpan -Hours 2) `
-MultipleInstances IgnoreNew
```

This means:
- ✅ Task will retry 3 times if it fails (instead of immediately disabling)
- ✅ Retries happen every 5 minutes
- ✅ Task can run up to 2 hours before being killed
- ✅ Multiple instances can run simultaneously (won't disable on overlap)

## The Problem

Even with retry settings, **Windows will still disable the task** if all retries fail. This means:
- If the pipeline fails 4 times in a row (1 initial + 3 retries), Windows disables it
- The task stays disabled even if you fix the underlying issue
- You have to manually re-enable it (or use our auto-re-enable feature)

## Our Solution: Auto-Re-Enable

We've added **auto-re-enable logic** in `scheduler.py` that:

1. **Detects mismatches**: When `get_state()` is called, it checks if the scheduler is enabled in our state file but disabled in Windows
2. **Auto-re-enables**: If detected, it automatically calls `enable()` to re-enable the task
3. **Logs the action**: Warns when auto-re-enable occurs so you know what happened

**Code location**: `modules/dashboard/backend/orchestrator/scheduler.py:120-145`

```python
def get_state(self) -> dict:
    state = self._load_state()
    windows_status = self._check_windows_task_status()
    
    # Auto-re-enable if user wants it enabled but Windows has disabled it
    if (state.get("scheduler_enabled", False) and 
        windows_status.get("exists", False) and 
        not windows_status.get("enabled", False)):
        # Automatically re-enable
        self.enable(changed_by="auto_recovery")
```

## Why It Happens Even When Tasks Succeed

You might see the task get disabled even when it's currently succeeding because:

1. **Past failures**: If the task failed in the past (before you fixed an issue), Windows may have disabled it then
2. **Intermittent failures**: If the task fails occasionally (network issues, temporary errors), Windows accumulates these failures
3. **Check timing**: The auto-disable check happens after failures, so if you check right after a failure, it may be disabled even if previous runs succeeded

## Prevention Strategy

1. **Fix root causes**: Address pipeline failures to prevent Windows from disabling in the first place
2. **Monitor logs**: Check pipeline logs when auto-re-enable occurs to understand why it failed
3. **Auto-re-enable**: Our code now handles this automatically - just keep the scheduler enabled in the dashboard
4. **Re-run setup script**: If auto-re-enable isn't working, re-run `automation/setup_task_scheduler.ps1` to recreate the task with proper settings

## Manual Investigation

To see why a task was disabled:

1. **Check Task Scheduler GUI**:
   - Open Task Scheduler (`taskschd.msc`)
   - Find "Pipeline Runner" task
   - Check "Last Run Result" - non-zero means failure
   - Check "History" tab for failure events

2. **Check Event Logs**:
   ```powershell
   Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-TaskScheduler/Operational'} -MaxEvents 50
   ```

3. **Check Pipeline Logs**:
   - `automation/logs/pipeline_standalone.log`
   - `automation/logs/events/pipeline_*.jsonl`

## Summary

- ✅ **Windows auto-disables tasks after failures** - this is expected behavior
- ✅ **We've added auto-re-enable logic** - the dashboard will automatically re-enable it
- ✅ **Monitor logs** when auto-re-enable occurs to fix root causes
- ✅ **Task configuration** includes retry logic to minimize failures

The auto-re-enable feature ensures that even if Windows disables the task, it will be automatically re-enabled as long as the dashboard shows it as "enabled".

