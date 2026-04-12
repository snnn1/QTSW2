# OnBarUpdate() and Hydration - Complete Explanation

**Date**: 2026-01-30  
**Topic**: How OnBarUpdate() is used for hydration, what was wrong before, and how it was fixed

---

## What is Hydration?

**Hydration** is the process of loading historical bars into streams before they start trading. This gives streams the price history they need to:
- Calculate initial ranges
- Detect breakouts
- Make informed trading decisions

**Two Types of Hydration**:
1. **Pre-Hydration** (SIM mode): Historical bars loaded via NinjaTrader's `BarsRequest` API
2. **File-Based Hydration** (DRYRUN mode): Historical bars loaded from CSV/Parquet files

### Critical Invariant: Hydration Control

**Invariant**: **OnBarUpdate() may deliver hydration bars, but hydration state progression is driven exclusively by Tick() and BarsRequest completion.**

- **OnBarUpdate()** is **data ingress only** - delivers bars but does NOT control hydration state
- **Hydration completion** is driven by:
  1. BarsRequest completion (marks when historical bars are loaded)
  2. Direct bar injection (`LoadPreHydrationBars()` feeds bars directly)
  3. Tick() evaluation (`HandlePreHydrationState()` checks if hydration is complete)
- **Hydration completion must never depend on OnBarUpdate()** - this prevents regressions

---

## How OnBarUpdate() is Used for Hydration

### Step 1: BarsRequest Initiates Historical Bar Loading

When the strategy starts (in `OnStateChange(State.DataLoaded)`):

1. **Strategy calls `RequestHistoricalBarsForPreHydration()`**
   - Requests historical bars from NinjaTrader using `BarsRequest` API
   - Requests bars from `range_start_time` to `slot_time` (or current time if restart)
   - Marks BarsRequest as "pending" to prevent premature range lock

2. **NinjaTrader processes BarsRequest asynchronously**
   - Loads historical bars from its data feed
   - Returns bars via callback when complete

### Step 2: Historical Bars Feed Through OnBarUpdate()

**Critical Point**: When NinjaTrader returns historical bars from `BarsRequest`, they **feed through `OnBarUpdate()`** just like live bars!

**Flow**:
```
BarsRequest Complete
        ↓
NinjaTrader Returns Historical Bars
        ↓
OnBarUpdate() Called for Each Historical Bar
        ↓
_engine.OnBar() → Routes Bar to Streams
        ↓
Streams Buffer Bars During PRE_HYDRATION State
        ↓
_engine.Tick() → Processes Pre-Hydration State
```

### Step 3: OnBarUpdate() Delivers Bars to Engine

In `OnBarUpdate()` (line 1216):
```csharp
// Deliver bar data to engine (bars only provide market data, not time advancement)
_engine.OnBar(barUtcOpenTime, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
```

**What `_engine.OnBar()` does**:
- Routes bar to appropriate streams (canonicalization: MES → ES)
- If stream is in `PRE_HYDRATION` state, bar is buffered
- If stream is in `ARMED` or `RANGE_BUILDING` state, bar is processed normally

### Step 4: LoadPreHydrationBars() Also Feeds Bars Directly

**Alternative Path**: `LoadPreHydrationBars()` can also feed bars directly to streams:

```csharp
// In RequestHistoricalBarsForPreHydration() callback (line 863)
_engine.LoadPreHydrationBars(instrumentName, bars, DateTimeOffset.UtcNow);
```

**What `LoadPreHydrationBars()` does**:
- Feeds bars directly to stream buffers (bypasses `OnBarUpdate()`)
- Used when BarsRequest completes and returns all bars at once
- Ensures bars are available even if `OnBarUpdate()` hasn't been called yet

**Two Paths for Historical Bars**:
1. **Path A**: BarsRequest callback → `LoadPreHydrationBars()` → Direct to stream buffers
2. **Path B**: BarsRequest callback → NinjaTrader feeds bars → `OnBarUpdate()` → `_engine.OnBar()` → Stream buffers

**Both paths work**, but Path A is more direct and reliable.

---

## What Was Wrong Before?

### Problem 1: OnBarUpdate() Was the ONLY Way Tick() Ran

**Before the Fix**:
- `Tick()` was **only** called from `OnBarUpdate()`
- `Tick()` only ran when bars closed
- If bars stopped closing (data feed issues), `Tick()` stopped running

**Impact**:
- Time-based logic (range lock checks) stopped executing
- Streams stuck in `PRE_HYDRATION` or `RANGE_BUILDING` states
- Range lock checks didn't run because `Tick()` wasn't being called

### Problem 2: BarsRequest Bars Didn't Always Feed Through OnBarUpdate()

**Before the Fix**:
- Historical bars from `BarsRequest` sometimes didn't feed through `OnBarUpdate()`
- BarsRequest callback returned bars, but NinjaTrader didn't always call `OnBarUpdate()` for them
- Streams waited indefinitely for bars that never arrived

**Impact**:
- Streams stuck in `PRE_HYDRATION` state
- `PRE_HYDRATION` timeout forced transition to `ARMED` without bars
- Range calculations started with insufficient data

### Problem 3: Tick() Dependency on Bar Arrival

**Before the Fix**:
- `Tick()` execution was **completely dependent** on `OnBarUpdate()` being called
- No fallback mechanism for continuous execution
- If bars stopped, everything stopped

**Impact**:
- Range lock checks didn't run
- Time-based state transitions didn't occur
- Streams could get stuck indefinitely

---

## How the Fix Solved These Problems

### Fix 1: OnMarketData() Calls Tick() Continuously

**The Fix** (in `OnMarketData()`, line 1286):
```csharp
// CRITICAL FIX: Drive Tick() from tick flow to ensure continuous execution
// This ensures range lock checks and time-based logic run even when bars aren't closing
// Tick() is idempotent and safe to call frequently
// 
// INVARIANT: Tick() must run even when bars are not closing.
// Tick() must NEVER depend on OnBarUpdate liveness.
// This prevents regression if someone removes OnBarUpdate Tick() call.

_engine.Tick(utcNow);
```

**What This Fixes**:
- ✅ `Tick()` now runs **continuously** on every tick (via `OnMarketData()`)
- ✅ `Tick()` also runs when bars arrive (via `OnBarUpdate()`)
- ✅ Time-based logic (range lock checks) runs even if bars stop
- ✅ Streams can transition states even without bar arrivals

**Result**: **Dual execution paths**:
- **Bar-driven**: `OnBarUpdate()` → `Tick()` (when bars close)
- **Tick-driven**: `OnMarketData()` → `Tick()` (continuous)

### Fix 2: LoadPreHydrationBars() Feeds Bars Directly

**The Fix** (in `RequestHistoricalBarsForPreHydration()`, line 863):
```csharp
// Feed bars to engine for pre-hydration
_engine.LoadPreHydrationBars(instrumentName, bars, DateTimeOffset.UtcNow);
```

**What This Fixes**:
- ✅ Historical bars feed directly to stream buffers
- ✅ Doesn't depend on `OnBarUpdate()` being called for each bar
- ✅ Ensures bars are available even if NinjaTrader doesn't call `OnBarUpdate()`

**Result**: **Two paths for historical bars**:
- **Direct path**: BarsRequest callback → `LoadPreHydrationBars()` → Stream buffers
- **Indirect path**: BarsRequest callback → NinjaTrader → `OnBarUpdate()` → Stream buffers

### Fix 3: BarsRequest Marked as Pending Before Queuing

**The Fix** (in `OnStateChange(State.DataLoaded)`, lines 244-252):
```csharp
// CRITICAL: Mark ALL instruments as pending BEFORE queuing BarsRequest
// This ensures streams wait even if they process ticks before BarsRequest completes
// This prevents race condition where stream checks IsBarsRequestPending before it's marked

foreach (var instrument in executionInstruments)
{
    // Mark as pending immediately (synchronously) before queuing BarsRequest
    _engine.MarkBarsRequestPending(instrument, DateTimeOffset.UtcNow);
}
```

**What This Fixes**:
- ✅ Prevents race condition where streams check `IsBarsRequestPending()` before it's marked
- ✅ Ensures streams wait for BarsRequest even if they process ticks early
- ✅ Prevents premature range lock before historical bars arrive

**Result**: **Synchronized BarsRequest tracking** - streams always know when BarsRequest is pending

---

## Complete Hydration Flow (After Fix)

### SIM Mode Hydration Flow

```
1. Strategy Starts (OnStateChange(State.DataLoaded))
        ↓
2. RequestHistoricalBarsForPreHydration() Called
        ↓
3. MarkBarsRequestPending() - Mark instruments as pending
        ↓
4. BarsRequest Queued (asynchronous, non-blocking)
        ↓
5. Strategy Reaches Realtime State (doesn't wait for BarsRequest)
        ↓
6. OnMarketData() Starts Calling Tick() Continuously
        ↓
7. Tick() → HandlePreHydrationState() → Waits for BarsRequest
        ↓
8. BarsRequest Completes → Returns Historical Bars
        ↓
9. LoadPreHydrationBars() → Feeds Bars Directly to Stream Buffers
        ↓
10. OnBarUpdate() Also Called for Each Historical Bar (if NinjaTrader feeds them)
        ↓
11. Streams Buffer Bars During PRE_HYDRATION State
        ↓
12. Tick() → HandlePreHydrationState() → Detects BarsRequest Complete
        ↓
13. Mark Pre-Hydration Complete → Transition to ARMED State
        ↓
14. Range Building Begins with Historical Bars Available
```

### Key Improvements

1. **Continuous Tick() Execution**: `OnMarketData()` ensures `Tick()` runs even without bars
2. **Direct Bar Feeding**: `LoadPreHydrationBars()` feeds bars directly, doesn't depend on `OnBarUpdate()`
3. **Synchronized Tracking**: BarsRequest marked as pending before queuing prevents race conditions
4. **Dual Execution Paths**: Both bar-driven and tick-driven `Tick()` execution ensures robustness

---

## Why Both OnBarUpdate() and LoadPreHydrationBars() Are Needed

### OnBarUpdate() Role:
- **Primary path** for live bars (after hydration)
- **Secondary path** for historical bars (if NinjaTrader feeds them)
- **Always calls `Tick()`** after delivering bars
- **Data ingress only** - NOT part of hydration control

### LoadPreHydrationBars() Role:
- **Primary path** for historical bars from BarsRequest
- **Direct feeding** to stream buffers (bypasses NinjaTrader's bar feed)
- **Ensures bars are available** even if `OnBarUpdate()` isn't called

### Why Both?
- **Redundancy**: If one path fails, the other still works
- **Reliability**: Historical bars are guaranteed to reach streams
- **Flexibility**: Works with different NinjaTrader bar feed behaviors

---

## Critical Invariant: OnBarUpdate() is NOT Part of Hydration Control

**Invariant**: **OnBarUpdate() may deliver hydration bars, but hydration state progression is driven exclusively by Tick() and BarsRequest completion.**

### What This Means:

**OnBarUpdate() is data ingress only**:
- Delivers bars to streams (live or historical)
- Calls `Tick()` after delivering bars
- **Does NOT control hydration state**

**Hydration completion is driven by**:
1. **BarsRequest completion** - Marks when historical bars are loaded
2. **Direct bar injection** - `LoadPreHydrationBars()` feeds bars directly
3. **Tick() evaluation** - `HandlePreHydrationState()` checks if hydration is complete

**Why This Matters**:
- Hydration completion must **never depend on OnBarUpdate()**
- If `OnBarUpdate()` stops being called, hydration can still complete via `LoadPreHydrationBars()`
- If `OnBarUpdate()` delivers bars out of order, hydration state is unaffected
- Hydration state progression is **time-driven and state-driven**, not bar-arrival-driven

**This invariant prevents regressions** where someone might try to make hydration depend on `OnBarUpdate()` being called, which would break if bars stop arriving.

---

## Summary

### How OnBarUpdate() is Used for Hydration:
1. ✅ Historical bars from `BarsRequest` feed through `OnBarUpdate()` (if NinjaTrader feeds them)
2. ✅ `OnBarUpdate()` calls `_engine.OnBar()` to deliver bars to streams
3. ✅ Streams buffer bars during `PRE_HYDRATION` state
4. ✅ `OnBarUpdate()` also calls `Tick()` to process pre-hydration state
5. ✅ **Critical**: `OnBarUpdate()` is **data ingress only** - NOT part of hydration control

### Critical Invariant:
**OnBarUpdate() may deliver hydration bars, but hydration state progression is driven exclusively by Tick() and BarsRequest completion.**

- Hydration completion must **never depend on OnBarUpdate()**
- Hydration state progression is **time-driven and state-driven**, not bar-arrival-driven
- This prevents regressions where hydration might depend on bars arriving

### What Was Wrong Before:
1. ❌ `Tick()` only ran from `OnBarUpdate()` - stopped if bars stopped
2. ❌ Historical bars sometimes didn't feed through `OnBarUpdate()`
3. ❌ Time-based logic stopped executing when bars stopped

### How the Fix Solved It:
1. ✅ `OnMarketData()` calls `Tick()` continuously (tick-driven execution)
2. ✅ `LoadPreHydrationBars()` feeds bars directly (doesn't depend on `OnBarUpdate()`)
3. ✅ BarsRequest marked as pending before queuing (prevents race conditions)
4. ✅ Dual execution paths ensure robustness (bar-driven + tick-driven)
5. ✅ **Hydration control decoupled from OnBarUpdate()** - hydration completion driven by Tick() and BarsRequest completion

**Result**: Hydration works reliably, and time-based logic runs continuously even if bars stop. Hydration state progression is independent of `OnBarUpdate()` being called.

---

**Status**: ✅ **COMPLETE EXPLANATION**
