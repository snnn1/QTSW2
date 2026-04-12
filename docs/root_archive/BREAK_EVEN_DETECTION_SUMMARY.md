# Break-Even Detection - How It Works

## Overview

Break-even detection monitors filled positions and automatically moves the stop loss to break-even (1 tick before breakout level) when the price reaches 65% of the target distance from entry.

## Complete Flow

### 1. BE Trigger Calculation (Entry Detection)

**Location**: `StreamStateMachine.cs` → `ComputeAndLogProtectiveOrders()`

**When**: When entry is detected (breakout level determined)

**Formula**:
```csharp
// BE Trigger = Entry Price ± (Base Target × 65%)
var beTriggerPts = _baseTarget * 0.65m; // 65% of target
var beTriggerPrice = direction == "Long" 
    ? entryPrice + beTriggerPts  // Long: entry + 65% of target
    : entryPrice - beTriggerPts; // Short: entry - 65% of target
```

**Example**:
- Entry Price: 100.00
- Base Target: 4.00 points
- BE Trigger (Long): 100.00 + (4.00 × 0.65) = **102.60**

**Stored**: `_intendedBeTrigger` → Set in `Intent.BeTrigger` → Registered with intent

---

### 2. Intent Registration

**Location**: `StreamStateMachine.cs` → `RegisterIntent()`

**When**: Before entry order submission

**Key Fields**:
- `Intent.EntryPrice` = Breakout level (brkLong/brkShort)
- `Intent.BeTrigger` = BE trigger price (calculated above)
- `Intent.Direction` = "Long" or "Short"

**Status**: ✅ Intent registered with BE trigger before order submission

---

### 3. Entry Fill & Protective Orders

**Location**: `NinjaTraderSimAdapter.cs` → `HandleEntryFill()`

**When**: Entry order fills

**Actions**:
1. Submit protective stop order (at original stop price)
2. Submit target order
3. Position is now protected but stop is still at original level

**Status**: ✅ Protective orders placed, BE monitoring can begin

---

### 4. Break-Even Monitoring (Tick-Based)

**Location**: `RobotSimStrategy.cs` → `OnMarketData()` → `CheckBreakEvenTriggersTickBased()`

**When**: Every tick (MarketDataType.Last only)

**Flow**:
```csharp
// 1. Get active intents that need BE monitoring
var activeIntents = _adapter.GetActiveIntentsForBEMonitoring();

// 2. For each active intent:
foreach (var (intentId, intent, beTriggerPrice, entryPrice, actualFillPrice, direction) in activeIntents)
{
    // 3. Check if tick price reached BE trigger
    bool beTriggerReached = direction == "Long" 
        ? tickPrice >= beTriggerPrice  // Long: price >= trigger
        : tickPrice <= beTriggerPrice;  // Short: price <= trigger
    
    // 4. If triggered, modify stop to break-even
    if (beTriggerReached)
    {
        // Calculate BE stop price (breakout level ± 1 tick)
        decimal beStopPrice = direction == "Long" 
            ? entryPrice - tickSize  // 1 tick below breakout level
            : entryPrice + tickSize; // 1 tick above breakout level
        
        // Modify stop order
        _adapter.ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);
    }
}
```

**Key Points**:
- ✅ Tick-based detection (immediate, not waiting for bar close)
- ✅ Only processes `MarketDataType.Last` (actual trades, not bid/ask)
- ✅ Checks every tick for all active intents
- ✅ Idempotent (checks if BE already modified before processing)

---

### 5. Active Intent Filtering

**Location**: `NinjaTraderSimAdapter.cs` → `GetActiveIntentsForBEMonitoring()`

**Criteria** (ALL must be true):
1. ✅ Entry order is filled (`orderInfo.State == "FILLED"`)
2. ✅ Entry fill time recorded (`orderInfo.EntryFillTime.HasValue`)
3. ✅ Intent exists in `_intentMap`
4. ✅ Intent has `BeTrigger` set (`intent.BeTrigger != null`)
5. ✅ Intent has `EntryPrice` set (`intent.EntryPrice != null`)
6. ✅ Intent has `Direction` set (`intent.Direction != null`)
7. ✅ BE not already modified (`!IsBEModified()`)

**Returns**: List of intents that need BE monitoring

---

### 6. Stop Order Modification

**Location**: `NinjaTraderSimAdapter.cs` → `ModifyStopToBreakEven()`

**Flow**:
1. Find protective stop order for intent (by tag)
2. Check if order exists and is working
3. Check if BE already modified (idempotency)
4. Calculate new stop price (breakout level ± 1 tick)
5. Modify order using NinjaTrader API
6. Log success/failure

**BE Stop Price Calculation**:
```csharp
// BE stop = Breakout level ± 1 tick
decimal beStopPrice = direction == "Long" 
    ? entryPrice - tickSize  // Long: 1 tick below breakout level
    : entryPrice + tickSize; // Short: 1 tick above breakout level
```

**Key Points**:
- ✅ Uses breakout level (`entryPrice`), not actual fill price
- ✅ "1 tick before breakout point" = breakout level ± 1 tick
- ✅ Idempotent (won't modify if already at BE)
- ✅ Handles race conditions (stop order may not exist yet)

---

## Key Concepts

### BE Trigger Price
- **Definition**: Price level that triggers break-even stop modification
- **Calculation**: Entry Price ± (Base Target × 65%)
- **Purpose**: Detect when position has moved favorably enough to move stop to break-even

### BE Stop Price
- **Definition**: New stop loss price after BE trigger is reached
- **Calculation**: Breakout Level ± 1 tick
- **Purpose**: Protect position at break-even (no loss, but can still hit target)

### Breakout Level (Entry Price)
- **Definition**: Strategic entry point (brkLong/brkShort)
- **For Stop Orders**: `brkLong` (RangeHigh + tickSize) or `brkShort` (RangeLow - tickSize)
- **For Limit Orders**: Limit price
- **Used For**: BE stop calculation (not actual fill price)

### Actual Fill Price
- **Definition**: Price at which order actually filled (may differ from breakout level due to slippage)
- **Used For**: Logging/debugging only
- **NOT Used For**: BE stop calculation (uses breakout level instead)

---

## Detection Method

### Tick-Based Detection ✅
- **Method**: `OnMarketData()` override
- **Frequency**: Every tick (MarketDataType.Last)
- **Advantage**: Immediate detection, no waiting for bar close
- **Status**: ✅ Currently implemented

### Bar-Based Detection (Deprecated)
- **Method**: `OnBarUpdate()` → `CheckBreakEvenTriggers()`
- **Frequency**: On bar close
- **Disadvantage**: Delayed detection (waits for bar close)
- **Status**: ❌ Removed (replaced by tick-based)

---

## Potential Issues

### Issue 1: BE Trigger Not Set
**Symptom**: `GetActiveIntentsForBEMonitoring()` returns empty list
**Cause**: Intent registered without `BeTrigger` set
**Fix**: ✅ Fixed - `ComputeAndLogProtectiveOrders()` always computes BE trigger

### Issue 2: Stop Order Not Found
**Symptom**: `BE_TRIGGER_RETRY_NEEDED` events
**Cause**: Race condition - stop order not in account.Orders yet
**Fix**: ✅ Handled - Retries on next tick

### Issue 3: BE Stop Price Wrong
**Symptom**: Stop modified to wrong price
**Cause**: Using wrong price source (fill price vs breakout level)
**Fix**: ✅ Fixed - Uses breakout level (`entryPrice`), not actual fill price

### Issue 4: Detection Not Happening
**Symptom**: BE trigger reached but stop not modified
**Possible Causes**:
- `OnMarketData()` not being called
- Intent not in `GetActiveIntentsForBEMonitoring()` list
- Tick price not reaching trigger level
- Stop order modification failing

---

## Debugging Checklist

If BE detection isn't working:

1. ✅ **Check Intent Registration**: Verify `INTENT_REGISTERED` event has `be_trigger` field
2. ✅ **Check Entry Fill**: Verify `EXECUTION_FILLED` event logged
3. ✅ **Check Active Intents**: Verify intent appears in `GetActiveIntentsForBEMonitoring()` return
4. ✅ **Check Tick Processing**: Verify `OnMarketData()` is being called
5. ✅ **Check Trigger Level**: Verify tick price reaches BE trigger price
6. ✅ **Check Stop Order**: Verify protective stop order exists
7. ✅ **Check Modification**: Verify `ModifyStopToBreakEven()` is called and succeeds

---

## Summary

Break-even detection:
1. ✅ Calculates BE trigger at entry detection (65% of target)
2. ✅ Registers intent with BE trigger
3. ✅ Monitors every tick for BE trigger level
4. ✅ Modifies stop to break-even (breakout level ± 1 tick) when triggered
5. ✅ Uses tick-based detection for immediate response
6. ✅ Handles race conditions and idempotency

The system is designed to automatically protect positions at break-even once they've moved favorably enough (65% of target distance).
