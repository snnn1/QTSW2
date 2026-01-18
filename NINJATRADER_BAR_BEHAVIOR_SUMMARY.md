# NinjaTrader Bar Behavior Summary

## Answer: YES, NinjaTrader Sends Historical Bars

**When you enable a strategy at 07:25 on the 16th, NinjaTrader DOES send all historical bars from the 16th up to that point.**

## How NinjaTrader Works

### When Strategy Is Enabled:

1. **Historical Data Processing First**
   - NinjaTrader processes ALL historical bars from the beginning of the day
   - Bars are sent via `OnBarUpdate()` in chronological order
   - This includes all bars up to the latest **completed** bar before current time
   - Example: If you enable at 07:25, you get bars from 00:00 through 07:24

2. **Transition to Live Data**
   - After historical bars are processed, NinjaTrader switches to live bars
   - New bars arrive as they complete (every minute for 1-minute bars)

### Evidence from Logs:

```
ENGINE_START: 2026-01-17T23:33:53 UTC
First bars received: 2026-01-15 23:02:00 (immediately after start)
```

**This confirms:** Bars arrive immediately after `ENGINE_START`, and they're from earlier times, proving NinjaTrader sends historical data.

## Current Problem

**The Issue:**
- NinjaTrader IS sending historical bars ✅
- But `OnBar()` only buffers bars when `State == RANGE_BUILDING` ❌
- So bars arriving in `PRE_HYDRATION` or `ARMED` states are **LOST** ❌

**Timeline:**
```
07:25 - Strategy enabled
07:25 - ENGINE_START
07:25 - Streams created → PRE_HYDRATION state
07:25 - NinjaTrader sends bars from 00:00-07:24 → IGNORED (wrong state)
07:25 - Pre-hydration reads CSV file → Loads bars from file
07:25 - PRE_HYDRATION_COMPLETE → ARMED state
07:25 - NinjaTrader continues sending bars → IGNORED (wrong state)
02:00 - Range window starts → RANGE_BUILDING state
02:00+ - OnBar() NOW starts buffering bars
```

**Result:** Bars from NinjaTrader are lost, so we read from files instead (redundant!)

## Solution

### We DON'T Need File Pre-Hydration for SIM Mode

**Reason:**
1. NinjaTrader already sends all historical bars
2. We just need to buffer them starting from `ARMED` state
3. File pre-hydration is redundant

**Fix:**
- Change `OnBar()` to buffer bars in `ARMED` state (not just `RANGE_BUILDING`)
- Remove mandatory file pre-hydration for SIM mode
- Keep file pre-hydration as optional fallback for gap detection

## Comparison

| Scenario | File Pre-Hydration Needed? | Why |
|----------|---------------------------|-----|
| **SIM Mode** | ❌ **NO** | NinjaTrader sends historical bars automatically |
| **DRYRUN Mode** | ✅ **YES** | No NinjaTrader data feed, uses file replay |
| **Late Startup** | ⚠️ **Maybe** | If strategy starts after range window, may miss early bars (but NinjaTrader still sends them) |

## Recommendation

**For SIM Mode:**
- ✅ Buffer bars starting from `ARMED` state
- ✅ Remove mandatory file pre-hydration
- ✅ Keep file pre-hydration as optional fallback (for gap detection only)

**For DRYRUN Mode:**
- ✅ Keep file pre-hydration (it's the primary data source)

## Implementation

**Change in `StreamStateMachine.cs` line 934:**
```csharp
// Current:
if (State == StreamState.RANGE_BUILDING)

// New:
if (State == StreamState.ARMED || State == StreamState.RANGE_BUILDING)
```

This will capture all bars from NinjaTrader as they arrive, eliminating the need for file-based pre-hydration in SIM mode.
