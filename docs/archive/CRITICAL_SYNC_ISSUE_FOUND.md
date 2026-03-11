# CRITICAL SYNC ISSUE FOUND

## Problem: RobotSimStrategy.cs Not Synced

**Evidence**: NinjaTrader output shows `Calculate=On bar close` but our code has `Calculate = Calculate.OnEachTick`

This means the strategy file in the NinjaTrader project is **NOT** synced with our modules version.

## Impact

All the fixes we made are **NOT** in the NinjaTrader project:
- ❌ Fire-and-forget BarsRequest (ThreadPool.QueueUserWorkItem)
- ❌ Fail-closed mechanism (_initFailed flag)
- ❌ Enhanced diagnostic logging
- ❌ TradingHours access hardening
- ❌ Calculate = OnEachTick (for tick-based BE detection)

## Why Strategies Are Stuck

The old code in NinjaTrader project likely:
- Still has blocking BarsRequest calls
- Still has Calculate = OnBarClose (wrong mode)
- Missing all the hardening fixes

## Solution

**CRITICAL**: Copy `modules/robot/ninjatrader/RobotSimStrategy.cs` to NinjaTrader project:
- Location: `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\Strategies\RobotSimStrategy.cs`
- Or wherever your NinjaTrader strategies are located

## Additional Issues Found

1. **StartBehavior=WaitUntilFlat**
   - May block if existing position exists (MNG 03-26 1L)
   - Consider changing to `StartBehavior=Immediately`

2. **Strategies Disabling Immediately**
   - May be due to errors in old code
   - Will be fixed once synced

## Next Steps

1. ✅ Copy RobotSimStrategy.cs to NinjaTrader project
2. ✅ Rebuild NinjaTrader project
3. ✅ Restart strategies
4. ✅ Verify Calculate=OnEachTick appears in output
5. ✅ Check if strategies reach Realtime state

## Files That Need Manual Copy

- `modules/robot/ninjatrader/RobotSimStrategy.cs` → NinjaTrader Strategies folder
- `modules/robot/ninjatrader/NinjaTraderBarRequest.cs` → NinjaTrader Strategies folder (if not already there)

These files are NOT in RobotCore_For_NinjaTrader because they're NinjaTrader-specific strategy files.
