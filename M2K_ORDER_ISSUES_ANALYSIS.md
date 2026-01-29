# M2K Order Issues Analysis

## Executive Summary

Three distinct issues affecting M2K order execution:
1. **Order Tracking Race Condition** - Execution updates arrive before orders are tracked
2. **OCO ID Reuse** - Old OCO IDs without GUIDs being reused
3. **Price Limit Errors** - Stop prices rejected as outside valid range

---

## Issue 1: Order Tracking Race Condition (`EXECUTION_UPDATE_UNKNOWN_ORDER`)

### Problem
Execution updates arrive for orders that aren't in the tracking map (`_orderMap`), causing `EXECUTION_UPDATE_UNKNOWN_ORDER` warnings.

### Root Cause
**Race condition in order submission flow:**

```
1. Order created (line 850-874)
2. OrderInfo created (line 859-874)
3. Order added to _orderMap (line 897) ✅
4. Order submitted via account.Submit() (line 905)
5. NinjaTrader immediately fires ExecutionUpdate event ⚡
6. HandleExecutionUpdateReal() checks _orderMap (line 1235) ❌
   → Order might not be in map yet (race condition)
```

**Evidence from logs:**
```
broker_order_id: 357414280478
order_state: Submitted
error: Order not found in tracking map
note: Execution update received for untracked order - may indicate order was rejected before tracking or tracking race condition
```

**Multiple execution updates for same order:**
- Same `broker_order_id` appears multiple times with different `run_id` values
- Suggests multiple strategy instances receiving same execution update
- Each instance checks its own `_orderMap`, but order might only be tracked in one instance

### Impact
- **Low severity** - Orders still execute correctly
- Logs show warnings but don't prevent execution
- May cause confusion when debugging order fills

### Current Mitigation
Code already handles this gracefully (line 1235-1256):
- Logs as `INFO` severity (not error)
- Returns early without crashing
- Note explains it's expected for rejected orders

### Potential Fix
**Option 1: Track orders BEFORE submission**
```csharp
// Add to map BEFORE Submit()
_orderMap[intentId] = orderInfo;
// Then submit
account.Submit(new[] { order });
```

**Current code already does this** (line 897 is before line 905), so the issue might be:
- Multiple strategy instances (each has its own `_orderMap`)
- Execution updates broadcast to all instances
- Only one instance has the order in its map

**Option 2: Cross-instance order tracking**
- Use shared storage (file/database) for order tracking
- All strategy instances check shared map
- More complex, may not be necessary

**Option 3: Accept as expected behavior**
- Current handling is correct
- Multiple instances are expected to receive same updates
- Only the instance that submitted should track it

---

## Issue 2: OCO ID Reuse Errors

### Problem
Orders rejected with: `The OCO ID 'QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30' cannot be reused. Please use a new OCO ID.`

### Root Cause
**OCO ID format mismatch:**

**Error shows (OLD FORMAT - NO GUID):**
```
QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30
```

**Successful orders show (NEW FORMAT - WITH GUID):**
```
QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30:0bbe539ac61f49d18608afe49bcddcda
```

**Code in `RobotOrderIds.cs` line 30:**
```csharp
public static string EncodeEntryOco(string tradingDate, string stream, string slotTimeChicago) =>
    $"{Prefix}OCO_ENTRY:{tradingDate}:{stream}:{slotTimeChicago}:{Guid.NewGuid():N}";
```

**This SHOULD generate GUID**, but error shows old format without GUID.

### Possible Causes

**1. Old orders still in NinjaTrader system:**
- Previous orders with old OCO ID format still exist
- NinjaTrader remembers OCO IDs and rejects reuse
- Old format: `QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30` (no GUID)
- New format: `QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30:{GUID}` (with GUID)

**2. Code path not using EncodeEntryOco():**
- Somewhere OCO ID is hardcoded or generated differently
- Check `StreamStateMachine.cs` line 3420 - calls `EncodeEntryOco()`
- But maybe old code path exists?

**3. NinjaTrader caching:**
- NinjaTrader might cache OCO IDs from previous sessions
- Even with new GUID, if base format matches, might conflict

### Evidence from Logs

**Failed orders (OLD FORMAT):**
```
ts_utc: 2026-01-28T17:39:50.2376276+00:00
error: The OCO ID `QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30' cannot be reused
```

**Successful orders (NEW FORMAT):**
```
ts_utc: 2026-01-28T18:51:22.4088436+00:00
oco_group: QTSW2:OCO_ENTRY:2026-01-28:RTY2:09:30:0bbe539ac61f49d18608afe49bcddcda
```

### Impact
- **Medium severity** - Orders rejected, trades don't execute
- Affects specific time slots where old OCO IDs exist
- Later orders succeed (with GUID format)

### Fix Options

**Option 1: Clear NinjaTrader order history**
- Remove old orders with old OCO IDs
- Restart NinjaTrader to clear cache
- New orders will use GUID format

**Option 2: Verify EncodeEntryOco() is always called**
- Search codebase for any hardcoded OCO IDs
- Ensure all OCO generation uses `RobotOrderIds.EncodeEntryOco()`
- Add logging to verify GUID is generated

**Option 3: Add OCO ID cleanup on strategy start**
- Cancel any existing orders with old OCO IDs
- Clear OCO groups before placing new orders
- Prevents reuse conflicts

---

## Issue 3: Price Limit Errors

### Problem
Order rejected with: `Please check the order price. The current price is outside the price limits set for this product.`

### Root Cause
**Stop price too far from current market price**

NinjaTrader has price limits (circuit breakers) that prevent orders too far from current market:
- **Example**: If M2K is trading at 2650, stop at 2700 might be rejected
- **Limit varies by instrument** (M2K might have tighter limits than ES)

**Evidence from logs:**
```
broker_order_id: 357414280478
order_state: Rejected
error: Please check the order price. The current price is outside the price limits set for this product.
```

**Timeline:**
1. Order submitted at `18:51:22.4088436`
2. Multiple `EXECUTION_UPDATE_UNKNOWN_ORDER` warnings (order not tracked)
3. Order rejected at `18:51:22.6615112` (price limit violation)

### Why This Happens

**Possible scenarios:**

1. **Stale breakout level:**
   - Range locked earlier in day
   - Breakout level calculated (e.g., 2700)
   - Market moved significantly (now at 2650)
   - Stop price (2700) is too far from current (2650)
   - NinjaTrader rejects as invalid

2. **Market gap:**
   - Range locked at close (e.g., 2700)
   - Market gaps down at open (e.g., 2650)
   - Stop price still at 2700 (too far)
   - Rejected by NinjaTrader

3. **Range size too large:**
   - Large range (e.g., 50 points)
   - Breakout level far from current price
   - Stop price exceeds NinjaTrader's limit

### Impact
- **High severity** - Orders rejected, trades don't execute
- Affects orders with large ranges or stale breakout levels
- May cause missed trading opportunities

### Fix Options

**Option 1: Validate stop price before submission**
```csharp
// Get current market price
var currentPrice = instrument.MarketData.GetBid(0) ?? instrument.MarketData.GetAsk(0);
var priceDiff = Math.Abs(stopPrice - currentPrice);

// Check if within NinjaTrader limits (e.g., 100 points for M2K)
if (priceDiff > 100)
{
    // Reject or adjust stop price
    LogWarning("Stop price too far from market, adjusting...");
    stopPrice = currentPrice + (stopPrice > currentPrice ? 100 : -100);
}
```

**Option 2: Check range age before placing orders**
- If range locked > X hours ago, recalculate breakout levels
- Use current market price to validate stop prices
- Skip orders with stale ranges

**Option 3: Use NinjaTrader's price validation**
- Let NinjaTrader reject invalid prices
- Log rejection and skip order
- Continue with other orders

**Option 4: Adjust stop price to valid range**
- If rejected for price limit, adjust stop to nearest valid price
- Resubmit with adjusted price
- May change trade risk/reward

---

## Recommendations

### Priority 1: OCO ID Reuse (Medium-High)
1. **Immediate**: Clear NinjaTrader order history for M2K
2. **Short-term**: Verify all OCO generation uses `EncodeEntryOco()` with GUID
3. **Long-term**: Add OCO ID cleanup on strategy start

### Priority 2: Price Limit Errors (High)
1. **Immediate**: Add price validation before order submission
2. **Short-term**: Check range age and recalculate breakout levels if stale
3. **Long-term**: Implement dynamic stop price adjustment based on market conditions

### Priority 3: Order Tracking Race Condition (Low)
1. **Accept as expected behavior** - Current handling is correct
2. **Optional**: Add logging to identify which instance submitted order
3. **Optional**: Consider shared order tracking if multiple instances become problematic

---

## Code Locations

### Order Tracking
- `NinjaTraderSimAdapter.NT.cs` line 897: `_orderMap[intentId] = orderInfo;`
- `NinjaTraderSimAdapter.NT.cs` line 1235: `_orderMap.TryGetValue(intentId, out var orderInfo)`
- `NinjaTraderSimAdapter.NT.cs` line 1243: `EXECUTION_UPDATE_UNKNOWN_ORDER` logging

### OCO ID Generation
- `RobotOrderIds.cs` line 30: `EncodeEntryOco()` method
- `StreamStateMachine.cs` line 3420: Calls `EncodeEntryOco()`

### Price Validation
- **Missing** - No price validation before submission
- `NinjaTraderSimAdapter.NT.cs` line 922: Order rejection handling
- Need to add validation before line 905 (`account.Submit()`)

---

## Testing Recommendations

1. **OCO ID Test:**
   - Place orders for same stream/slot multiple times
   - Verify each gets unique GUID
   - Check no reuse errors

2. **Price Limit Test:**
   - Create range with large breakout level
   - Wait for market to move away
   - Submit order and verify rejection
   - Add validation and verify adjustment

3. **Order Tracking Test:**
   - Submit order and immediately check execution updates
   - Verify order is in map before update arrives
   - Test with multiple strategy instances
