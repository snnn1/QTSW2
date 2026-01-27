# NINJATRADER Compilation Fix

## Problem

You're using `RobotSimStrategy`, but logs show "MOCK - harness mode", which means the `NINJATRADER` preprocessor directive is **NOT defined** when compiling.

## Root Cause

The `NINJATRADER` preprocessor directive must be defined in your NinjaTrader project for the real NT API code to be compiled.

### What's Happening

1. ✅ `SetNTContext()` IS being called (RobotSimStrategy line 674)
2. ✅ `_ntContextSet` IS set to `true` (NinjaTraderSimAdapter line 93)
3. ❌ `#if NINJATRADER` is NOT defined → Real NT API code NOT compiled
4. ❌ `VerifySimAccountReal()` doesn't exist → Falls back to mock
5. ❌ `SubmitEntryOrderReal()` doesn't exist → Falls back to mock

## Solution

### Step 1: Check Your NinjaTrader Project File

Open your NinjaTrader Strategy project's `.csproj` file and verify it has:

```xml
<PropertyGroup>
  <DefineConstants>NINJATRADER</DefineConstants>
</PropertyGroup>
```

OR in the build configuration:

```xml
<PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|AnyCPU'">
  <DefineConstants>NINJATRADER</DefineConstants>
</PropertyGroup>
<PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'">
  <DefineConstants>NINJATRADER</DefineConstants>
</PropertyGroup>
```

### Step 2: Verify in Visual Studio / IDE

1. Right-click your NinjaTrader Strategy project → Properties
2. Go to Build → Conditional compilation symbols
3. Add `NINJATRADER` (or check if it's already there)
4. Rebuild the project

### Step 3: Verify Compilation

After rebuilding, check if these methods exist:
- `VerifySimAccountReal()` - Should be in `NinjaTraderSimAdapter.NT.cs`
- `SubmitEntryOrderReal()` - Should be in `NinjaTraderSimAdapter.NT.cs`

If they don't exist, the `NINJATRADER` directive isn't set.

## How to Verify It's Working

After setting `NINJATRADER` and rebuilding:

1. **Check Logs**: Look for `SIM_ACCOUNT_VERIFIED` event
   - ✅ Should NOT say "MOCK - harness mode"
   - ✅ Should show real account name verification

2. **Check Logs**: Look for `EXECUTION_MODE_MISMATCH` event (if we added it)
   - ✅ Should NOT appear (means `_ntContextSet = true`)

3. **Check Orders**: Look for `ORDER_SUBMIT_ATTEMPT` events
   - ✅ Should use real NT API (not mock)

## Code Flow (After Fix)

```
RobotSimStrategy.OnStateChange(State.DataLoaded)
  → Create RobotEngine(ExecutionMode.SIM)
  → Engine creates NinjaTraderSimAdapter
  → Strategy.WireNTContextToAdapter()
    → adapter.SetNTContext(Account, Instrument)
      → _ntContextSet = true
      → VerifySimAccount()
        → #if NINJATRADER defined ✅
        → VerifySimAccountReal() ✅ (real NT API)
  → Orders use real NT API ✅
```

## Files That Need NINJATRADER Defined

- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - Entire file wrapped in `#if NINJATRADER`
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` - Methods check `#if NINJATRADER` before calling real API

## Quick Test

Add this to your RobotSimStrategy to verify:

```csharp
#if NINJATRADER
Log("NINJATRADER is defined - real NT API will be used", LogLevel.Information);
#else
Log("WARNING: NINJATRADER is NOT defined - using mock implementation", LogLevel.Warning);
#endif
```

If you see the warning, `NINJATRADER` isn't defined.
