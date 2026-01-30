# Canonical vs Execution Instrument Assessment

**Date**: 2026-01-30  
**Purpose**: Comprehensive assessment of canonical vs execution instrument usage to identify issues similar to the `PHASE 3 ASSERTION FAILED` error.

## Executive Summary

After fixing the critical issue where `StreamStateMachine` was initialized with canonical instruments instead of execution instruments, a comprehensive code review was performed to identify similar issues. **One additional issue was found** that could cause BarsRequest failures during restart scenarios.

## Issues Found

### ✅ FIXED: StreamStateMachine Constructor (Critical)
**Location**: `RobotEngine.cs` lines ~3075-3098  
**Issue**: `StreamStateMachine` constructor was receiving `directive.instrument` (canonical, e.g., "GC") instead of the resolved execution instrument (e.g., "MGC").  
**Fix**: Created `modifiedDirective` with `instrument = executionInstrument` before passing to constructor.  
**Status**: ✅ Fixed and synced

### ✅ FIXED: GetInstrumentsNeedingRestartBarsRequest Uses Canonical Instrument
**Location**: `RobotEngine.cs` line 4057 (now fixed)  
**Issue**: Method was using `stream.CanonicalInstrument` instead of `stream.ExecutionInstrument` when determining which instruments need BarsRequest for restart.  
**Impact**: 
- If a stream has `ExecutionInstrument = MGC` and `CanonicalInstrument = GC`, the method would return `GC`
- Strategy would request `GC` bars instead of `MGC` bars
- BarsRequest for `GC` might fail or return wrong data
- Streams would not receive the correct historical bars for restart

**Fix Applied**: Changed line 4057 from:
```csharp
var canonicalInstrument = stream.CanonicalInstrument;
```
to:
```csharp
var executionInstrument = stream.ExecutionInstrument;
```

And updated all references to use `executionInstrument` instead of `canonicalInstrument` in this method, including logging.

**Rationale**: 
- `GetInstrumentsForBarsRequest()` correctly uses `ExecutionInstrument` (line 3978-3980)
- BarsRequest must request bars for the execution instrument (MGC), which then get canonicalized when routed to streams
- The tracking in `MarkBarsRequestPending()` canonicalizes for internal tracking, but the actual request must use execution instrument

**Status**: ✅ Fixed in both `RobotCore_For_NinjaTrader/RobotEngine.cs` and `modules/robot/core/RobotEngine.cs`

### ✅ FIXED: Outdated Comment
**Location**: `RobotEngine.cs` lines 3985-3986 (now fixed)  
**Issue**: Comment stated "If stream has ExecutionInstrument = YM, we need to request MYM bars" - but after our fix, `ExecutionInstrument` should already be `MYM`, not `YM`.  
**Impact**: Misleading comment, but code logic was correct (checks if base instrument and maps to micro, otherwise uses as-is).  
**Fix Applied**: Updated comment to reflect that ExecutionInstrument is already the execution instrument (micro if applicable).

**Status**: ✅ Fixed in both `RobotCore_For_NinjaTrader/RobotEngine.cs` and `modules/robot/core/RobotEngine.cs`

## Verified Correct Usage

### ✅ Order Submission
**Location**: `StreamStateMachine.cs` lines 3400-3401, 5373-5375  
**Status**: ✅ Correct - Uses `ExecutionInstrument` for all order submissions
- `SubmitStopEntryOrder(longIntentId, ExecutionInstrument, ...)`
- `SubmitEntryOrder(intentId, ExecutionInstrument, ...)`

### ✅ Intent Creation
**Location**: `StreamStateMachine.cs` lines 2383-2395, 3307-3330, 5223-5235  
**Status**: ✅ Correct - Uses `Instrument` (canonical) for Intent ID computation
- Intent IDs use canonical instruments for logic identity
- Orders use `ExecutionInstrument` separately (correct separation of concerns)

### ✅ Bar Routing
**Location**: `StreamStateMachine.cs` lines 530-539  
**Status**: ✅ Correct - `IsSameInstrument()` canonicalizes incoming bars and compares to `CanonicalInstrument`
- MES bars canonicalize to ES and route to ES streams
- Execution instrument bars (MGC) canonicalize to GC and route to GC streams

### ✅ GetInstrumentsForBarsRequest
**Location**: `RobotEngine.cs` lines 3977-4006  
**Status**: ✅ Correct - Uses `ExecutionInstrument` from streams and maps base instruments to micro futures
- Logic correctly handles both micro futures (uses as-is) and base instruments (maps to micro)

### ✅ Execution Policy Validation
**Location**: `RobotEngine.cs` lines ~2820-2868, 3076-3077  
**Status**: ✅ Correct - Validates canonical instruments against policy and resolves execution instruments
- Uses `GetEnabledExecutionInstrument(canonicalInstrument)` to resolve execution instrument

### ✅ StreamStateMachine Constructor Assertions
**Location**: `RobotEngine.cs` lines 3115-3139  
**Status**: ✅ Correct - Verifies ExecutionInstrument, CanonicalInstrument, Stream ID, and Instrument property match expectations

## Additional Notes

### Strategy Instrument Comparison
**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs` line 443  
**Note**: The strategy compares `Instrument?.MasterInstrument?.Name` (strategy's instrument root, e.g., "MGC") to the instrument returned by `GetInstrumentsNeedingRestartBarsRequest()` (now execution instrument, e.g., "MGC").  
**Status**: ✅ Should work correctly - Strategy is enabled on execution instrument (MGC), so `MasterInstrument.Name` should match the execution instrument returned.  
**Minor Issue**: Variable named `canonicalInstrument` is misleading - it's actually the strategy's instrument root (execution instrument), not canonical. This is a naming issue, not a functional bug.

## Recommendations

1. **IMMEDIATE**: Fix `GetInstrumentsNeedingRestartBarsRequest()` to use `ExecutionInstrument` instead of `CanonicalInstrument` ✅ DONE
2. **MINOR**: Update comment at lines 3985-3986 to reflect that ExecutionInstrument is already the execution instrument ✅ DONE
3. **VERIFICATION**: After fix, test restart scenario to ensure BarsRequest requests correct instruments
4. **OPTIONAL**: Consider renaming `canonicalInstrument` variable in `RobotSimStrategy.cs` line 443 to `strategyInstrument` or `executionInstrument` for clarity

## Testing Checklist

- [x] Fix `GetInstrumentsNeedingRestartBarsRequest()` ✅ DONE
- [x] Update comment at lines 3985-3986 ✅ DONE
- [x] Sync changes to `modules/robot/core/RobotEngine.cs` ✅ DONE
- [ ] Build and deploy DLL
- [ ] Test restart scenario: Enable strategy, let it run, restart NinjaTrader, verify BarsRequest requests MGC (not GC)
- [ ] Verify logs show correct execution instrument in BarsRequest calls

## Related Files

- `RobotCore_For_NinjaTrader/RobotEngine.cs` - Main engine logic
- `modules/robot/core/RobotEngine.cs` - Source (should match RobotCore_For_NinjaTrader)
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` - Stream state machine
- `modules/robot/ninjatrader/RobotSimStrategy.cs` - Strategy that calls BarsRequest methods
