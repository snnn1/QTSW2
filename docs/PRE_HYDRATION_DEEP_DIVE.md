# Pre-Hydration Deep Dive

## Executive Summary

Pre-hydration loads historical bars (via NinjaTrader BarsRequest) into streams before range lock to compute breakout levels. The pipeline is **architecturally sound** and degrades gracefully (0 bars → live bars only). Recent logs show **BarsRequest consistently returns 0 bars** when the strategy runs outside market hours or when the requested window is in the future.

---

## 1. What Pre-Hydration Does

| Purpose | Detail |
|---------|--------|
| **Goal** | Populate stream bar buffers with historical bars from `range_start` to `slot_time` before range lock |
| **Source** | NinjaTrader `BarsRequest` API → `LoadPreHydrationBars()` → stream `OnBar()` |
| **Benefit** | Range (high/low) computed from historical bars; faster range lock and breakout detection |
| **Fallback** | If 0 bars: stream transitions with `bar_count=0`, range built from live bars only |

---

## 2. End-to-End Flow

```
State.DataLoaded
    │
    ├─► engine.Start() → streams created, trading date locked
    │
    ├─► GetAllExecutionInstrumentsForBarsRequest() → [MES, MCL, MNQ, ...]
    │
    ├─► MarkBarsRequestPending(instrument) [SYNC, before queue]
    │
    └─► ThreadPool.QueueUserWorkItem(RequestHistoricalBarsForPreHydration)
            │
            ├─► GetBarsRequestTimeRange(instrument) → (range_start, slot_time)
            ├─► NinjaTraderBarRequest.RequestBarsForTradingDate(...)
            │       └─► BarsRequest.Request(callback) [async, blocks until callback]
            │
            ├─► bars.Count == 0: Log BARSREQUEST_EXECUTED(0), return [no LoadPreHydrationBars]
            │
            ├─► bars.Count > 0:
            │       └─► engine.LoadPreHydrationBars(instrument, bars, utcNow)
            │               ├─► Filter future/partial bars
            │               ├─► MarkBarsRequestCompleted(instrument)
            │               └─► stream.OnBar(..., isHistorical: true) for each bar
            │
            └─► finally: MarkBarsRequestCompleted(instrument) [always, including timeout/exception]
```

---

## 3. Key Components

### 3.1 RobotSimStrategy (DataLoaded)

- **Location**: `Strategies/RobotSimStrategy.cs` ~lines 411–546
- **Behavior**:
  - Marks all execution instruments as pending **before** queuing
  - Fires BarsRequest in background (ThreadPool) so DataLoaded does not block
  - On failure: logs, marks completed, continues

### 3.2 RequestHistoricalBarsForPreHydration

- **Location**: `Strategies/RobotSimStrategy.cs` ~lines 771–1145
- **Logic**:
  - Uses `GetBarsRequestTimeRange()` for earliest `range_start` and latest `slot_time`
  - Requests bars from `range_start` to `min(slot_time, now)` to avoid future bars
  - Skips if: trading date not locked, invalid time range, before `range_start_time`
  - **Restart**: If restart after slot_time, requests up to current time

### 3.3 RobotEngine.LoadPreHydrationBars

- **Location**: `RobotEngine.cs` ~lines 1954–2200
- **Behavior**:
  - Filters out future bars (`bar.TimestampUtc > utcNow`)
  - Filters out recent bars (< 0.1 min old)
  - Marks BarsRequest completed **before** feeding bars
  - Feeds bars only to streams in PRE_HYDRATION, ARMED, or RANGE_BUILDING
  - Matches streams via `IsSameInstrument()` (canonical mapping, e.g. MES→ES)

### 3.4 StreamStateMachine.HandlePreHydrationState

- **Location**: `StreamStateMachine.cs` ~lines 1128–1710
- **Behavior**:
  - **SIM**: Waits for `IsBarsRequestPending(CanonicalInstrument|ExecutionInstrument)` to be false
  - When not pending: sets `_preHydrationComplete = true`
  - Transition to ARMED when: `barCount > 0` OR `nowChicago >= RangeStartChicagoTime` OR hard timeout (range_start + 1 min)
  - **Hard timeout**: Guarantees exit from PRE_HYDRATION even with 0 bars

### 3.5 BarsRequest Pending/Completed

- **Location**: `RobotEngine.cs` ~lines 207–290
- **MarkBarsRequestPending**: `_barsRequestPending[canonicalInstrument] = utcNow`
- **MarkBarsRequestCompleted**: removes from pending, adds to completed
- **IsBarsRequestPending**: true if in pending and not timed out (default 5 min)

---

## 4. Canonical / Execution Instrument Mapping

| Stream | CanonicalInstrument | ExecutionInstrument | BarsRequest Instrument |
|--------|---------------------|---------------------|-------------------------|
| ES1 | ES | MES | MES |
| CL1 | CL | MCL | MCL |
| NQ1 | NQ | MNQ | MNQ |
| YM1 | YM | MYM | MYM |
| etc. | | | |

- `GetAllExecutionInstrumentsForBarsRequest()` returns execution instruments (e.g. MES, MCL).
- `MarkBarsRequestPending(instrument)` uses canonical form internally.
- Streams use `IsSameInstrument(instrument)` to match BarsRequest instrument to their Canonical/Execution pair.

---

## 5. Observed Behavior (From Logs)

### 5.1 BARSREQUEST_EXECUTED

- All recent events show `bars_returned: 0`.
- Example: `current_time_chicago: "19:11"`, `range_start_time: "08:00"`, `slot_time: "11:00"` (trading_date 2026-02-13).
- Interpretation: Strategy runs at ~7:11 PM Chicago; requested window 08:00–11:00 for 2026-02-13 may be in the future (e.g. next day) or past (previous day) depending on date logic. Either way, NinjaTrader returns 0 bars in this scenario.

### 5.2 PRE_HYDRATION → ARMED with bar_count=0

- Logs show `bar_count: "0"` during transitions.
- Streams still transition to ARMED and proceed with live bars only.

### 5.3 No LOADPREHYDRATIONBARS_ENTERED / PRE_HYDRATION_BARS_LOADED

- These events only fire when `bars.Count > 0`.
- With 0 bars, the strategy never calls `LoadPreHydrationBars`, so these events do not appear.

---

## 6. Potential Issues / Edge Cases

| Issue | Severity | Notes |
|-------|----------|-------|
| **0 bars when strategy runs outside market hours** | Expected | BarsRequest returns 0 for future or unavailable windows; fallback to live bars |
| **Race: stream checks pending before mark** | Mitigated | Mark pending in sync before queueing |
| **Stream state mismatch** | Low | Bars skipped if stream not in PRE_HYDRATION/ARMED/RANGE_BUILDING; logged |
| **RangeStartChicagoTime invalid** | Low | Hard timeout skipped; PRE_HYDRATION_TIMEOUT_SKIPPED logged |
| **Multi-chart: multiple engines** | Known | Each chart has its own engine; BarsRequest per chart |

---

## 7. Verification Checklist

To confirm pre-hydration when it should return bars:

1. **Start during market hours** (e.g. 8:30–10:00 AM Chicago) so the requested window is in the past.
2. **Check NinjaTrader**:
   - "Days to load" sufficient for the requested date
   - Historical data available
   - Trading hours template correct
3. **Search logs**:
   ```powershell
   Select-String -Path "logs\robot\robot_ENGINE.jsonl" -Pattern "BARSREQUEST_EXECUTED"
   Select-String -Path "logs\robot\robot_ENGINE.jsonl" -Pattern "LOADPREHYDRATIONBARS_ENTERED|PRE_HYDRATION_BARS_LOADED"
   ```
4. **Success indicators**:
   - `bars_returned > 0` in BARSREQUEST_EXECUTED
   - LOADPREHYDRATIONBARS_ENTERED with `streams_matched_count > 0`
   - PRE_HYDRATION_BARS_LOADED with `streams_fed > 0`
   - HYDRATION_SUMMARY with `historical_bar_count > 0`

---

## 8. Event Types (RobotEventTypes)

| Event | Level | When |
|-------|-------|------|
| BARSREQUEST_INIT | INFO | BarsRequest initiation |
| BARSREQUEST_PENDING_MARKED | INFO | MarkBarsRequestPending |
| BARSREQUEST_COMPLETED_MARKED | INFO | MarkBarsRequestCompleted |
| BARSREQUEST_EXECUTED | INFO | BarsRequest returned (0 or N bars) |
| LOADPREHYDRATIONBARS_ENTERED | INFO | LoadPreHydrationBars entered |
| PRE_HYDRATION_BARS_LOADED | INFO | Bars fed to streams |
| PRE_HYDRATION_BARS_SKIPPED | DEBUG | Bars skipped (e.g. stream state) |
| PRE_HYDRATION_NO_BARS_AFTER_FILTER | WARN | All bars filtered out |
| PRE_HYDRATION_WAITING_FOR_BARSREQUEST | DEBUG | Stream waiting for BarsRequest |
| PRE_HYDRATION_COMPLETE_SET | DEBUG | _preHydrationComplete set |
| PRE_HYDRATION_FORCED_TRANSITION | INFO | Hard timeout forced ARMED |
| PRE_HYDRATION_TIMEOUT_NO_BARS | INFO | Transition with 0 bars |

---

## 9. Operator Decision Tree

Use this when pre-hydration appears broken. One page, three branches:

| Log pattern | Diagnosis | Action |
|-------------|-----------|--------|
| **BARSREQUEST_EXECUTED** `bars_returned: 0` | Likely outside market hours or requested window is in the future. | **Expected.** Run during market hours when the requested range is in the past. |
| **bars_returned > 0** but **PRE_HYDRATION_NO_BARS_AFTER_FILTER** | Timestamp / timezone / filtering bug. All bars rejected (future or &lt;0.1 min old). | Check `accepted_first_bar_utc`, `accepted_last_bar_utc` vs `current_time_utc` in BARSREQUEST_FILTER_SUMMARY. Fix timezone or bar-age logic. |
| **bars_returned > 0** and **PRE_HYDRATION_BARS_LOADED** but streams still at `bar_count=0` | Mapping/matching bug. Bars not reaching streams. | Check `IsSameInstrument`, stream state filter (PRE_HYDRATION/ARMED/RANGE_BUILDING), `streams_fed` in PRE_HYDRATION_BARS_LOADED. Fix canonical/execution instrument mapping. |
| **bars_returned > 0** but neither **LOADPREHYDRATIONBARS_ENTERED** nor **PRE_HYDRATION_BARS_LOADED** appears | Silent drop. LoadPreHydrationBars not invoked; early return or exception swallowed. | Investigate early returns or exception handling in `RequestHistoricalBarsForPreHydration`. Check for `MarkBarsRequestCompleted` called in `finally` before bars reach engine. |

---

## 10. Risk Surface Verification

| Fix | Status | Location |
|-----|--------|----------|
| **Race condition (pending before queue)** | Done | `RobotSimStrategy.cs` ~436: `MarkBarsRequestPending(instrument)` called **before** `ThreadPool.QueueUserWorkItem`. Comment: "Mark as pending immediately (synchronously) before queuing BarsRequest". |
| **5-minute stall on timeout (finally completion)** | Done | `RobotSimStrategy.cs` ~1070: `finally { MarkBarsRequestCompleted(instrumentName) }` ensures completion on ANY exit (timeout, exception, partial failure). Also in background-error handler ~541. |
| **Ambiguous state visibility (snapshots + decision tree)** | Done | `TryEmitEventDrivenSnapshot` on `BARSREQUEST_COMPLETE`; `STREAM_STATUS_SUMMARY` every 5 min. Operator Decision Tree in §9. |
| **Confusion about 0 bars** | Done | Doc §5, §9 explain 0 bars is expected outside market hours. Code: `bars.Count == 0` path logs `BARSREQUEST_EXECUTED` with `possible_causes`, does not call `LoadPreHydrationBars`. |
| **Canonical mapping uncertainty** | Done | `IsSameInstrument()`, `GetCanonicalInstrument()` throughout. Doc §4 table. `LoadPreHydrationBars` feeds via `stream.IsSameInstrument(instrument)`. |
| **DataLoaded blocking risk** | Done | `ThreadPool.QueueUserWorkItem` (~519) — BarsRequest fire-and-forget. Comment: "HARDENING FIX 2: Make BarsRequest fire-and-forget to prevent blocking DataLoaded". |

---

## 11. Conclusion

Pre-hydration is correctly implemented and degrades safely when BarsRequest returns 0 bars. To see bars actually loaded:

- Run during market hours when the requested window is in the past.
- Ensure NinjaTrader has historical data for the requested date and instrument.
