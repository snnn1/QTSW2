# Break-Even Trigger Calculation Explanation

## How BE Trigger is Calculated

The break-even trigger is calculated as **65% of the target distance FROM the entry point** (breakout level).

### Formula

**For Long Trades:**
```
BE Trigger = Entry Price + (Base Target × 0.65)
```

**For Short Trades:**
```
BE Trigger = Entry Price - (Base Target × 0.65)
```

### Key Points

1. **Base**: The calculation starts from the **entry price** (which is the breakout level)
2. **Percentage**: Uses **65% of the target distance** (not 65% of the entry price itself)
3. **Direction**: 
   - Long: Add 65% of target to entry price
   - Short: Subtract 65% of target from entry price

## Code Location

**File**: `modules/robot/core/StreamStateMachine.cs`

**Line 5577-5578**:
```csharp
var beTriggerPts = _baseTarget * 0.65m; // 65% of target
var beTriggerPrice = direction == "Long" ? entryPrice + beTriggerPts : entryPrice - beTriggerPts;
```

## Example

**Long Trade:**
- Entry Price (Breakout Level): 100.00
- Base Target: 4.00 points
- BE Trigger = 100.00 + (4.00 × 0.65) = 100.00 + 2.60 = **102.60**

**Short Trade:**
- Entry Price (Breakout Level): 100.00
- Base Target: 4.00 points
- BE Trigger = 100.00 - (4.00 × 0.65) = 100.00 - 2.60 = **97.40**

## Important Distinction

**NOT**: 65% of the breakout level itself
- ❌ Wrong: If breakout is 100.00, then 65% = 65.00

**CORRECT**: Entry point + 65% of target distance
- ✅ Correct: Entry 100.00 + (Target 4.00 × 65%) = 102.60

## Summary

The BE trigger is calculated **FROM the entry point** (breakout level), using **65% of the target distance**. It is NOT 65% of the breakout level value itself.
