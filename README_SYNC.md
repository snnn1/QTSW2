# Robot.Core File Synchronization

## Overview

The `RobotCore_For_NinjaTrader` directory contains a copy of source files from `modules/robot/core` that NinjaTrader compiles directly. To keep these directories synchronized, use the sync script.

## Automatic Sync (Recommended)

The `Robot.Core.csproj` file includes a pre-build step that automatically syncs files before each build. This ensures NinjaTrader always has the latest source files.

## Manual Sync

To manually sync files, run:

```powershell
.\sync_robotcore_to_ninjatrader.ps1
```

### Options

- `-WhatIf`: Preview what would be synced without making changes
- `-Verbose`: Show detailed output for each file synced

### Examples

```powershell
# Preview changes
.\sync_robotcore_to_ninjatrader.ps1 -WhatIf

# Sync files with verbose output
.\sync_robotcore_to_ninjatrader.ps1 -Verbose

# Sync files silently
.\sync_robotcore_to_ninjatrader.ps1
```

## Files Synced

The script syncs all core source files, including:
- Main files (RobotEngine.cs, StreamStateMachine.cs, etc.)
- Execution subdirectory files
- Notifications subdirectory files

**Note**: NinjaTrader-specific files (NinjaTraderBarProvider.cs, etc.) are NOT synced as they may have NinjaTrader-specific modifications.

## Troubleshooting

If sync fails:
1. Ensure both directories exist
2. Check file permissions
3. Ensure no files are locked by another process (e.g., NinjaTrader IDE)

## Git Hooks (Optional)

To automatically sync before commits, add this to `.git/hooks/pre-commit`:

```bash
#!/bin/sh
powershell -ExecutionPolicy Bypass -File sync_robotcore_to_ninjatrader.ps1
```
