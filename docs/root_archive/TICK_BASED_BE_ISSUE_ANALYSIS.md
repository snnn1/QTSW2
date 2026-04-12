# Tick-Based BE Implementation Issue Analysis

## User Report
"The issues started when we tried to make the break-even logic check on every tick"

## Analysis

### What Changed
1. **Calculate = OnEachTick** (was OnBarClose)
2. **OnMarketData() override** added for tick-based BE detection
3. **CheckBreakEvenTriggersTickBased()** method added

### Potential Issues Found

#### Issue 1: Calculate = OnEachTick May Block Realtime Transition ⚠️
**Problem**: NinjaTrader may require tick data to be available before transitioning to Realtime when `Calculate = OnEachTick` is set.

**Impact**: 
- If MGC/MYM/M2K don't have tick data available, NinjaTrader may wait indefinitely
- Strategy stays in DataLoaded state waiting for ticks
- This is a NinjaTrader platform behavior, not our code

**Evidence**: 
- Strategies complete initialization (`DATALOADED_INITIALIZATION_COMPLETE`)
- But never reach Realtime (`REALTIME_STATE_REACHED` not logged)
- NinjaTrader shows "loading connection" status

#### Issue 2: Instrument.GetInstrument() Call in BE Logic ⚠️
**Problem**: `CheckBreakEvenTriggersTickBased()` calls `Instrument.GetInstrument()` (line 1199) which can block.

**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs` line 1199

**Impact**:
- Even though this runs in Realtime (not DataLoaded), it could cause performance issues
- If instrument doesn't exist, call may hang
- This is called on every tick, so any delay is multiplied

**Fix Applied**: ✅ Changed to use strategy's Instrument directly (no resolution needed)

### OnMarketData() Analysis ✅
**Status**: Safe - won't block initialization
- Only runs in Realtime state (not DataLoaded)
- Has proper guards (_initFailed, _engineReady checks)
- Filters to Last ticks only (avoids bid/ask noise)
- Wrapped in try-catch (won't crash)

## Root Cause Hypothesis

**Most Likely**: `Calculate = OnEachTick` requires NinjaTrader to have tick data available before transitioning to Realtime.

**Why MGC/MYM/M2K Specifically**:
- These instruments may not have tick data feed available
- NinjaTrader waits for tick data before allowing Realtime transition
- Other instruments (MES, MNG) may have tick data, so they transition successfully

## Solutions

### Solution 1: Revert to OnBarClose (Temporary)
**Quick Fix**: Change back to `Calculate = Calculate.OnBarClose` to test if this resolves the issue.

**Trade-off**: BE detection will be bar-based (slower) instead of tick-based (faster)

### Solution 2: Hybrid Approach (Recommended)
**Better Fix**: Use `Calculate = OnBarClose` but still check BE on ticks:
- Keep `OnMarketData()` override
- Check BE triggers on every tick (when ticks are available)
- Fallback to bar-based check if no ticks

**Benefit**: 
- Doesn't block Realtime transition (OnBarClose doesn't require ticks)
- Still gets tick-based BE detection when ticks are available
- Works even if tick data isn't available

### Solution 3: Make Calculate Mode Configurable
**Best Fix**: Add strategy parameter to choose Calculate mode:
- `OnBarClose` (default, doesn't block)
- `OnEachTick` (faster BE, but requires tick data)

## Recommendation

**Immediate**: Revert to `Calculate = OnBarClose` to test if this fixes the loading issue.

**If that fixes it**: Implement hybrid approach (OnBarClose + OnMarketData for tick-based BE)

**Long-term**: Make Calculate mode configurable via strategy parameter

## Code Changes Made

1. ✅ Removed `Instrument.GetInstrument()` call from `CheckBreakEvenTriggersTickBased()`
2. ✅ Changed to use strategy's Instrument directly (no resolution needed)
3. ⏳ Consider reverting `Calculate = OnEachTick` to `OnBarClose` for testing
