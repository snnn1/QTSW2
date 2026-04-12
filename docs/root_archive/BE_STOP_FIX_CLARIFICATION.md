# Break-Even Stop Fix Clarification

## Issue Clarified

**User Requirement**: BE stop should be **1 tick before breakout level** (not fill price)

## What Changed

### Previous Implementation (INCORRECT)
- BE stop was calculated using actual fill price
- This meant slippage would affect BE stop placement
- Example: If breakout level is 100.00 but fill is 100.05, BE stop would be at 100.04 (1 tick below fill)

### Current Implementation (CORRECT)
- BE stop is calculated using **breakout level** (entryPrice from intent)
- Breakout level is the strategic entry point, slippage doesn't affect BE stop
- Example: If breakout level is 100.00, BE stop is at 99.75 (1 tick below breakout level), regardless of actual fill price

## Code Changes

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Before**:
```csharp
decimal fillPriceForBE = actualFillPrice ?? entryPrice;
decimal beStopPrice = direction == "Long" 
    ? fillPriceForBE - tickSize  // 1 tick below actual fill
    : fillPriceForBE + tickSize; // 1 tick above actual fill
```

**After**:
```csharp
decimal breakoutLevel = entryPrice;  // Breakout level (brkLong/brkShort for stop orders)
decimal beStopPrice = direction == "Long" 
    ? breakoutLevel - tickSize  // 1 tick below breakout level
    : breakoutLevel + tickSize; // 1 tick above breakout level
```

## Understanding Breakout Level vs Fill Price

- **Breakout Level** (`entryPrice` in intent):
  - For stop-market orders: This is `brkLong` or `brkShort` (the breakout trigger level)
  - For limit orders: This is the limit price
  - This is the **strategic entry point** - where you intended to enter

- **Fill Price** (`actualFillPrice` from journal):
  - This is the actual price at which the order filled
  - May differ from breakout level due to slippage
  - For stop orders: Fill might be slightly worse than breakout level
  - For limit orders: Fill might be at or better than limit price

## Why Use Breakout Level?

1. **Consistency**: BE stop placement is consistent regardless of slippage
2. **Strategic**: The breakout level is the strategic entry point, not the fill price
3. **Protection**: Protects the trade at the intended entry level, not the actual fill level

## Example

**Long Trade**:
- Breakout Level: 100.00
- Actual Fill: 100.05 (slippage)
- Tick Size: 0.25
- **BE Stop**: 99.75 (1 tick below breakout level of 100.00)
- **NOT**: 100.00 (1 tick below fill price of 100.05)

**Short Trade**:
- Breakout Level: 100.00
- Actual Fill: 99.95 (slippage)
- Tick Size: 0.25
- **BE Stop**: 100.25 (1 tick above breakout level of 100.00)
- **NOT**: 100.20 (1 tick above fill price of 99.95)

## Status

✅ **FIXED**: BE stop now uses breakout level (entryPrice) instead of fill price
✅ **BUILT**: DLL rebuilt with fix
✅ **COPIED**: DLL copied to NinjaTrader folders

**Action Required**: Restart NinjaTrader to load the updated DLL
