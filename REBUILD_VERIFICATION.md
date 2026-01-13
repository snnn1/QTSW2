# Rebuild Verification Guide

## Source Code Status: ✅ ALL FIXED

All source code issues have been resolved:
1. ✅ `SetAccountInfo` method exists at `RobotEngine.cs:57` (public)
2. ✅ All `nowChicago` variables renamed (no conflicts)
3. ✅ `ConnectionStatus` fully qualified in both strategy files

## Compilation Errors Are From Stale DLL

The errors you're seeing indicate the NinjaTrader project is referencing an **outdated Robot.Core.dll** that doesn't contain the fixes.

## Required Actions

### Step 1: Rebuild Robot.Core Project

```powershell
# Navigate to Robot.Core project directory
cd modules\robot\core

# Clean and rebuild
dotnet clean
dotnet build --configuration Release
```

**Verify**: Check that `Robot.Core.dll` timestamp is recent in the output directory.

### Step 2: Update NinjaTrader Project Reference

**If using project reference:**
- Ensure the NinjaTrader solution includes the Robot.Core project
- Rebuild the entire solution

**If using DLL reference:**
- Update the reference path to point to the newly built `Robot.Core.dll`
- Location: `modules\robot\core\bin\Release\net8.0\Robot.Core.dll` (or your target framework)

### Step 3: Clean NinjaTrader Build Cache

In NinjaTrader:
1. Close NinjaTrader
2. Delete the `bin` and `obj` folders in your NinjaTrader strategy project directory
3. Reopen NinjaTrader
4. Rebuild the strategy

## Verification Checklist

After rebuilding, verify these exist:

- [ ] `RobotEngine.SetAccountInfo(string?, string?)` - public method
- [ ] No `nowChicago` variables in `StreamStateMachine.cs` (grep confirms: 0 matches)
- [ ] `QTSW2.Robot.Core.ConnectionStatus` fully qualified in:
  - `RobotSimStrategy.cs:244`
  - `RobotSkeletonStrategy.cs:147`

## If Errors Persist

1. **Check DLL timestamp**: Ensure `Robot.Core.dll` was rebuilt after your last code change
2. **Check reference path**: Verify NinjaTrader project references the correct DLL location
3. **Restart IDE**: Sometimes IDEs cache references
4. **Check target framework**: Ensure Robot.Core and NinjaTrader use compatible .NET versions

## Source Code Verification

Run these commands to verify source is correct:

```powershell
# Verify SetAccountInfo exists
Select-String -Path "modules\robot\core\RobotEngine.cs" -Pattern "public void SetAccountInfo"

# Verify no nowChicago variables
Select-String -Path "modules\robot\core\StreamStateMachine.cs" -Pattern "\bnowChicago\b"

# Verify ConnectionStatus is qualified
Select-String -Path "modules\robot\ninjatrader\*.cs" -Pattern "QTSW2\.Robot\.Core\.ConnectionStatus"
```

All should return expected results (SetAccountInfo found, no nowChicago matches, ConnectionStatus qualified).
