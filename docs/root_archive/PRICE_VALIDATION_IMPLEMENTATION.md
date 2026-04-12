# Price Limit Validation Implementation

## Summary

**Status**: ✅ **IMPLEMENTED** - Pre-submission price validation for stop entry orders

**Date**: 2026-01-28

**Issue**: Orders rejected with "The current price is outside the price limits set for this product" due to stale breakout levels or market gaps.

---

## Implementation

### What Was Added

**Pre-submission price validation** in `SubmitStopEntryOrderReal()` method:

1. **Get current market price** using dynamic typing (handles NT API variations)
   - For long stops: uses ask price (buy stop triggers above ask)
   - For short stops: uses bid price (sell stop triggers below bid)

2. **Calculate stop distance** from current market price

3. **Check against configurable thresholds**:
   - **Micro futures** (M2K, MGC, MES, etc.): **50 points** maximum
   - **Mini futures** (ES, NQ): **200 points** maximum
   - **Default**: **100 points** for other instruments

4. **Reject order before submission** if distance exceeds threshold

### Code Location

**Files Modified**:
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` (line ~2561)
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (line ~2317)

**Method**: `SubmitStopEntryOrderReal()`

**Placement**: After pre-submission checks, before order creation

---

## Behavior

### Fail-Closed Design

- **If validation fails**: Order rejected before submission
- **Clear diagnostic event**: `STOP_PRICE_VALIDATION_FAILED` logged
- **Journal entry**: Rejection recorded in execution journal
- **No silent failures**: All rejections are logged and observable

### Fail-Open Fallback

- **If market data unavailable**: Validation skipped (fail-open)
- **Rationale**: NinjaTrader will reject invalid prices anyway
- **Warning logged**: `PRICE_VALIDATION_WARNING` event emitted
- **Order proceeds**: Submission continues, NT validates

### Instrument-Specific Thresholds

| Instrument Type | Max Distance | Example |
|----------------|--------------|---------|
| Micro Futures (M2K, MGC, MES) | 50 points | M2K: 5.0 points |
| Mini Futures (ES, NQ) | 200 points | ES: 50.0 points |
| Default | 100 points | Generic fallback |

---

## Example Scenarios

### Scenario 1: Stale Breakout Level ✅ BLOCKED

```
Range locked earlier: Breakout level = 2700
Market moved: Current price = 2650
Stop price: 2700
Distance: 50 points
Threshold: 50 points (M2K)
Result: ✅ REJECTED (exceeds threshold)
```

### Scenario 2: Valid Breakout Level ✅ ALLOWED

```
Range locked recently: Breakout level = 2655
Current price: 2650
Stop price: 2655
Distance: 5 points
Threshold: 50 points (M2K)
Result: ✅ ALLOWED (within threshold)
```

### Scenario 3: Market Gap ✅ BLOCKED

```
Range locked at close: Breakout level = 2700
Market gaps at open: Current price = 2650
Stop price: 2700
Distance: 50 points
Threshold: 50 points (M2K)
Result: ✅ REJECTED (exceeds threshold)
```

---

## Log Events

### Success Case (Validation Passes)

No special event - order proceeds normally.

### Failure Case (Validation Fails)

**Event**: `STOP_PRICE_VALIDATION_FAILED`

```json
{
  "event": "STOP_PRICE_VALIDATION_FAILED",
  "intent_id": "...",
  "stop_price": 2700.0,
  "current_market_price": 2650.0,
  "stop_distance_points": 50.0,
  "direction": "Long",
  "reason": "Stop price 2700 is 50.00 points from current market 2650.00, exceeding limit of 50 points. This indicates a stale breakout level or market gap.",
  "note": "Order rejected before submission to prevent NinjaTrader price limit error. This indicates a stale breakout level or significant market gap."
}
```

### Warning Case (Market Data Unavailable)

**Event**: `PRICE_VALIDATION_WARNING`

```json
{
  "event": "PRICE_VALIDATION_WARNING",
  "warning": "Could not access market data for price validation",
  "error": "...",
  "stop_price": 2700.0,
  "direction": "Long",
  "note": "Proceeding with order submission - NinjaTrader will validate price"
}
```

---

## Benefits

1. **Prevents NinjaTrader rejections**: Catches invalid prices before submission
2. **Clear diagnostics**: Explains why order was rejected
3. **Fail-closed**: Blocks invalid orders deterministically
4. **Observable**: All rejections logged and journaled
5. **Instrument-aware**: Different thresholds for different instrument types
6. **Graceful degradation**: Falls back if market data unavailable

---

## Future Enhancements (Optional)

1. **Configurable thresholds**: Make thresholds configurable via settings
2. **Range age check**: Invalidate ranges older than X hours
3. **Dynamic adjustment**: Adjust stop price to valid range instead of rejecting
4. **Session-aware thresholds**: Different limits for different market sessions

---

## Testing Recommendations

1. **Test stale breakout**: Lock range, wait for market to move, verify rejection
2. **Test valid breakout**: Lock range recently, verify order proceeds
3. **Test market gap**: Simulate gap scenario, verify rejection
4. **Test micro futures**: Verify 50-point threshold for M2K/MGC
5. **Test mini futures**: Verify 200-point threshold for ES/NQ
6. **Test market data unavailable**: Verify graceful fallback

---

## Related Issues

- **M2K Order Issues Analysis**: See `M2K_ORDER_ISSUES_ANALYSIS.md`
- **Price Limit Errors**: Issue #3 in analysis document
- **OCO ID Reuse**: Issue #2 (requires manual cleanup)
- **Order Tracking Race Condition**: Issue #1 (left as-is per guidance)
