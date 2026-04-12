# Windows Task Scheduler Auto-Disable: How It Works & How to Prevent It

## How Windows Task Scheduler Auto-Disable Works

Windows Task Scheduler can **automatically disable tasks** under these conditions:

### 1. **Consecutive Failures**
- If a task fails multiple times in a row, Windows may disable it
- Default behavior: After a certain number of failures, Windows disables the task to prevent repeated failures

### 2. **Task Expiration**
- If `DeleteExpiredTaskAfter` is set, Windows may delete/disable expired tasks
- This can happen if the task's end date has passed

### 3. **Settings That Control Auto-Disable**

The key settings that prevent auto-disable are:

| Setting | Current Value | What It Does |
|---------|--------------|--------------|
| **RestartCount** | `3` (too low!) | Number of times to restart on failure. If set too low, Windows may disable after this many failures. |
| **RestartInterval** | `5 minutes` | Time to wait between restart attempts |
| **DeleteExpiredTaskAfter** | `Not set` | If set, Windows may delete/disable expired tasks |
| **ExecutionTimeLimit** | `2 hours` | Maximum time task can run before being stopped |

### 4. **Why Your Task Gets Disabled**

Your current task has:
- `RestartCount = 3` - **This is too low!**
- If the task fails 3 times in a row, Windows may disable it
- No explicit `DeleteExpiredTaskAfter = Never` setting

## How to Prevent Auto-Disable

### Solution 1: Update Existing Task (Recommended)

Run this script **as Administrator**:

```powershell
.\automation\prevent_scheduler_auto_disable.ps1
```

This script will:
- Set `RestartCount = 100` (high enough to prevent auto-disable, but not infinite)
- Set `DeleteExpiredTaskAfter = Never` (PT0S)
- Keep other settings optimized

**Important:** The application has its own retry logic (max 3 retries per stage). This Windows setting is for process-level failures (crashes), not application errors.

### Solution 2: Recreate Task with Correct Settings

If you need to recreate the task, the updated `setup_task_scheduler.ps1` now includes:
- `RestartCount = 999` (unlimited restarts)
- `DeleteExpiredTaskAfter = Never` (PT0S)
- All other settings optimized

Run:
```powershell
.\automation\setup_task_scheduler.ps1
```

### Solution 3: Manual Configuration (GUI)

1. Open **Task Scheduler** (`taskschd.msc`)
2. Find **"Pipeline Runner"** task
3. Right-click → **Properties**
4. Go to **Settings** tab
5. Configure:
   - **"If the task fails, restart every"**: `5 minutes`
   - **"Attempt to restart up to"**: `999 times` (or very high number)
   - **"If the task is not scheduled to run again, delete it after"**: **Uncheck** or set to `Never`
   - **"Stop the task if it runs longer than"**: `2 hours` (optional)
6. Click **OK**

## Key Settings Explained

### RestartCount
- **Low value (1-5)**: Windows may disable after this many failures
- **Medium value (50-100)**: Prevents auto-disable while still having a limit
- **Very high (999+)**: Effectively unlimited, but may mask persistent issues
- **Recommendation**: Set to `100` - prevents auto-disable but not infinite

**Note:** The application has its own retry logic (3 retries per stage). Windows `RestartCount` is for process crashes, not application errors.

### DeleteExpiredTaskAfter
- **Set to a time period**: Windows may delete/disable expired tasks
- **Set to Never (PT0S)**: Windows will never auto-delete/disable
- **Recommendation**: Set to `Never` (PT0S)

### RestartInterval
- Time to wait between restart attempts
- **Recommendation**: `5 minutes` (gives system time to recover)

## Two-Layer Retry Strategy

The system uses **two layers** of retry logic:

### 1. Application-Level Retries (Handles Transient Errors)
- Each pipeline stage retries up to **3 times** (configurable)
- Exponential backoff between retries
- Handles: API timeouts, network issues, temporary file locks
- **This is where most failures are handled**

### 2. Process-Level Retries (Handles Crashes)
- Windows Task Scheduler restarts the process up to **100 times**
- 5-minute delay between restarts
- Handles: Python crashes, exit codes, process termination
- **This prevents Windows from auto-disabling the task**

### Current Status

Your task currently has:
- ✅ `RestartInterval = 5 minutes` (good)
- ✅ `ExecutionTimeLimit = 2 hours` (good)
- ❌ `RestartCount = 3` (too low - may cause auto-disable)
- ❌ `DeleteExpiredTaskAfter` not explicitly set to Never

## What Happens After Fix

After running the prevention script:
- ✅ Task will restart up to **100 times** on process failure (prevents Windows auto-disable)
- ✅ Task will never be auto-deleted/disabled due to expiration
- ✅ Windows will not disable the task after failures
- ✅ Application's own retry logic (3 retries per stage) handles transient errors
- ✅ Persistent failures are logged in dashboard and event logs
- ✅ Watchdog monitors for hung runs and timeouts

**Result:** Task stays enabled, but persistent failures are still visible and logged (not hidden by infinite retries).

## Backend Auto-Re-Enable (Additional Protection)

The backend also has auto-re-enable logic:
- Checks scheduler state every 2 minutes
- If Windows disables the task but state file says "enabled", it auto-re-enables
- This provides **double protection** against auto-disable

## Summary

**To prevent auto-disable:**
1. Run `prevent_scheduler_auto_disable.ps1` as Administrator (one-time fix)
2. Or recreate task with updated `setup_task_scheduler.ps1` (includes fix)
3. Backend will also auto-re-enable if Windows disables it (ongoing protection)

**Result:** Task will stay enabled even after failures.

