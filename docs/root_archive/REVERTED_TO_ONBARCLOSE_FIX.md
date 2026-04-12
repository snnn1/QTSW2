# Reverted to OnBarClose - Fix for Loading Issue

## Changes Made

### 1. Calculate Mode Reverted ✅
**Changed**: `Calculate = Calculate.OnEachTick` → `Calculate = Calculate.OnBarClose`

**Reason**: 
- `OnEachTick` requires NinjaTrader to have tick data available before transitioning to Realtime
- MGC/MYM/M2K may not have tick data available, causing NinjaTrader to wait indefinitely
- `OnBarClose` doesn't require ticks, so won't block Realtime transition

### 2. OnMarketData() Override Kept ✅
**Status**: Still present and functional

**Why**: 
- `OnMarketData()` fires in Realtime state regardless of `Calculate` mode
- Provides tick-based BE detection when ticks are available
- Best of both worlds: no blocking + tick-based BE

### 3. Instrument.GetInstrument() Removed ✅
**Changed**: Removed blocking `Instrument.GetInstrument()` call from `CheckBreakEvenTriggersTickBased()`

**Reason**: 
- Can block/hang if instrument doesn't exist
- Strategy's Instrument is already loaded - use it directly

## How It Works Now

### Bar Processing
- `OnBarUpdate()` fires on bar close (as before)
- Processes bars and drives engine

### Tick Processing (BE Detection)
- `OnMarketData()` fires on every tick (in Realtime state)
- Checks BE triggers using tick price
- Modifies stop orders when BE trigger reached

### Result
- ✅ Strategies reach Realtime immediately (no tick data required)
- ✅ Tick-based BE detection still works (via OnMarketData)
- ✅ No blocking operations in DataLoaded
- ✅ No blocking operations in BE logic

## Testing

After copying this file to NinjaTrader project and rebuilding:

1. **Verify Calculate Mode**: NinjaTrader output should show `Calculate=On bar close`
2. **Check Realtime Transition**: Strategies should reach Realtime immediately
3. **Verify BE Detection**: OnMarketData() should still fire and detect BE triggers

## Expected Behavior

- **MGC/MYM/M2K**: Should reach Realtime state immediately (no longer stuck)
- **BE Detection**: Still works tick-based via OnMarketData()
- **Performance**: No degradation - OnMarketData() is lightweight

## Files Changed

- `modules/robot/ninjatrader/RobotSimStrategy.cs`:
  - Line 64: `Calculate = Calculate.OnBarClose` (was OnEachTick)
  - Line 1199: Removed `Instrument.GetInstrument()` call (uses strategy Instrument directly)

## Next Steps

1. Copy `RobotSimStrategy.cs` to NinjaTrader project
2. Rebuild NinjaTrader project
3. Test MGC/MYM/M2K strategies - should reach Realtime immediately
4. Verify BE detection still works (check logs for BE modifications)
