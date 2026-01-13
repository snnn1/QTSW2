# StreamStateMachine.cs Synchronization

## ⚠️ CRITICAL: Always Keep Files Synchronized

`StreamStateMachine.cs` exists in **two locations** and **MUST** remain identical:

1. **Source (EDIT HERE)**: `modules/robot/core/StreamStateMachine.cs`
2. **Copy (AUTO-SYNCED)**: `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

## Rules

### ✅ DO:
- **Always edit** `modules/robot/core/StreamStateMachine.cs`
- **Run sync** after making changes: `.\sync_robotcore_to_ninjatrader.ps1`
- **Verify sync** before committing: `.\verify_sync.ps1`

### ❌ DON'T:
- **Never edit** `RobotCore_For_NinjaTrader/StreamStateMachine.cs` directly
- **Never commit** changes to `RobotCore_For_NinjaTrader/StreamStateMachine.cs` without syncing

## Automatic Sync

The `Robot.Core.csproj` includes a **pre-build step** that automatically syncs files before each build. However, you should still:

1. **Verify sync** before committing changes
2. **Run sync manually** if you're not building via the project file

## Quick Commands

```powershell
# Verify files are synchronized
.\verify_sync.ps1

# Fix if out of sync
.\verify_sync.ps1 -Fix

# Full sync (all files)
.\sync_robotcore_to_ninjatrader.ps1

# Preview sync changes
.\sync_robotcore_to_ninjatrader.ps1 -WhatIf
```

## Why Two Files?

- `modules/robot/core/StreamStateMachine.cs` - Main source file (version controlled)
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` - Copy for NinjaTrader compilation

NinjaTrader requires files in a specific directory structure, so we maintain a synchronized copy.

## Troubleshooting

### Files are out of sync
```powershell
# Quick fix
.\verify_sync.ps1 -Fix

# Or full sync
.\sync_robotcore_to_ninjatrader.ps1
```

### Compilation errors in NinjaTrader
1. Check if files are synced: `.\verify_sync.ps1`
2. If out of sync, run: `.\sync_robotcore_to_ninjatrader.ps1`
3. Rebuild in NinjaTrader

### Accidentally edited the wrong file
1. Discard changes to `RobotCore_For_NinjaTrader/StreamStateMachine.cs`
2. Make your changes to `modules/robot/core/StreamStateMachine.cs`
3. Run sync: `.\sync_robotcore_to_ninjatrader.ps1`
