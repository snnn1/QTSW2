# Break-Even Detection — Full Summary

**Last Updated:** 2026-02-13  
**Scope:** RobotCore_For_NinjaTrader — OnMarketData tick-level BE detection

---

## 1. Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ LAYER 1: StreamStateMachine (Engine)                                        │
│ - ComputeProtectivesFromLockSnapshot() / ComputeAndLogProtectiveOrders()      │
│ - BE trigger = entry ± (baseTarget × 0.65)                                  │
│ - BE stop = entry ± 1 tick (breakout level)                                 │
│ - Intent created with BeTrigger, EntryPrice, Direction, ExecutionInstrument  │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ LAYER 2: NinjaTraderSimAdapter                                               │
│ - RegisterIntent(intent) → _intentMap[intentId] = intent                     │
│ - GetActiveIntentsForBEMonitoring(executionInstrument) → filter + journal     │
│ - ModifyStopToBreakEven() → IsBEModified → ModifyStopToBreakEvenReal → Record │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ LAYER 3: RobotSimStrategy (Strategy)                                         │
│ - OnMarketData(MarketDataType.Last) → RunBreakEvenCheck(tickPrice)            │
│ - CheckBreakEvenTriggersTickBased(tickPrice, utcNow)                         │
│ - Price from e.Price (Last trade) — tick-level, no bar starvation            │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Detection Path (OnMarketData)

### 2.1 Entry Conditions

BE detection runs **only** when all of the following are true:

| Condition | Location | Purpose |
|-----------|----------|---------|
| `e.MarketDataType == MarketDataType.Last` | OnMarketData | Use last-trade price |
| `State == State.Realtime` | OnMarketData | Skip historical/Calculating |
| `HasAccountPositionInInstrument()` (Account.Positions) | OnMarketData | Skip when no position; uses account, not strategy Position |
| `!_initFailed && _engineReady && _engine != null` | OnMarketData | Fail-closed |
| `CurrentBars[BarsInProgress] >= 1` | CheckBreakEvenTriggersTickBased | Bars loaded |

### 2.2 Trigger Logic

| Direction | Trigger Condition | BE Stop Placement |
|-----------|-------------------|-------------------|
| **Long** | `Last >= beTriggerPrice` | `breakoutLevel - tickSize` (1 tick below breakout) |
| **Short** | `Last <= beTriggerPrice` | `breakoutLevel + tickSize` (1 tick above breakout) |

- **beTriggerPrice** = from `Intent.BeTrigger` (computed by engine, typically entry ± 65% of base target)
- **breakoutLevel** = `Intent.EntryPrice` (breakout level brkLong/brkShort, not actual fill)
- **tickSize** = `Instrument.MasterInstrument.TickSize` (fallback 0.25m if unavailable)

### 2.3 Price Source

- **Source:** `OnMarketData(MarketDataEventArgs e)` when `e.MarketDataType == MarketDataType.Last`
- **Value:** `(decimal)e.Price` — last executed trade price
- **Latency:** Tick-level; no bar close delay or 1-second series dependency

---

## 3. Throttling & Performance

| Throttle | Value | Purpose |
|----------|-------|---------|
| **BE_SCAN_THROTTLE_MS** | 200 ms | Run intent scan at most ~5/sec; latch price every tick |
| **BE_INTENTS_CACHE_MS** | 200 ms | Cache `GetActiveIntentsForBEMonitoring` result |
| **BE_MODIFY_ATTEMPT_INTERVAL_MS** | 200 ms | Max one modify attempt per intent per 200 ms |
| **BE_PATH_ACTIVE_RATE_LIMIT_SECONDS** | 60 s | BE_PATH_ACTIVE diagnostic log once/min |
| **BE_EVALUATION_RATE_LIMIT_SECONDS** | 1 s | BE_EVALUATION_TICK heartbeat once/sec |

---

## 4. Idempotency & Guards

### 4.1 Idempotency Layers

1. **GetActiveIntentsForBEMonitoring** — Skips intents where `IsBEModified(intentId, tradingDate, stream)` is true
2. **ModifyStopToBreakEven** — Returns early with "BE modification already attempted" if `IsBEModified`
3. **ModifyStopToBreakEvenReal** — "Only tighten" guard: if current stop already at or tighter than BE, records BE and returns success (no NT Change call)

### 4.2 "Only Tighten" Guard (ModifyStopToBreakEvenReal)

```csharp
// Long: current stop at or above BE = already tighter
// Short: current stop at or below BE = already tighter
var stopAlreadyTighter = intentDirection == "Long"
    ? currentStop >= beStopPrice
    : currentStop <= beStopPrice;
if (stopAlreadyTighter) {
    // Record as modified, return success — no Change() call
}
```

Prevents overwriting a stop that trailing logic (or manual adjustment) already moved tighter.

### 4.3 Execution Journal

- **RecordBEModification** — Sets `BEModified = true`, `BEStopPrice`, `BEModifiedAt`
- **IsBEModified** — Checked before any modify attempt and in `GetActiveIntentsForBEMonitoring`

---

## 5. GetActiveIntentsForBEMonitoring

Returns intents that:

1. Match `executionInstrument` (e.g. MES, MGC) — strategy only gets ticks for its chart
2. Have `BeTrigger`, `EntryPrice`, `Direction` set
3. Entry filled per execution journal (`EntryFilled`, `EntryFilledQuantityTotal > 0`)
4. Not yet BE-modified (`!IsBEModified`)

**Critical:** Uses execution journal, not `_orderMap`, because protective orders overwrite entry orders in `_orderMap`.

---

## 6. Log Events

| Event | When | Rate Limit |
|-------|------|------------|
| **REALTIME_STATE_REACHED** | Strategy transitions to Realtime | Once |
| **BE_PATH_ACTIVE** | In position, BE path running | 1/min per instrument |
| **BE_EVALUATION_TICK** | BE scan ran | 1/sec per instrument |
| **BE_TRIGGER_REACHED** | Stop modified to BE successfully | Per intent |
| **BE_TRIGGER_RETRY_NEEDED** | Trigger reached, stop not found (retry) | Per attempt |
| **BE_TRIGGER_FAILED** | Modify failed (non-retryable) | Per attempt |
| **BE_SKIP_STOP_ALREADY_TIGHTER** | Only-tighten guard hit | Per intent |
| **BE_TRIGGER_TIMEOUT_ERROR** | Trigger reached 5+ sec ago, no modify | Per intent |
| **BE_CHECK_SLOW** | CheckBreakEvenTriggersTickBased > 10 ms | When diagnostic_slow_logs=true |
| **CHECK_BE_TRIGGERS_EXCEPTION** | Exception in BE check | Per exception |

---

## 7. File Locations

| Component | File |
|-----------|------|
| OnMarketData, RunBreakEvenCheck, CheckBreakEvenTriggersTickBased | `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` |
| GetActiveIntentsForBEMonitoring | `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` |
| ModifyStopToBreakEven, ModifyStopToBreakEvenReal | `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs`, `NinjaTraderSimAdapter.NT.cs` |
| IsBEModified, RecordBEModification | `RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs` |
| Intent creation (BeTrigger, EntryPrice) | `RobotCore_For_NinjaTrader/StreamStateMachine.cs` |

---

## 8. Strategy Copies (Synced)

- `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs`
- `NT_STRATEGIES/RobotSimStrategy.cs`
- `modules/robot/ninjatrader/RobotSimStrategy.cs`

---

## 8.1 NinjaTrader Deployment (OneDrive)

**Always deploy to OneDrive** — NinjaTrader loads from `MyDocuments`, which resolves to `OneDrive\Documents` on this system.

- **Target:** `%USERPROFILE%\OneDrive\Documents\NinjaTrader 8\bin\Custom\`
- **DLL:** `Robot.Core.dll` → `Custom\`
- **Strategy:** `RobotSimStrategy.cs` → `Custom\Strategies\`
- **Script:** `batch\DEPLOY_ROBOT_TO_NINJATRADER.bat` (build + copy)
- **Note:** Close NinjaTrader before deploying DLL (file lock).

---

## 9. Invariants (Critical)

1. **BE only in Realtime** — `State == State.Realtime`, account position active (`HasAccountPositionInInstrument` via `Account.Positions`)
2. **Idempotent** — `IsBEModified` check, 200 ms modify throttle, "only tighten" guard
3. **Price source** — `Last` (e.Price) for both long and short
4. **Light OnMarketData** — Early returns, throttled scan, rate-limited logging, no per-tick allocations in hot path
