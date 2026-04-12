# Break-Even Stop Calculation Verification

## Current Implementation

```csharp
decimal breakoutLevel = entryPrice;  // Breakout level from intent
decimal beStopPrice = direction == "Long" 
    ? breakoutLevel - tickSize  // 1 tick below breakout level for long
    : breakoutLevel + tickSize; // 1 tick above breakout level for short
```

## Examples

### Long Trade
- Breakout Level: 100.00
- Tick Size: 0.25
- **BE Stop**: 100.00 - 0.25 = **99.75** (1 tick BELOW breakout level) ✓

### Short Trade  
- Breakout Level: 100.00
- Tick Size: 0.25
- **BE Stop**: 100.00 + 0.25 = **100.25** (1 tick ABOVE breakout level) ✓

## Verification

✅ **Long**: `breakoutLevel - tickSize` = 1 tick BELOW breakout level
✅ **Short**: `breakoutLevel + tickSize` = 1 tick ABOVE breakout level

**Status**: Code is correct - matches requirement "1 tick below breakout level (or higher if it's short)"
