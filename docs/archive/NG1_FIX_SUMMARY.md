# NG1 Stop Order Failure - Fix Summary

## Problem Identified

**Root Cause:** `SubmitStopEntryBracketsAtLock()` was registering intents but **NOT registering policy expectations** before submitting orders.

### Error
```
"Pre-submission check failed: intent policy expectation missing"
```

### What Was Missing

The code flow was:
1. ✅ Create intents (longIntent, shortIntent)
2. ✅ Register intents (`RegisterIntent()`)
3. ❌ **MISSING:** Register policy expectations (`RegisterIntentPolicy()`)
4. ❌ Submit orders → **FAILED** (pre-submission check requires policy)

### The Fix

Added policy expectation registration **before** order submission:

```csharp
// Register intents so protective orders can be submitted on fill, and ensure OCO cancels opposite
if (_executionAdapter is NinjaTraderSimAdapter ntAdapter)
{
    ntAdapter.RegisterIntent(longIntent);
    ntAdapter.RegisterIntent(shortIntent);
    
    // CRITICAL FIX: Register policy expectations BEFORE order submission
    // This is required for pre-submission validation checks
    ntAdapter.RegisterIntentPolicy(longIntentId, _orderQuantity, _maxQuantity,
        CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
    ntAdapter.RegisterIntentPolicy(shortIntentId, _orderQuantity, _maxQuantity,
        CanonicalInstrument, ExecutionInstrument, "EXECUTION_POLICY_FILE");
}
```

## Files Modified

1. `modules/robot/core/StreamStateMachine.cs` (line ~3285)
2. `RobotCore_For_NinjaTrader/StreamStateMachine.cs` (line ~3285)

## Why This Happened

The policy expectation registration was only implemented in `RecordIntendedEntry()` (called when entry is detected), but stop entry brackets are placed **before** entry detection. The code path for stop brackets at lock was missing this critical step.

## Next Steps

1. **Rebuild Robot.Core.dll** with the fix
2. **Copy to NinjaTrader** bin/Custom directory
3. **Restart NinjaTrader** strategy
4. **Monitor NG1** at next slot time (07:30 tomorrow) to verify fix

## Verification

After fix, logs should show:
- ✅ `INTENT_POLICY_REGISTERED` events for both long and short intents
- ✅ `STOP_BRACKETS_SUBMITTED` instead of `STOP_BRACKETS_SUBMIT_FAILED`
- ✅ `ORDER_CREATED_STOPMARKET` events for both orders
- ✅ `ORDER_SUBMITTED` events with `order_state = "Working"`
