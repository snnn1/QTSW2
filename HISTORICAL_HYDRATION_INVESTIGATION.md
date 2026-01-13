# Historical Hydration Investigation - SIM Mode

## Problem Summary

When the robot starts **AFTER** the range window has ended (e.g., starts at 14:56 UTC when range window was 14:00-15:00 UTC), historical hydration fails silently and range computation fails with `NO_BARS_IN_WINDOW`.

## Code Flow

### 1. Bar Provider Setup
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` lines 62-65
```csharp
// Set bar provider for historical hydration (late start support)
var barProvider = new NinjaTraderBarProviderWrapper(this, _engine);
_engine.SetBarProvider(barProvider);
```

### 2. Hydration Attempt
**File**: `modules/robot/core/StreamStateMachine.cs` lines 330-333
```csharp
if (!_rangeComputed && !_hydrationAttempted)
{
    _hydrationAttempted = true;
    TryHydrateFromHistory(utcNow);
}
```

### 3. Hydration Check
**File**: `modules/robot/core/StreamStateMachine.cs` line 1461
```csharp
if (_barProvider == null || _rangeComputed)
    return; // ← Exits silently if barProvider is null
```

### 4. Get Bars from Provider
**File**: `modules/robot/core/StreamStateMachine.cs` line 1480
```csharp
var historicalBars = _barProvider.GetBars(Instrument, RangeStartUtc, SlotTimeUtc).ToList();
```

### 5. NinjaTraderBarProvider Implementation
**File**: `RobotCore_For_NinjaTrader/NinjaTraderBarProvider.cs` lines 25-28
```csharp
public IEnumerable<Bar> GetBars(string instrument, DateTimeOffset startUtc, DateTimeOffset endUtc)
{
    if (_strategy.Bars == null || _strategy.Bars.Count == 0)
        yield break; // ← Returns empty if Bars collection is empty
```

### 6. Silent Failure
**File**: `modules/robot/core/StreamStateMachine.cs` lines 1482-1483
```csharp
if (historicalBars.Count == 0)
    return; // ← Fails silently, no error logged
```

## Root Cause

**The Problem**: `NinjaTraderBarProvider.GetBars()` accesses `_strategy.Bars` collection, which only contains bars that NinjaTrader has **already loaded**. 

**Why It Fails**:
1. NinjaTrader's `Bars` collection is populated when:
   - Strategy is enabled and bars arrive via `OnBarUpdate()`
   - Historical data is explicitly requested via `Bars.Request()` or `AddDataSeries()`
   
2. If robot starts **AFTER** the range window:
   - Bars from that period haven't been loaded into `Bars` collection
   - `Bars.Count == 0` or bars don't include the historical period
   - `GetBars()` returns empty collection
   - Hydration fails silently

3. **No Error Logging**: When `historicalBars.Count == 0`, hydration just returns without logging why (line 1482-1483)

## Evidence from Logs

- **Range window**: 08:00-09:00 Chicago (14:00-15:00 UTC)
- **Robot started**: 14:56 UTC (08:56 Chicago) - **AFTER range window ended**
- **Range computation**: Failed with `NO_BARS_IN_WINDOW` (bar_count: 0)
- **Hydration logs**: **NONE** - hydration failed silently

## Solutions

### Option 1: Request Historical Bars in NinjaTrader Strategy
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

Add historical data request when strategy starts:
```csharp
protected override void OnStateChange()
{
    if (State == State.DataLoaded)
    {
        // Request historical bars for today (ensures Bars collection has data)
        // This should be done BEFORE setting barProvider
        // NinjaTrader will load bars into Bars collection
    }
}
```

**Problem**: NinjaTrader's `Bars.Request()` or `AddDataSeries()` may not work for arbitrary time ranges.

### Option 2: Add Logging to Hydration Failure
**File**: `modules/robot/core/StreamStateMachine.cs` line 1482

Change silent failure to logged failure:
```csharp
if (historicalBars.Count == 0)
{
    _log.Write(RobotEvents.Base(..., "RANGE_HYDRATION_NO_BARS", ...,
        new {
            reason = "barProvider returned 0 bars",
            provider_type = _barProvider?.GetType().Name ?? "NULL",
            range_start_utc = RangeStartUtc.ToString("o"),
            range_end_utc = SlotTimeUtc.ToString("o"),
            note = "Historical hydration returned no bars - may need to request historical data in NinjaTrader"
        }));
    return;
}
```

### Option 3: Use NinjaTrader's Historical Data API
**File**: `RobotCore_For_NinjaTrader/NinjaTraderBarProvider.cs`

Instead of accessing `Bars` collection, use NinjaTrader's historical data API:
- `Bars.Request()` - Request bars for a specific period
- `GetBars()` - Access historical bars from data feed

**Problem**: Requires async API usage and may not be available in all contexts.

### Option 4: Accept Limitation and Document
- Document that SIM mode requires strategy to start **BEFORE** range window
- Or ensure NinjaTrader has historical data loaded for the period
- Add warning logs when hydration fails

## Recommended Fix

**Immediate**: Add logging to hydration failure (Option 2) so we can see why hydration is failing.

**Long-term**: Investigate NinjaTrader's historical data API to properly request bars for the range window period.
