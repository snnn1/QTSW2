# DEEPER ROOT CAUSE ANALYSIS - MNQ1 Position Accumulation

## CRITICAL FINDINGS

### Issue #1: Intent Resolution Failure
- **270 fills have UNKNOWN/missing intent_id**
- **0 protective orders submitted** (because intent_id is required)
- **All 270 fills are UNPROTECTED**

### Issue #2: Order Tag Decoding Failure
Looking at `HandleExecutionUpdateReal` (line 1324-1346):
```csharp
var encodedTag = GetOrderTag(order);
var intentId = RobotOrderIds.DecodeIntentId(encodedTag);

if (string.IsNullOrEmpty(intentId))
{
    // Logs EXECUTION_UPDATE_IGNORED_NO_TAG and returns early
    return; // Fill is ignored
}
```

**Problem**: If order tag is missing/invalid, the fill is **ignored** but the position still fills in NinjaTrader!

### Issue #3: Order Not in Tracking Map
Even if intent_id is decoded, if order is not in `_orderMap`:
```csharp
if (!_orderMap.TryGetValue(intentId, out var orderInfo))
{
    // Logs EXECUTION_UPDATE_UNKNOWN_ORDER and returns early
    return; // Fill is ignored
}
```

**Problem**: Fill is ignored but position still accumulates in NinjaTrader!

## Root Cause Chain

1. **Order submitted** → Tag should be set with `RobotOrderIds.EncodeTag(intentId)`
2. **Order fills** → `HandleExecutionUpdateReal` is called
3. **Tag decoding fails** OR **Order not in _orderMap** → Fill is ignored
4. **Position accumulates** in NinjaTrader (fill happened, but robot didn't track it)
5. **No protective orders** → Position is unprotected
6. **Repeat 270 times** → Position grows to 270 contracts

## Why This Happens

### Scenario A: Order Tag Not Set
- Order submitted but tag wasn't set correctly
- `GetOrderTag(order)` returns null or invalid tag
- `DecodeIntentId` returns empty string
- Fill is ignored, position accumulates

### Scenario B: Order Not Tracked
- Order was rejected before being added to `_orderMap`
- But order still fills in NinjaTrader
- Fill arrives but order not in tracking map
- Fill is ignored, position accumulates

### Scenario C: Order Tracking Race Condition
- Order submitted and fills very quickly
- Fill arrives before order is added to `_orderMap`
- Fill is ignored, position accumulates

## The Real Problem

**The code assumes that if a fill can't be tracked, it should be ignored. But in NinjaTrader, the fill still happens!**

This creates a dangerous situation:
- Robot thinks: "Fill ignored, no position"
- NinjaTrader reality: "Fill happened, position exists"
- Result: **Unprotected position**

## What Needs to Happen

### Option 1: Flatten on Unknown Fill (Fail-Closed)
When a fill can't be tracked:
1. Log critical error
2. **Flatten the position immediately** (fail-closed)
3. Stand down stream

### Option 2: Track Unknown Fills
When a fill can't be tracked:
1. Log critical error
2. Try to flatten position
3. Track unknown fills separately
4. Alert operator

### Option 3: Prevent Untracked Fills
Ensure orders are always tracked before they can fill:
1. Verify order is in `_orderMap` before allowing fills
2. If order not tracked, cancel it immediately
3. Retry with proper tracking

## Immediate Fix Needed

**CRITICAL**: When `HandleExecutionUpdateReal` receives a fill that can't be tracked:
- **DO NOT** just return and ignore it
- **DO** flatten the position immediately (fail-closed)
- **DO** log critical error
- **DO** stand down stream

This prevents unprotected positions from accumulating.
