# SIM vs DRYRUN Pre-Hydration Differences

## Overview

**DRYRUN mode** uses **file-based pre-hydration** (CSV files only).  
**SIM mode** uses **NinjaTrader BarsRequest** only (no CSV files).

## Key Differences

### 1. **Data Sources**

#### DRYRUN Mode:
- **Primary**: CSV files from `data/raw/{instrument}/1m/{yyyy}/{MM}/{INSTRUMENT}_1m_{yyyy-MM-dd}.csv`
- **Supplemental**: None
- **Result**: File-based only, deterministic

#### SIM Mode:
- **Primary**: NinjaTrader `BarsRequest` historical bars
- **Supplemental**: None
- **Result**: BarsRequest only (no CSV files)

### 2. **Pre-Hydration Flow**

#### DRYRUN Flow:
```
1. PerformPreHydration() loads CSV file
2. Bars added to buffer (BarSource.CSV)
3. _preHydrationComplete = true
4. Immediately transition to ARMED state
5. No waiting for additional bars
```

#### SIM Flow:
```
1. Skip CSV file loading (SIM mode doesn't use CSV)
2. _preHydrationComplete = true (immediately)
3. Wait for NinjaTrader bars via LoadPreHydrationBars()
4. Bars arrive via BarsRequest → marked as BarSource.BARSREQUEST
5. Check if bars exist OR if past range start time
6. Transition to ARMED when bars available or time threshold reached
```

### 3. **Bar Source Tracking**

Both modes track bar sources for deduplication:

```csharp
public enum BarSource
{
    LIVE = 0,        // Live feed bars (highest precedence)
    BARSREQUEST = 1, // NinjaTrader BarsRequest (SIM mode only)
    CSV = 2          // File-based pre-hydration (lowest precedence)
}
```

**Deduplication Precedence**: `LIVE > BARSREQUEST > CSV`

### 4. **Transition Timing**

#### DRYRUN:
- Transitions to ARMED **immediately** after file-based pre-hydration completes
- No waiting for additional data sources
- Deterministic and fast

#### SIM:
- Transitions to ARMED when:
  - **OR** bar count > 0 (has bars from BarsRequest)
  - **OR** current time >= range start time (time threshold)
- Waits for NinjaTrader BarsRequest bars to arrive
- No CSV file dependency

### 5. **Code Path Differences**

#### DRYRUN (StreamStateMachine.cs:597-637):
```csharp
else  // DRYRUN mode
{
    // File-based pre-hydration complete, transition to ARMED
    Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE");
}
```

#### SIM (StreamStateMachine.cs:521-594):
```csharp
if (IsSimMode())
{
    // SIM mode: Skip CSV files, rely solely on BarsRequest
    // Mark pre-hydration complete immediately (no CSV to load)
    _preHydrationComplete = true;
}
else
{
    // DRYRUN mode: Load CSV files
    PerformPreHydration(utcNow);
}

// Later, when checking transition:
if (IsSimMode())
{
    // Check if we have sufficient bars from BarsRequest or if past range start time
    if (barCount > 0 || nowChicago >= RangeStartChicagoTime)
    {
        // Log HYDRATION_SUMMARY with breakdown
        Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE_SIM");
    }
    // Otherwise, wait for more bars from NinjaTrader (buffered in OnBar)
}
```

### 6. **NinjaTrader Integration (SIM Only)**

SIM mode receives bars via `RobotEngine.LoadPreHydrationBars()`:
- Called by NinjaTrader strategy after `BarsRequest` completes
- Bars are filtered (future bars, partial bars removed)
- Bars marked as `BarSource.BARSREQUEST`
- Fed to streams via `stream.OnBar(..., isHistorical: true)`

**Filtering Rules**:
- Reject bars with timestamp > `utcNow` (future bars)
- Reject bars < 1 minute old (partial/in-progress bars)
- Only fully closed bars accepted

### 7. **Logging Differences**

#### DRYRUN Logs:
```
PRE_HYDRATION_START
PRE_HYDRATION_COMPLETE (file-based only)
HYDRATION_SUMMARY
  - historical_bar_count: CSV bars only
  - live_bar_count: 0
  - execution_mode: DRYRUN
```

#### SIM Logs:
```
PRE_HYDRATION_COMPLETE (BarsRequest only)
HYDRATION_SUMMARY
  - historical_bar_count: BarsRequest bars only
  - live_bar_count: 0 (during pre-hydration)
  - execution_mode: SIM
  - note: "SIM mode uses BarsRequest only (no CSV files)"
```

### 8. **Error Handling**

#### Missing CSV Files:
- **DRYRUN**: Logs `PRE_HYDRATION_ZERO_BARS`, completes with zero bars, transitions to ARMED
- **SIM**: Same as DRYRUN, but can still receive NinjaTrader bars later

#### Missing NinjaTrader Data (SIM only):
- If `BarsRequest` fails or returns no bars, SIM has zero bars
- System transitions to ARMED when time threshold reached (past range start)
- Range computation will rely on live bars only (may be incomplete)
- Logs `PRE_HYDRATION_NO_BARS_AFTER_FILTER` if all bars filtered

### 9. **Use Cases**

#### DRYRUN:
- **Testing**: Deterministic replay of historical data
- **Backtesting**: Fast, file-based only
- **Development**: No NinjaTrader dependency
- **CI/CD**: Can run without NinjaTrader running

#### SIM:
- **Live Testing**: Uses actual NinjaTrader data feed
- **Validation**: Verifies BarsRequest integration
- **Production-like**: Closer to LIVE mode behavior
- **Data Source**: NinjaTrader BarsRequest only (no CSV dependency)

### 10. **Performance Characteristics**

#### DRYRUN:
- **Faster**: No network/API calls
- **Deterministic**: Same CSV file = same results
- **Isolated**: No external dependencies

#### SIM:
- **Slower**: Waits for NinjaTrader BarsRequest
- **Variable**: Depends on NinjaTrader data availability
- **Integrated**: Requires NinjaTrader running

## Summary Table

| Aspect | DRYRUN | SIM |
|--------|--------|-----|
| **Primary Source** | CSV files | NinjaTrader BarsRequest |
| **Supplemental Source** | None | None |
| **Transition Timing** | Immediate after CSV load | When bars available OR time threshold |
| **Bar Sources** | CSV only | BARSREQUEST only |
| **Deduplication** | CSV vs LIVE | BARSREQUEST vs LIVE |
| **Deterministic** | Yes | No (depends on NT data) |
| **Speed** | Fast | Slower (waits for NT) |
| **Use Case** | Testing, backtesting | Live testing, validation |
| **Dependencies** | None | NinjaTrader running |

## Code References

- **Pre-hydration logic**: `StreamStateMachine.cs:519-639`
- **File-based loading**: `StreamStateMachine.cs:1702-1915`
- **NinjaTrader bars**: `RobotEngine.cs:591-730`
- **Bar source enum**: `StreamStateMachine.cs:33-40`
