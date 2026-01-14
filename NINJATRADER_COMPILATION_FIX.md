# NinjaTrader Compilation Fix

## Error
```
RobotSimStrategy.cs: No overload for method 'OnBar' takes 7 arguments (CS1501)
```

## Root Cause
NinjaTrader is using a cached/old version of `RobotEngine.OnBar()` that doesn't have the `open` parameter.

## Solution

### Option 1: Rebuild Robot.Core.dll (Recommended)

1. **Build the DLL:**
   ```powershell
   cd C:\Users\jakej\QTSW2
   dotnet build modules/robot/core/Robot.Core.csproj -c Release --no-incremental
   ```

2. **Copy the DLL to NinjaTrader:**
   ```powershell
   Copy-Item "modules\robot\core\bin\Release\net48\Robot.Core.dll" -Destination "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll" -Force
   ```

3. **In NinjaTrader:**
   - Close and reopen NinjaTrader
   - Tools → References → Remove old Robot.Core reference
   - Tools → References → Add → Browse to `Robot.Core.dll`
   - Recompile the strategy

### Option 2: If Using Source Files Directly

If NinjaTrader is compiling `RobotEngine.cs` directly (not using a DLL):

1. **Verify RobotEngine.cs is updated:**
   - Check that `RobotCore_For_NinjaTrader/RobotEngine.cs` line 390 has:
     ```csharp
     public void OnBar(DateTimeOffset barUtc, string instrument, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
     ```

2. **Clean NinjaTrader build:**
   - In NinjaTrader: Tools → Remove Compiled Assembly
   - Recompile the strategy

### Option 3: Verify File Sync

Run the sync script to ensure files are up to date:
```powershell
powershell -ExecutionPolicy Bypass -File sync_robotcore_to_ninjatrader.ps1
```

## Verification

After rebuilding, verify the signature matches:

**RobotEngine.cs line 390 should be:**
```csharp
public void OnBar(DateTimeOffset barUtc, string instrument, decimal open, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
```

**RobotSimStrategy.cs line 143 should be:**
```csharp
_engine.OnBar(barUtc, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
```

Both should have 7 parameters total.
