# NinjaTrader Restoration Errors - Fix Guide

## Problem
NinjaTrader is showing restoration errors for:
- `Exporter` indicator (5 instances)
- `RobotSimStrategy` strategies (3 instances)

## Root Cause
NinjaTrader has saved workspace/chart configurations that reference these items, but the compiled assembly has changed (rebuilt, properties changed, etc.), so the saved state doesn't match.

## Impact
✅ **These are warnings, not errors**
- The items themselves still work fine
- You just need to re-add them to charts if needed
- No functional impact on your robot

## Solutions

### Option 1: Ignore (Recommended)
These warnings are harmless. Just re-add indicators/strategies to charts as needed.

### Option 2: Clear Workspace Cache
1. **Close NinjaTrader completely**
2. Navigate to: `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\workspaces\`
3. **Backup** your workspace files (`.xml` files)
4. Delete or rename the workspace files that contain the saved configurations
5. Restart NinjaTrader

**Note**: This will remove all saved chart configurations, so you'll need to recreate your charts.

### Option 3: Recompile Cleanly
1. Close NinjaTrader
2. Delete `NinjaTrader.Custom.dll` and `NinjaTrader.Custom.pdb` from:
   - `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\bin\Release\`
   - `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\bin\Debug\`
3. Delete `obj\` folders in the Custom directory
4. Rebuild the project
5. Restart NinjaTrader

### Option 4: Remove Saved Configurations Manually
1. Open NinjaTrader
2. Go to each chart that has `Exporter` indicator
3. Remove the indicator from the chart
4. Save the workspace
5. Re-add `Exporter` if needed

## Prevention
To avoid these warnings in the future:
- Avoid changing public properties/methods of indicators/strategies that are saved in workspaces
- If you must change them, clear workspace cache first
- Or use version numbers in class names (e.g., `ExporterV2`)

## Verification
After applying a fix:
1. Restart NinjaTrader
2. Check if warnings still appear
3. Verify `Exporter` indicator still works (add to a chart)
4. Verify `RobotSimStrategy` still works (create a new strategy instance)

## Current Status
- ✅ `Exporter.cs` exists and compiles correctly
- ✅ `RobotSimStrategy.cs` exists and compiles correctly
- ⚠️ NinjaTrader just can't restore old saved configurations

**Recommendation**: Ignore these warnings unless they're causing actual problems. They're just NinjaTrader being cautious about restoring saved state.
