# Strategy and Exporter Missing - Fix Guide

## Problem
`RobotSimStrategy` and `Exporter` are not appearing in NinjaTrader, even though the files exist.

## Root Cause
NinjaTrader needs to recompile the project. The DLL might be outdated or there might be compilation errors preventing them from loading.

## Quick Fix Steps

### Step 1: Force NinjaTrader to Recompile
1. **Close NinjaTrader completely** (check Task Manager to ensure it's fully closed)
2. Delete the compiled DLL:
   ```
   Delete: C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\NinjaTrader.Custom.dll
   Delete: C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\NinjaTrader.Custom.pdb (if exists)
   ```
3. **Restart NinjaTrader** - It will automatically recompile

### Step 2: Check for Compilation Errors
1. In NinjaTrader, go to: **Tools → Compile**
2. Check the **Output** window for any errors
3. Fix any compilation errors that appear

### Step 3: Verify Files Are Included
The files should be in the `.csproj`:
- ✅ `Strategies\RobotSimStrategy.cs` (line 373)
- ✅ `Indicators\Exporter.cs` (line 371)

### Step 4: Check Namespace
Both files must use the correct namespace:
- **Strategy**: `namespace NinjaTrader.NinjaScript.Strategies`
- **Indicator**: `namespace NinjaTrader.NinjaScript.Indicators`

Both are correct ✅

## Alternative: Manual Rebuild

If the above doesn't work, rebuild manually:

1. **Close NinjaTrader**
2. Open Visual Studio or use command line:
   ```powershell
   cd "c:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom"
   dotnet build NinjaTrader.Custom.csproj -c Release
   ```
3. **Restart NinjaTrader**

## Verification

After restarting NinjaTrader:
1. **Check Strategies**: Tools → Strategies → Search for "RobotSimStrategy"
2. **Check Indicators**: Right-click chart → Indicators → Search for "Exporter"
3. **Check Output Window**: Tools → Output → Look for compilation errors

## Common Issues

### Issue 1: Missing Robot.Core.dll Reference
- **Symptom**: Compilation errors about `QTSW2.Robot.Core` namespace
- **Fix**: Ensure `Robot.Core.dll` is in the Custom folder and referenced in the project

### Issue 2: Preprocessor Directive Not Defined
- **Symptom**: Warning about `NINJATRADER` not defined
- **Fix**: The `.csproj` already has `<DefineConstants>NINJATRADER</DefineConstants>` - rebuild should fix it

### Issue 3: Files Not in .csproj
- **Symptom**: Files exist but aren't compiled
- **Fix**: Verify files are listed in `NinjaTrader.Custom.csproj` (they are ✅)

## If Still Not Working

1. **Check NinjaTrader Logs**:
   - `Documents\NinjaTrader 8\log\` folder
   - Look for compilation errors

2. **Verify File Permissions**:
   - Ensure files are not read-only
   - Ensure NinjaTrader has write access to Custom folder

3. **Check for Duplicate Files**:
   - Search for duplicate `RobotSimStrategy.cs` or `Exporter.cs` files
   - Remove duplicates

4. **Clean Build**:
   - Delete `obj\` folders
   - Delete `bin\` folders (except Robot.Core.dll)
   - Rebuild

## Expected Result

After following these steps:
- ✅ `RobotSimStrategy` appears in Tools → Strategies
- ✅ `Exporter` appears in chart Indicators list
- ✅ No compilation errors in Output window
