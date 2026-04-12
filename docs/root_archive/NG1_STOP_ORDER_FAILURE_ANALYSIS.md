# NG1 Stop Order Failure Analysis

## Summary
**NG1 reached RANGE_LOCKED at 07:30 Chicago (13:30 UTC) but stop orders FAILED to place.**

## Timeline
- **13:30:00.279 UTC** - NG1 transitioned to `RANGE_LOCKED`
- **13:30:00.279 UTC** - Range locked: High=3.747, Low=3.57, FreezeClose=3.71
- **13:30:00.279 UTC** - Breakout levels computed: Long=3.748, Short=3.569
- **13:30:00.279 UTC** - **STOP_BRACKETS_SUBMIT_FAILED** ❌

## Root Cause

### Error Message
```
"Pre-submission check failed: intent policy expectation missing"
```

Both long and short stop entry orders failed with the **same error**.

### What Happened

1. **Range locked successfully** ✅
   - Range computed: 3.747 (high) to 3.57 (low)
   - Breakout levels computed: 3.748 (long) / 3.569 (short)
   - State transitioned to `RANGE_LOCKED`

2. **Stop bracket submission attempted** ✅
   - Code called `SubmitStopEntryBracketsAtLock()`
   - Created intents for long and short orders
   - Attempted to submit both orders

3. **Pre-submission check FAILED** ❌
   - Both orders failed before reaching NinjaTrader
   - Error: "intent policy expectation missing"
   - This is a **validation check** that runs before order submission

## Technical Details

### Intent Policy Expectation
The execution adapter requires an "intent policy expectation" to be registered before submitting orders. This expectation contains:
- Expected quantity (from execution_policy.json)
- Max quantity (from execution_policy.json)
- Policy source
- Canonical instrument
- Execution instrument

### Where It Should Be Registered
Looking at the code flow:
1. `SubmitStopEntryBracketsAtLock()` creates intents (lines 3226-3252)
2. Intents are registered with adapter (lines 3286-3290):
   ```csharp
   if (_executionAdapter is NinjaTraderSimAdapter ntAdapter)
   {
       ntAdapter.RegisterIntent(longIntent);
       ntAdapter.RegisterIntent(shortIntent);
   }
   ```
3. Orders are submitted (lines 3295-3296)
4. **Pre-submission check** validates intent policy expectation exists

### The Problem
The intent policy expectation is registered via `RegisterIntent()`, but the **pre-submission check** is looking for a policy expectation that may not have been set up correctly.

## Code Location

The error originates from:
- `NinjaTraderSimAdapter.SubmitStopEntryOrder()` 
- Pre-submission validation check
- Likely in `OrderCoordinator` or similar validation layer

## Impact

- ✅ NG1 is in `RANGE_LOCKED` state
- ✅ Range and breakout levels computed correctly
- ❌ **No stop entry orders placed**
- ❌ Stream cannot detect breakouts (no orders to fill)
- ⚠️ Stream will wait until market close, then commit with "NO_TRADE"

## Next Steps

1. **Check intent policy registration:**
   - Verify `RegisterIntent()` is being called
   - Check if policy expectation is set correctly
   - Look for `INTENT_POLICY_MISSING_AT_ORDER_CREATE` warnings

2. **Review pre-submission validation:**
   - Find where "intent policy expectation missing" check occurs
   - Verify policy expectations are set before order submission
   - Check if there's a timing issue (policy set after validation)

3. **Check execution_policy.json:**
   - Verify MNG (execution instrument) is configured
   - Check base_size and max_size values

4. **Review RegisterIntent() implementation:**
   - Ensure it sets policy expectations correctly
   - Check if there's a race condition or timing issue

## Related Log Events

- `STOP_BRACKETS_SUBMIT_FAILED` - Both orders failed
- `INTENDED_BRACKETS_PLACED` - Logged after failure (intended but not placed)
- `RANGE_LOCKED` - Successfully reached locked state
- `BREAKOUT_LEVELS_COMPUTED` - Levels computed correctly

## Comparison with Yesterday (2026-01-27)

Yesterday's NG1 journal shows:
- `StopBracketsSubmittedAtLock: true` ✅
- `EntryDetected: true` ✅
- Successfully placed stop orders

This suggests the issue is **new today** or **intermittent**.
