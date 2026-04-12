# Compilation Mode Analysis

## Issue

Orders aren't executing because the robot is running in **harness/testing mode** instead of **NinjaTrader Strategy mode**.

## Root Cause

**The code is working as designed, but it's not running in the correct context.**

### How It Works

1. **Execution Mode**: Set at runtime (DRYRUN/SIM/LIVE) ✅
2. **NT Context**: Set by `RobotSimStrategy` calling `SetNTContext()` ❌
3. **Compilation**: Uses `#if NINJATRADER` preprocessor directive

### The Problem

**Two separate issues:**

1. **Runtime Issue**: `SetNTContext()` is never called
   - Robot is running in harness/testing mode (not inside NT Strategy)
   - `RobotSimStrategy.WireNTContextToAdapter()` never executes
   - `_ntContextSet` stays `false`
   - Adapter uses mock implementation

2. **Compilation Issue**: `NINJATRADER` preprocessor directive may not be set
   - Real NT API code is in `NinjaTraderSimAdapter.NT.cs`
   - Wrapped in `#if NINJATRADER` blocks
   - If not defined, real NT API code isn't compiled
   - Even if `SetNTContext()` was called, real API wouldn't be available

## Code Flow

### Correct Flow (Inside NT Strategy)
```
RobotSimStrategy.OnStateChange(State.DataLoaded)
  → Create RobotEngine(ExecutionMode.SIM)
  → Engine creates NinjaTraderSimAdapter
  → Strategy.WireNTContextToAdapter()
    → adapter.SetNTContext(Account, Instrument)
      → _ntContextSet = true
      → VerifySimAccountReal() (if NINJATRADER defined)
  → Orders use real NT API
```

### Current Flow (Harness Mode)
```
Harness/Testing Program
  → Create RobotEngine(ExecutionMode.SIM)
  → Engine creates NinjaTraderSimAdapter
  → SetNTContext() NEVER CALLED
    → _ntContextSet = false
    → VerifySimAccount() uses mock
  → Orders use mock implementation
```

## Detection

### How to Check

1. **Check Logs**: Look for `SIM_ACCOUNT_VERIFIED` event
   - ✅ Real mode: Should NOT say "MOCK - harness mode"
   - ❌ Harness mode: Says "MOCK - harness mode"

2. **Check Code**: Look for `SetNTContext()` call
   - ✅ Real mode: Called by `RobotSimStrategy.WireNTContextToAdapter()`
   - ❌ Harness mode: Never called

3. **Check Compilation**: Look for `NINJATRADER` define
   - ✅ Real mode: Defined when compiling inside NT project
   - ❌ Harness mode: Not defined (standalone compilation)

## Solutions

### Option 1: Run Inside NinjaTrader Strategy (Required)

**The robot MUST run inside `RobotSimStrategy` to get real NT context:**

1. Copy `RobotSimStrategy.cs` to NinjaTrader Strategy project
2. Copy `Robot.Core.dll` or source files to NT project
3. Set `NINJATRADER` preprocessor directive in NT project
4. Run strategy inside NinjaTrader

**Result**: `SetNTContext()` called → Real NT API used

### Option 2: Add Detection/Warning (Code Improvement)

Add a check to warn if running in harness mode when SIM mode is requested:

```csharp
// In NinjaTraderSimAdapter.SubmitEntryOrder()
if (_executionMode == ExecutionMode.SIM && !_ntContextSet)
{
    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, 
        "EXECUTION_MODE_MISMATCH", new
    {
        execution_mode = "SIM",
        nt_context_set = false,
        warning = "SIM mode requested but NT context not set - using mock implementation. " +
                  "Run inside RobotSimStrategy to get real NT API access."
    }));
}
```

### Option 3: Check Compilation (Build Configuration)

Verify `NINJATRADER` is defined when compiling for NT:
- Check `.csproj` file for `<DefineConstants>NINJATRADER</DefineConstants>`
- Or check build configuration in IDE

## Current State

### From Logs
- **Execution Mode**: SIM ✅
- **NT Context Set**: ❌ False
- **SIM Account Verified**: Mock mode ❌
- **Orders**: Mock implementation ❌

### Expected State (Inside NT Strategy)
- **Execution Mode**: SIM ✅
- **NT Context Set**: ✅ True
- **SIM Account Verified**: Real NT account ✅
- **Orders**: Real NT API ✅

## Conclusion

**The code is correct - it's designed to work in both harness and NT Strategy contexts.**

**The issue is runtime context, not compilation:**
- Robot is running in harness mode (not inside NT Strategy)
- `SetNTContext()` is never called
- Adapter correctly falls back to mock implementation

**To fix**: Run robot inside `RobotSimStrategy` in NinjaTrader.
