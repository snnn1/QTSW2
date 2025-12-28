# Scheduler Enable/Disable Requires Administrator Privileges

## Issue

When trying to enable or disable the scheduler via the dashboard, you may see:
```
Access is denied
HRESULT 0x80070005
```

## Root Cause

**Windows Task Scheduler requires administrator privileges to enable/disable tasks**, even if the task itself runs with Limited privileges. This is a Windows security boundary, not a bug in the code.

## Why This Happens

The "Pipeline Runner" task is created with:
- **RunLevel**: Limited (no admin privileges needed to run)
- **Principal**: Current user (S4U logon)

However, **modifying the task's enabled state** (enable/disable) requires administrator privileges in Windows, regardless of how the task was created.

## Solutions

### Option 1: Run Backend as Administrator (Recommended)

1. **Close the current backend** (if running)
2. **Right-click** `batch\START_DASHBOARD.bat`
3. **Select "Run as administrator"**
4. **Click "Yes"** when prompted by Windows
5. **Re-open the dashboard** in your browser

This allows the backend to enable/disable the scheduler task.

### Option 2: Enable/Disable Manually

If you don't want to run the backend as administrator:

1. **Open Task Scheduler**: Press `Win + R`, type `taskschd.msc`, press Enter
2. **Find the task**: Look for "Pipeline Runner" in the task list
3. **Right-click** the task
4. **Select "Enable"** or "Disable" as needed

The dashboard will still show the correct status (it can read the status, just not change it).

### Option 3: Use PowerShell as Administrator

1. **Open PowerShell as Administrator** (right-click → Run as administrator)
2. **Run**:
   ```powershell
   # To enable
   Enable-ScheduledTask -TaskName "Pipeline Runner"
   
   # To disable
   Disable-ScheduledTask -TaskName "Pipeline Runner"
   ```

## Verification

After running the backend as administrator, you should see:
```
✓ Windows Task Scheduler ENABLED (task: Pipeline Runner, changed_by: dashboard)
```

Instead of:
```
Access is denied
```

## Note

This is a **Windows security feature**, not a bug. The task can run with Limited privileges, but modifying its configuration (including enabled state) requires administrator privileges for security reasons.

