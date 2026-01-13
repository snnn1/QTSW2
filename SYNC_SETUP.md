# Robot.Core File Synchronization Setup

## Problem

The `RobotCore_For_NinjaTrader` directory contains duplicate source files that NinjaTrader compiles directly. These files can drift out of sync with the main `modules/robot/core` source files, causing compilation errors and inconsistencies.

## Solution

A PowerShell sync script (`sync_robotcore_to_ninjatrader.ps1`) has been created to automatically synchronize files from `modules/robot/core` to `RobotCore_For_NinjaTrader`.

## Automatic Sync (Pre-Build)

The `Robot.Core.csproj` file includes a pre-build step that automatically syncs files before each build:

```xml
<Target Name="PreBuild" BeforeTargets="PreBuildEvent">
  <Exec Command="powershell -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)\..\..\..\sync_robotcore_to_ninjatrader.ps1&quot;" ContinueOnError="true" />
</Target>
```

**This means**: Every time you build `Robot.Core`, the NinjaTrader directory is automatically updated with the latest source files.

## Manual Sync

To manually sync files (useful for testing or one-off updates):

```powershell
# From project root
.\sync_robotcore_to_ninjatrader.ps1

# Preview what would be synced
.\sync_robotcore_to_ninjatrader.ps1 -WhatIf

# Verbose output
.\sync_robotcore_to_ninjatrader.ps1 -Verbose
```

## Files Synced

The script syncs all core source files:
- Main directory files (RobotEngine.cs, StreamStateMachine.cs, etc.)
- Execution subdirectory files
- Notifications subdirectory files

**Note**: NinjaTrader-specific files (NinjaTraderBarProvider.cs, NinjaTraderBarProviderWrapper.cs, SnapshotParquetBarProvider.cs) are NOT synced, as they may have NinjaTrader-specific modifications.

## Verification

After syncing, verify files are synchronized:

```powershell
# Check if files are identical
Compare-Object (Get-Content modules\robot\core\StreamStateMachine.cs) (Get-Content RobotCore_For_NinjaTrader\StreamStateMachine.cs)
```

## Best Practices

1. **Always edit files in `modules/robot/core`** - Never edit files directly in `RobotCore_For_NinjaTrader`
2. **Build Robot.Core** - The pre-build step will automatically sync
3. **Verify sync** - If you see compilation errors in NinjaTrader, run the sync script manually

## Troubleshooting

### Sync fails with "file locked" error
- Close NinjaTrader IDE if it's open
- Ensure no other processes are using the files
- Try running sync again

### Files still out of sync after build
- Check that the pre-build step is enabled in your IDE
- Run sync manually: `.\sync_robotcore_to_ninjatrader.ps1`
- Verify the script path in `Robot.Core.csproj` is correct

### Need to exclude a file from sync
- Edit `sync_robotcore_to_ninjatrader.ps1` and add the file to the exclusion list
- Or manually manage that specific file

## Git Integration (Optional)

To automatically sync before commits, add to `.git/hooks/pre-commit`:

```bash
#!/bin/sh
powershell -ExecutionPolicy Bypass -File sync_robotcore_to_ninjatrader.ps1
```
