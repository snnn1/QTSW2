# How To Control The Pipeline Scheduler

## Current Status

✅ **The scheduler is currently ENABLED and running every 15 minutes**

- **Schedule**: Every 15 minutes at :00, :15, :30, :45
- **Next Run**: Shows in Task Scheduler
- **Status**: Active/Ready

## Can You Stop It?

**Yes!** You can stop, disable, or modify the scheduler at any time.

## How To Stop/Disable The Scheduler

### Option 1: Disable Task (Recommended)

**This stops the scheduler but keeps it configured (easy to re-enable later)**

1. Open **Task Scheduler** (search in Windows Start menu)
2. Find **"Pipeline Runner"** task
3. Right-click → **Disable**
4. Task status changes to "Disabled" - scheduler stops running

**To Re-enable:**
- Right-click → **Enable**

### Option 2: Delete Task

**This completely removes the scheduler (need to recreate later)**

1. Open **Task Scheduler**
2. Find **"Pipeline Runner"** task
3. Right-click → **Delete**
4. Confirm deletion

**To Re-create:**
- Run `automation\setup_task_scheduler_simple.bat` as Administrator

### Option 3: Using Command Line

**Disable Task:**
```powershell
schtasks /change /tn "Pipeline Runner" /disable
```

**Enable Task:**
```powershell
schtasks /change /tn "Pipeline Runner" /enable
```

**Delete Task:**
```powershell
schtasks /delete /tn "Pipeline Runner" /f
```

## How To Modify The Schedule

### Change Frequency

1. Open **Task Scheduler**
2. Find **"Pipeline Runner"** task
3. Right-click → **Properties**
4. Go to **Triggers** tab
5. Select the trigger → **Edit**
6. Change **Repeat task every**: (e.g., 30 minutes, 1 hour)
7. Click **OK**

### Change Times

1. Open **Task Scheduler**
2. Find **"Pipeline Runner"** task
3. Right-click → **Properties**
4. Go to **Triggers** tab
5. Select the trigger → **Edit**
6. Change **Start time** or **Repeat interval**
7. Click **OK**

### Stop Schedule Temporarily

**Disable for a specific time period:**
1. Open **Task Scheduler**
2. Find **"Pipeline Runner"** task
3. Right-click → **Properties**
4. Go to **Triggers** tab
5. Select the trigger → **Edit**
6. Check **Enabled** checkbox to uncheck it
7. Click **OK**

## Current Schedule Details

Based on the current configuration:

- **Frequency**: Every 15 minutes
- **Times**: :00, :15, :30, :45 (every hour)
- **Duration**: Runs indefinitely (until disabled/deleted)
- **Stop if running**: Disabled (allows overlapping runs)

## Quick Reference

| Action | Method | Reversible? |
|--------|--------|-------------|
| **Stop temporarily** | Disable task | ✅ Yes (just Enable) |
| **Stop permanently** | Delete task | ⚠️ Need to recreate |
| **Change frequency** | Edit trigger | ✅ Yes |
| **Change times** | Edit trigger | ✅ Yes |
| **Pause for X hours** | Disable, then Enable later | ✅ Yes |

## Recommendations

### If You Want To Stop It:

1. **Temporarily**: Use **Disable** (easy to re-enable)
2. **Permanently**: Use **Delete** (need to recreate if you change your mind)

### If You Want To Change Schedule:

- **Less frequent**: Change trigger to 30 minutes, 1 hour, etc.
- **Specific times only**: Create multiple triggers for specific times
- **Weekdays only**: Add condition to run only on weekdays

## Example: Disable For Maintenance

```powershell
# Disable the scheduler
schtasks /change /tn "Pipeline Runner" /disable

# Do your maintenance work...

# Re-enable when done
schtasks /change /tn "Pipeline Runner" /enable
```

## Example: Change To Every 30 Minutes

1. Open Task Scheduler
2. Right-click "Pipeline Runner" → Properties
3. Triggers tab → Edit
4. Change "Repeat task every" to **30 minutes**
5. Click OK

## Summary

✅ **Yes, the scheduler runs every 15 minutes** (currently enabled)  
✅ **Yes, you can stop it** (disable or delete)  
✅ **Yes, you can modify it** (change frequency, times, etc.)  
✅ **Easy to re-enable** if you disable it

**Recommended**: Use **Disable** to stop it temporarily (easier to re-enable than recreating).


