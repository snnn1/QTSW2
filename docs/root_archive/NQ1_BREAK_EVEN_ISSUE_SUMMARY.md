# NQ1 Break-Even Detection Issue

## Problem
Stop loss isn't moving to break-even when break-even point is hit for NQ1.

## Root Cause Analysis

### Issue: Protective Stop Orders Overwrite Entry Orders in `_orderMap`

**Location**: `NinjaTraderSimAdapter.NT.cs` lines ~2489 and ~2881

**Problem**: When protective stop and target orders are submitted after entry fill, they overwrite the entry order in `_orderMap`:

```csharp
// Protective stop order added to _orderMap
_orderMap[intentId] = stopOrderInfo; // Overwrites entry order!

// Protective target order added to _orderMap  
_orderMap[intentId] = targetOrderInfo; // Overwrites entry order again!
```

**Impact**: `GetActiveIntentsForBEMonitoring()` checks `_orderMap` for entry orders:

```csharp
// Only check entry orders that are filled
if (!orderInfo.IsEntryOrder || orderInfo.State != "FILLED" || !orderInfo.EntryFillTime.HasValue)
    continue;
```

Since protective orders have `IsEntryOrder = false`, they fail this check and break-even monitoring never finds the filled entry.

## Solution

### Option 1: Don't Overwrite Entry Order (Recommended)
Keep entry order in `_orderMap` and store protective orders separately or use a different key.

### Option 2: Check `_intentMap` Instead of `_orderMap`
Modify `GetActiveIntentsForBEMonitoring()` to iterate over `_intentMap` instead of `_orderMap`, then check execution journal for fill status.

### Option 3: Use Execution Journal as Source of Truth
Check execution journal entries directly for filled entries, regardless of `_orderMap` state.

## Verification Steps

1. Check if entry orders are being overwritten:
   - Look for `PROTECTIVE_ORDER_FILL_TRACKED_FROM_TAG` events
   - Check if `_orderMap[intentId]` assignments overwrite entry orders

2. Check if `GetActiveIntentsForBEMonitoring()` returns empty list:
   - Add logging to see how many intents are returned
   - Check if `IsEntryOrder` check is filtering out all intents

3. Check if break-even trigger is being detected:
   - Look for `BE_TRIGGER_REACHED` events in logs
   - Check if `OnMarketData()` is being called
   - Verify tick prices are reaching BE trigger levels

## Expected Behavior

1. Entry order fills → Entry order remains in `_orderMap` with `IsEntryOrder = true`
2. Protective orders submitted → Stored separately or tracked differently
3. `GetActiveIntentsForBEMonitoring()` finds filled entry → Returns intent for BE monitoring
4. Tick price reaches BE trigger → `CheckBreakEvenTriggersTickBased()` detects it
5. Stop order modified → `ModifyStopToBreakEven()` updates stop price to break-even

## Current Behavior

1. Entry order fills → Entry order in `_orderMap` with `IsEntryOrder = true` ✅
2. Protective stop submitted → **Overwrites entry order** ❌
3. `GetActiveIntentsForBEMonitoring()` checks `_orderMap` → Finds protective stop with `IsEntryOrder = false` ❌
4. No intents returned → Break-even monitoring never runs ❌
