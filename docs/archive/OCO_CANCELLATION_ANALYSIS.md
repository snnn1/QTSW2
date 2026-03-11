# OCO Cancellation Analysis

## Issue
When protective stop fills, we explicitly cancel the opposite entry order using `CancelIntentOrders(oppositeIntentId)`. This raises questions about OCO group handling and potential side effects.

## Current Behavior

### 1. Entry Order OCO Groups
- **Long and Short entry stops** are OCO-linked to each other
- OCO group: `QTSW2:OCO_ENTRY:{tradingDate}:{stream}:{slotTime}:{guid}`
- When one entry fills, NinjaTrader **should** automatically cancel the other via OCO

### 2. Protective Order OCO Groups
- **Stop and Target** are OCO-linked to each other
- OCO group: `QTSW2:{intentId}_PROTECTIVE_A{attempt}_{timestamp}`
- When one fills, NinjaTrader **should** automatically cancel the other via OCO

### 3. Entry vs Protective Orders
- **Entry orders and protective orders are NOT OCO-linked** - they use different OCO groups
- Protective orders are created AFTER entry fills
- Entry orders exist BEFORE protective orders

## CancelIntentOrders() Behavior

**Location**: `CancelIntentOrdersReal()` (line 3222)

**What it does**:
1. Finds all orders matching `intentId` by decoding tags:
   - Entry order: `QTSW2:{intentId}`
   - Stop order: `QTSW2:{intentId}:STOP`
   - Target order: `QTSW2:{intentId}:TARGET`
2. Cancels ALL matching orders directly via `account.Cancel()`
3. Updates `_orderMap` state to "CANCELLED"

**Key Point**: It cancels orders **directly**, bypassing OCO group mechanisms.

## Potential Issues

### ⚠️ Issue 1: OCO Group Side Effects

**Scenario**: When we cancel the opposite entry order:
- We call `CancelIntentOrders(oppositeIntentId)`
- This cancels the opposite entry order directly
- **Question**: Does NinjaTrader's OCO mechanism also cancel our entry order?

**Analysis**:
- NinjaTrader's OCO behavior: When you cancel one order in an OCO group, it should cancel the other
- However, we're cancelling **directly** via `account.Cancel()`, not through OCO
- **Risk**: If NinjaTrader sees a direct cancel, it might NOT trigger OCO cancellation of the paired order
- **But**: Our entry order already filled (that's why protective stop exists), so this shouldn't matter

**Verdict**: ✅ **SAFE** - Our entry order already filled, so OCO cancellation of it is irrelevant

### ⚠️ Issue 2: Cancelling Protective Orders for Opposite Intent

**Scenario**: What if the opposite entry order already filled and has protective orders?

**Analysis**:
- If opposite entry filled, we'd have a position in the opposite direction
- But we're cancelling when OUR protective stop fills, meaning we're closing OUR position
- The opposite entry should NOT have filled yet (otherwise we'd have conflicting positions)
- **However**: There's a race condition possibility

**Edge Case**:
1. Our entry fills → protective orders created
2. Opposite entry fills (somehow) → opposite protective orders created
3. Our protective stop fills → we cancel opposite entry
4. `CancelIntentOrders(oppositeIntentId)` cancels:
   - Opposite entry order (already filled, so no-op)
   - Opposite protective stop (⚠️ **ISSUE** - this cancels protection for opposite position)
   - Opposite protective target (⚠️ **ISSUE** - this cancels protection for opposite position)

**Verdict**: ⚠️ **POTENTIAL ISSUE** - If opposite entry already filled, we'd cancel its protective orders

### ⚠️ Issue 3: _orderMap State Update

**Location**: Line 3267-3270

**What happens**:
```csharp
if (decodedIntentId == intentId && _orderMap.TryGetValue(intentId, out var orderInfo))
{
    orderInfo.State = "CANCELLED";
}
```

**Issue**: 
- `_orderMap[intentId]` might contain the protective order (stop or target) if it was added last
- We're updating its state to "CANCELLED" even though we're cancelling the entry order
- This could cause confusion in tracking

**Verdict**: ⚠️ **MINOR ISSUE** - State tracking might be inaccurate, but shouldn't cause functional problems

## Recommended Fixes

### Fix 1: Only Cancel Entry Orders (Not Protective Orders)

**Change**: Modify `CancelIntentOrders()` to only cancel entry orders, not protective orders.

**Implementation**:
```csharp
// Only cancel entry orders (not protective orders)
// Protective orders should be cancelled via OCO when their pair fills
var tag = GetOrderTag(order) ?? "";
var decodedIntentId = RobotOrderIds.DecodeIntentId(tag);

// Match intent ID AND ensure it's an entry order (not STOP/TARGET)
if (decodedIntentId == intentId && !tag.EndsWith(":STOP") && !tag.EndsWith(":TARGET"))
{
    ordersToCancel.Add(order);
}
```

**Rationale**: 
- Entry orders should be cancelled to prevent re-entry
- Protective orders should be managed via OCO groups
- If opposite entry already filled, we shouldn't cancel its protective orders

### Fix 2: Add Explicit Check for Opposite Entry Fill State

**Change**: Before cancelling opposite entry, check if it already filled.

**Implementation**:
```csharp
// Check if opposite entry already filled
if (oppositeIntentId != null)
{
    // Check execution journal to see if opposite entry filled
    var oppositeEntryFilled = _executionJournal?.HasEntryFillForStream(
        filledIntent.TradingDate, filledIntent.Stream, oppositeIntentId) == true;
    
    if (!oppositeEntryFilled)
    {
        // Only cancel if opposite entry hasn't filled yet
        var cancelled = CancelIntentOrders(oppositeIntentId, utcNow);
        // ... logging ...
    }
    else
    {
        // Opposite entry already filled - don't cancel (would cancel protective orders)
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, orderInfo.Instrument, 
            "OPPOSITE_ENTRY_ALREADY_FILLED_SKIP_CANCEL",
            new
            {
                filled_intent_id = intentId,
                opposite_intent_id = oppositeIntentId,
                note = "Opposite entry already filled - skipping cancel to avoid cancelling protective orders"
            }));
    }
}
```

**Rationale**: Prevents cancelling protective orders for opposite intent if it already filled

### Fix 3: Improve _orderMap State Tracking

**Change**: Track which order type is in `_orderMap` and update state accordingly.

**Current**: Updates `_orderMap[intentId].State` regardless of which order was cancelled
**Better**: Only update state if the cancelled order matches what's in `_orderMap`

## Current Risk Assessment

### Risk Level: **MEDIUM**

**Why**:
1. ✅ Most common case (opposite entry not filled) is safe
2. ⚠️ Edge case (opposite entry already filled) could cancel protective orders
3. ⚠️ State tracking in `_orderMap` might be inaccurate

**Impact**:
- **Low** if opposite entry hasn't filled (normal case)
- **High** if opposite entry already filled (would cancel its protective orders, leaving position unprotected)

## Recommendation

**Priority**: **HIGH** - Implement Fix 1 and Fix 2

**Reasoning**:
- Fix 1 prevents cancelling protective orders entirely (safest)
- Fix 2 adds defense-in-depth by checking fill state
- Both fixes are simple and low-risk
- Current behavior could cause unprotected positions in edge cases
