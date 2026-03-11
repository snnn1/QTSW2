# MGC and M2K Strategy Loading Stuck - Assessment

## Recent Changes Made

### 1. Exception Handling Added (Prevent Crashes)
- **Files**: `RobotSimStrategy.cs`, `StreamStateMachine.cs`, `NinjaTraderSimAdapter.NT.cs`
- **Changes**: Wrapped all critical event handlers in try-catch blocks
- **Impact**: Should prevent crashes but could mask initialization errors

### 2. Enhanced Error Logging for MGC/M2K
- **Files**: `NinjaTraderSimAdapter.NT.cs` (both modules and RobotCore versions)
- **Changes**: 
  - Improved error extraction from OrderEventArgs
  - Added contract month fallback for instrument resolution
  - Enhanced diagnostic logging
- **Impact**: Better diagnostics but contract month logic might cause issues

### 3. Instrument Resolution Improvements
- **Files**: `NinjaTraderSimAdapter.NT.cs`
- **Changes**: 
  - Try exact match first (`Instrument.GetInstrument("M2K")`)
  - If fails, try with contract month (`Instrument.GetInstrument("M2K 03-26")`)
  - Fallback to strategy instrument
- **Potential Issue**: Contract month logic might be causing hangs if `Instrument.GetInstrument()` blocks

### 4. Compilation Fixes
- **Files**: `RobotSimStrategy.cs`, `NinjaTraderSimAdapter.NT.cs`
- **Changes**: Fixed brace structure, property access (OrderAction vs Action)
- **Impact**: Should not affect runtime behavior

## Potential Causes of "Stuck in Loading"

### 1. **Instrument Resolution Blocking** ⚠️ HIGH PROBABILITY
**Location**: `OnStateChange()` → `State.DataLoaded` → `Instrument.MasterInstrument.Name` (line 109)

**Issue**: 
- If `Instrument` is null or `MasterInstrument` is null, accessing `.Name` will throw `NullReferenceException`
- Even with try-catch, if exception occurs before adapter wiring, strategy might not transition properly

**Evidence**:
- M2K logs show `INSTRUMENT_RESOLUTION_FAILED` repeatedly
- MGC logs show `ORDER_SUBMIT_FAIL` but no initialization errors

**Fix Needed**: Add null checks and exception handling around instrument access in `OnStateChange`

### 2. **BarsRequest Hanging** ⚠️ MEDIUM PROBABILITY
**Location**: `OnStateChange()` → `State.DataLoaded` → `RequestHistoricalBarsForPreHydration()` (line 210)

**Issue**:
- `BarsRequest` is async but might block if instrument doesn't exist
- If `Instrument.GetInstrument()` hangs for M2K/MGC, BarsRequest never completes
- Strategy stays in DataLoaded state waiting for bars

**Evidence**:
- No recent logs showing BarsRequest completion for MGC/M2K
- Strategies stuck before reaching Realtime state

**Fix Needed**: Add timeout or check if instrument exists before requesting bars

### 3. **Engine.Start() Blocking** ⚠️ LOW PROBABILITY
**Location**: `OnStateChange()` → `State.DataLoaded` → `_engine.Start()` (line 118)

**Issue**:
- If engine initialization hangs (e.g., waiting for timetable, instrument resolution), strategy blocks
- No exception thrown, just hangs indefinitely

**Evidence**:
- Engine logs show initialization but might be incomplete

**Fix Needed**: Add timeout or async initialization

### 4. **WireNTContextToAdapter() Blocking** ⚠️ MEDIUM PROBABILITY
**Location**: `OnStateChange()` → `State.DataLoaded` → `WireNTContextToAdapter()` (line 231)

**Issue**:
- If adapter wiring tries to resolve instruments and hangs, strategy blocks
- M2K instrument resolution failures might cause this

**Evidence**:
- M2K has repeated instrument resolution failures
- Adapter might be trying to resolve M2K during wiring

**Fix Needed**: Add exception handling and timeout in WireNTContextToAdapter

## Recommended Fixes

### Priority 1: Add Exception Handling to OnStateChange
Wrap entire `State.DataLoaded` block in try-catch to prevent hangs:

```csharp
else if (State == State.DataLoaded)
{
    try
    {
        // ... existing code ...
    }
    catch (Exception ex)
    {
        Log($"CRITICAL: Exception during DataLoaded initialization: {ex.Message}. Strategy will not function.", LogLevel.Error);
        // Don't set _engineReady = true, preventing further execution
    }
}
```

### Priority 2: Add Null Checks for Instrument Access
Before accessing `Instrument.MasterInstrument.Name`, verify it exists:

```csharp
if (Instrument?.MasterInstrument == null)
{
    Log($"ERROR: Instrument or MasterInstrument is null. Cannot initialize. Aborting.", LogLevel.Error);
    return;
}
var engineInstrumentName = Instrument.MasterInstrument.Name;
```

### Priority 3: Add Timeout/Validation for Instrument Resolution
Before calling `Instrument.GetInstrument()`, validate instrument exists or add timeout:

```csharp
// Check if instrument exists before using it
var testInstrument = Instrument.GetInstrument(engineInstrumentName);
if (testInstrument == null)
{
    Log($"ERROR: Cannot resolve instrument '{engineInstrumentName}'. Strategy will not function.", LogLevel.Error);
    return;
}
```

### Priority 4: Make BarsRequest Non-Blocking
Add validation before requesting bars:

```csharp
// Verify instrument exists before requesting bars
var barsInstrument = Instrument.GetInstrument(instrumentName);
if (barsInstrument == null)
{
    Log($"WARNING: Cannot resolve instrument '{instrumentName}' for BarsRequest. Skipping.", LogLevel.Warning);
    return;
}
```

## Next Steps

1. Check NinjaTrader Output window for any errors during strategy loading
2. Add comprehensive exception handling to `OnStateChange()` 
3. Add null checks before instrument access
4. Add validation before BarsRequest
5. Test with M2K/MGC to see if strategies load successfully
