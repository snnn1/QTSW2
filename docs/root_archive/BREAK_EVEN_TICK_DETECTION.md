# Break-Even Tick-Based Detection Implementation

## Summary

**Status**: ✅ **IMPLEMENTED** - Break-even detection now happens on tick, not bar close

**Date**: 2026-01-28

**Issue**: Break-even detection was missing from Custom folder version - was only in modules version

---

## What Was Added

### 1. `OnMarketData()` Override

**Location**: `RobotSimStrategy.cs` (Custom folder)

**Purpose**: Process tick data for break-even detection

**Key Features**:
- Only processes `MarketDataType.Last` (actual trades, not bid/ask noise)
- Only processes ticks for strategy's instrument
- Calls `CheckBreakEvenTriggersTickBased()` on every tick

**Code**:
```csharp
protected override void OnMarketData(MarketDataEventArgs e)
{
    // Only process Last price ticks (avoid bid/ask noise)
    if (e.MarketDataType != MarketDataType.Last) return;
    
    // Only process ticks for this strategy's instrument
    if (e.Instrument != Instrument) return;
    
    var tickPrice = (decimal)e.Price;
    CheckBreakEvenTriggersTickBased(tickPrice, utcNow);
}
```

### 2. `CheckBreakEvenTriggersTickBased()` Method

**Location**: `RobotSimStrategy.cs` (Custom folder)

**Purpose**: Check break-even triggers using actual tick price

**Key Features**:
- Gets active intents from adapter (`GetActiveIntentsForBEMonitoring()`)
- Checks if tick price reached BE trigger level:
  - **Long**: `tickPrice >= beTriggerPrice`
  - **Short**: `tickPrice <= beTriggerPrice`
- Calculates BE stop price (entry ± 1 tick)
- Modifies stop order to break-even
- Handles race conditions (stop order may not exist yet)

**Code**:
```csharp
private void CheckBreakEvenTriggersTickBased(decimal tickPrice, DateTimeOffset utcNow)
{
    var activeIntents = _adapter.GetActiveIntentsForBEMonitoring();
    
    foreach (var (intentId, intent, beTriggerPrice, entryPrice, direction) in activeIntents)
    {
        bool beTriggerReached = direction == "Long" 
            ? tickPrice >= beTriggerPrice 
            : tickPrice <= beTriggerPrice;
        
        if (beTriggerReached)
        {
            // Calculate BE stop price (entry ± 1 tick)
            decimal beStopPrice = direction == "Long" 
                ? entryPrice - tickSize 
                : entryPrice + tickSize;
            
            // Modify stop order
            _adapter.ModifyStopToBreakEven(intentId, intent.Instrument ?? "", beStopPrice, utcNow);
        }
    }
}
```

---

## How It Works

### Flow

1. **Tick Arrives**: `OnMarketData()` called by NinjaTrader on every tick
2. **Filter**: Only process `Last` price ticks (actual trades)
3. **Check**: For each active intent, check if tick price reached BE trigger
4. **Trigger**: If reached, calculate BE stop price and modify order
5. **Log**: Emit `BE_TRIGGER_REACHED` event

### Break-Even Trigger Logic

**BE Trigger Price**: 65% of target distance from entry
- Example: Entry = 100, Target = 110, Target distance = 10
- BE trigger = 100 + (10 × 0.65) = 106.5

**Detection**:
- **Long**: When tick price >= 106.5 → move stop to entry - 1 tick
- **Short**: When tick price <= 106.5 → move stop to entry + 1 tick

### Active Intents

`GetActiveIntentsForBEMonitoring()` returns intents that:
- Have entry order filled (`State == "FILLED"`)
- Have BE trigger price set (`BeTrigger != null`)
- Haven't already triggered BE (`IsBEModified() == false`)

---

## Benefits

1. **Tick-Based Detection**: Immediate detection on every tick, not delayed until bar close
2. **More Accurate**: Uses actual trade prices, not bar high/low
3. **Faster Response**: BE stop moved as soon as trigger reached
4. **Race Condition Handling**: Handles cases where stop order not yet in account
5. **Idempotent**: Won't trigger multiple times (checks `IsBEModified()`)

---

## Comparison: Before vs After

### Before (Missing)
- ❌ No `OnMarketData()` override
- ❌ Break-even not checked on tick
- ❌ May have been checked on bar close (if at all)

### After (Implemented)
- ✅ `OnMarketData()` override processes ticks
- ✅ Break-even checked on every tick
- ✅ Immediate detection when trigger reached
- ✅ Uses actual trade prices (Last ticks)

---

## Log Events

### Success: `BE_TRIGGER_REACHED`

```json
{
  "event": "BE_TRIGGER_REACHED",
  "intent_id": "...",
  "instrument": "M2K",
  "direction": "Long",
  "entry_price": 2650.0,
  "be_trigger_price": 2665.0,
  "be_stop_price": 2649.75,
  "tick_price": 2665.25,
  "detection_method": "TICK_BASED",
  "note": "Break-even trigger reached (tick-based detection) - stop order modified to break-even"
}
```

### Retry Needed: `BE_TRIGGER_RETRY_NEEDED`

```json
{
  "event": "BE_TRIGGER_RETRY_NEEDED",
  "intent_id": "...",
  "error": "Stop order not found",
  "is_retryable": true,
  "note": "Break-even trigger reached but stop order not found yet (race condition) - will retry on next tick"
}
```

### Failure: `BE_TRIGGER_FAILED`

```json
{
  "event": "BE_TRIGGER_FAILED",
  "intent_id": "...",
  "error": "Order modification failed",
  "is_retryable": false,
  "note": "Break-even trigger reached but stop modification failed"
}
```

---

## Testing Recommendations

1. **Test Long Position**:
   - Enter long at 2650
   - BE trigger = 2665 (65% of target)
   - Verify stop moves to BE when tick >= 2665

2. **Test Short Position**:
   - Enter short at 2650
   - BE trigger = 2635 (65% of target)
   - Verify stop moves to BE when tick <= 2635

3. **Test Race Condition**:
   - Enter position
   - Immediately check BE trigger
   - Verify retry logic if stop order not found

4. **Test Idempotency**:
   - Trigger BE once
   - Verify it doesn't trigger again on subsequent ticks

---

## Related Files

- **Strategy**: `RobotSimStrategy.cs` (Custom folder)
- **Adapter**: `NinjaTraderSimAdapter.cs` - `GetActiveIntentsForBEMonitoring()`
- **Adapter**: `NinjaTraderSimAdapter.NT.cs` - `ModifyStopToBreakEvenReal()`
- **Journal**: `ExecutionJournal.cs` - `IsBEModified()`

---

## Notes

- **Tick Filtering**: Only `Last` price ticks are processed (actual trades)
- **Bid/Ask Ignored**: Avoids noise from bid/ask updates
- **Instrument Filtering**: Only processes ticks for strategy's instrument
- **Exception Handling**: All exceptions caught to prevent chart crashes
- **Race Conditions**: Handles cases where stop order not yet in account
