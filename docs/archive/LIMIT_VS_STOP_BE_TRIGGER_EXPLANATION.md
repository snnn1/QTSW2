# Limit vs Stop Orders: BE Trigger Calculation

## Entry Price Determination

### Stop Orders (Breakout Entry)
- **Entry Price**: `brkLong` (for Long) or `brkShort` (for Short)
- **Breakout Levels**:
  - `brkLong = RangeHigh + tickSize` (1 tick above range)
  - `brkShort = RangeLow - tickSize` (1 tick below range)
- **Order Type**: `StopMarket` order placed at breakout level
- **When Used**: Normal breakout entry after range lock

### Limit Orders (Immediate Entry)
- **Entry Price**: Still `brkLong` (for Long) or `brkShort` (for Short)
- **Breakout Levels**: Same as stop orders
- **Order Type**: `Limit` order placed at breakout level
- **When Used**: Immediate entry when `freeze_close >= brkLong` (Long) or `freeze_close <= brkShort` (Short)

## Key Point: Entry Price is ALWAYS the Breakout Level

**Both limit and stop orders use the same entry price** - the breakout level (`brkLong` or `brkShort`).

The difference is:
- **Stop Orders**: Wait for price to reach breakout level, then trigger
- **Limit Orders**: Price already beyond breakout level, so enter immediately at breakout level

## BE Trigger Calculation

**The BE trigger calculation is IDENTICAL for both order types:**

```
BE Trigger = Entry Price + (Base Target × 0.65)  [Long]
BE Trigger = Entry Price - (Base Target × 0.65)  [Short]
```

Where `Entry Price` = `brkLong` (Long) or `brkShort` (Short) for **both** limit and stop orders.

## Code Reference

**File**: `modules/robot/core/StreamStateMachine.cs`

**Line 1414** (comment):
```csharp
// entryPrice is the breakout level (brkLong/brkShort for stop orders, limit price for limit orders)
```

**Note**: The comment says "limit price for limit orders" but in practice, limit orders also use the breakout level (`brkLong`/`brkShort`) as their entry price. The limit order is placed at the breakout level, not at a different price.

**Line 5577-5578** (BE Trigger Calculation):
```csharp
var beTriggerPts = _baseTarget * 0.65m; // 65% of target
var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
```

Where `entryPrice` comes from `_intendedEntryPrice`, which is set to `brkLong` or `brkShort` regardless of order type.

## Example

**Scenario**: Range High = 100.00, Range Low = 99.00, Tick Size = 0.25, Base Target = 4.00

**Breakout Levels**:
- `brkLong = 100.00 + 0.25 = 100.25`
- `brkShort = 99.00 - 0.25 = 98.75`

**Long Trade (Stop Order)**:
- Entry Price: 100.25 (brkLong)
- BE Trigger: 100.25 + (4.00 × 0.65) = **102.85**

**Long Trade (Limit Order - Immediate Entry)**:
- Entry Price: 100.25 (brkLong) - same as stop order!
- BE Trigger: 100.25 + (4.00 × 0.65) = **102.85** - same as stop order!

**Short Trade (Stop Order)**:
- Entry Price: 98.75 (brkShort)
- BE Trigger: 98.75 - (4.00 × 0.65) = **96.15**

**Short Trade (Limit Order - Immediate Entry)**:
- Entry Price: 98.75 (brkShort) - same as stop order!
- BE Trigger: 98.75 - (4.00 × 0.65) = **96.15** - same as stop order!

## Summary

✅ **Both limit and stop orders use the same entry price** (breakout level)
✅ **BE trigger calculation is identical** for both order types
✅ **BE trigger = Entry Price ± (65% of target)**, where Entry Price = `brkLong` or `brkShort`

The only difference between limit and stop orders is **when** they execute:
- **Stop Orders**: Wait for breakout to occur
- **Limit Orders**: Enter immediately because breakout already occurred

But the **entry price** and **BE trigger** are calculated the same way for both.
