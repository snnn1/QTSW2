# Scheduler Toggle Fix

## Issue Found

The "Automation: OFF" button wasn't working because:

1. **Permission Issue**: The backend needs Administrator privileges to enable/disable Windows Task Scheduler tasks
2. **Status Check Bug**: The API status endpoint was using the wrong field to check if scheduler is enabled
3. **State File Migration**: The state file had old field names that weren't being migrated properly

## Fixes Applied

### 1. Fixed API Status Endpoint
- **File**: `modules/dashboard/backend/routers/schedule.py`
- **Change**: Now uses `scheduler.is_enabled()` method instead of non-existent `state.get("is_enabled")`
- **Result**: Status now correctly reflects Windows Task Scheduler state

### 2. Improved State File Migration
- **File**: `modules/dashboard/backend/orchestrator/scheduler.py`
- **Change**: Better handling of old `scheduler_enabled` field migration to `last_requested_enabled`
- **Result**: State file properly migrates old format

### 3. Enhanced Error Messages
- **File**: `modules/dashboard/frontend/src/hooks/usePipelineState.js`
- **Change**: Added detection for permission errors and shows clear message
- **Result**: Users see helpful message: "Backend must run as Administrator to enable/disable scheduler"

### 4. Status Refresh After Toggle
- **Change**: Added `pollPipelineStatus()` call after toggle to refresh UI
- **Result**: UI updates immediately after toggle attempt

## How to Use

### To Actually Disable the Scheduler:

**Option 1: Run Backend as Administrator (Recommended)**
1. Close the current backend
2. Right-click on the backend startup script/batch file
3. Select "Run as administrator"
4. Start the backend
5. Use the "Automation: OFF" button in the dashboard

**Option 2: Manual Disable**
1. Open Windows Task Scheduler (`taskschd.msc`)
2. Find task named "Pipeline Runner"
3. Right-click → Disable
4. The dashboard will reflect the change on next status check

### Current Status
- Windows Task Scheduler task: **ENABLED** (automation is ON)
- Backend can toggle: **Only if running as Administrator**
- Error message: Will now show clear permission error if backend lacks admin rights

## Testing

Run the diagnostic tool to check current status:
```bash
python tools/check_scheduler_status.py
```

Test disable operation:
```bash
python tools/test_scheduler_disable.py
```

## Summary

The scheduler toggle button **will work** if:
- ✅ Backend is running as Administrator
- ✅ Windows Task Scheduler task exists
- ✅ Task name is exactly "Pipeline Runner"

The button **will fail** if:
- ❌ Backend is not running as Administrator (permission denied)
- ❌ Task doesn't exist (need to run setup script)
- ❌ Task name mismatch

**Solution**: Restart the backend as Administrator to enable scheduler toggle functionality.








