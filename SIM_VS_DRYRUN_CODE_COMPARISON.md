# SIM vs DRYRUN: Code-Level Differences

## Overview

This document outlines the **main differences between SIM and DRYRUN modes** based on actual code analysis.

---

## 1. Execution Adapter

### DRYRUN Mode
**File**: `modules/robot/core/Execution/ExecutionAdapterFactory.cs:20-28`
```csharp
case ExecutionMode.DRYRUN:
    adapter = new NullExecutionAdapter(log);
    // Logs all execution attempts but does NOT place orders
```

**File**: `modules/robot/core/Execution/NullExecutionAdapter.cs`
- **All methods** (`SubmitEntryOrder`, `SubmitProtectiveStop`, `SubmitTargetOrder`, etc.) **log only**
- Returns success results but **no actual orders placed**
- Events logged: `ENTRY_ORDER_DRYRUN`, `PROTECTIVE_STOP_DRYRUN`, `TARGET_ORDER_DRYRUN`, etc.

### SIM Mode
**File**: `modules/robot/core/Execution/ExecutionAdapterFactory.cs:30-38`
```csharp
case ExecutionMode.SIM:
    adapter = new NinjaTraderSimAdapter(projectRoot, log, executionJournal);
    // Places orders in NinjaTrader Sim account
```

**File**: `modules/robot/ninjatrader/Execution/NinjaTraderSimAdapter.cs`
- **Places actual orders** in NinjaTrader Sim account
- Uses NinjaTrader API (`Account.CreateOrder`, `Account.Submit`, etc.)
- Orders are **real** but in simulated account (no real money)

**Key Difference**: DRYRUN = log only, SIM = real orders in Sim account

---

## 2. Pre-Hydration (Historical Bar Loading)

### DRYRUN Mode
**File**: `modules/robot/core/StreamStateMachine.cs:534-538`
```csharp
else
{
    // DRYRUN mode: Perform file-based pre-hydration
    PerformPreHydration(utcNow);
}
```

**Data Source**: 
- **CSV files** from `data/raw/{instrument}/1m/{YYYY}/{MM}/`
- **Parquet files** from `data/translated/{instrument}/1m/{YYYY}/{MM}/` (when using `--replay`)
- Loaded via `PerformPreHydration()` method
- Uses `SnapshotParquetBarProvider` for parquet files

**Flow**:
1. Load bars from CSV/parquet files
2. Filter future/partial bars
3. Feed to stream buffers
4. Mark `_preHydrationComplete = true`

### SIM Mode
**File**: `modules/robot/core/StreamStateMachine.cs:527-533`
```csharp
if (IsSimMode())
{
    // SIM mode: Skip CSV files, rely solely on BarsRequest
    // BarsRequest bars arrive via LoadPreHydrationBars() and are buffered in OnBar()
    // Mark pre-hydration as complete so we can transition when bars arrive
    _preHydrationComplete = true;
}
```

**Data Source**:
- **NinjaTrader BarsRequest API** only
- **No CSV files** (explicitly skipped)
- **No parquet files** (explicitly skipped)
- Bars requested synchronously at strategy startup

**Flow**:
1. Strategy calls `RequestHistoricalBarsForPreHydration()` (`RobotSimStrategy.cs:85`)
2. BarsRequest queries NinjaTrader historical database
3. Bars filtered (future/partial) and fed via `LoadPreHydrationBars()`
4. Bars buffered in stream's `_barBuffer`
5. `_preHydrationComplete = true` set immediately (doesn't wait for CSV loading)

**Key Difference**: DRYRUN = file-based (CSV/parquet), SIM = NinjaTrader BarsRequest only

---

## 3. Bar Data Sources

### DRYRUN Mode
**Files**: `modules/robot/harness/HistoricalReplay.cs`, `modules/robot/harness/SnapshotParquetBarProvider.cs`

**Sources**:
- **CSV files**: `data/raw/{instrument}/1m/{YYYY}/{MM}/{INSTRUMENT}_1m_{YYYY-MM-DD}.csv`
- **Parquet files**: `data/translated/{instrument}/1m/{YYYY}/{MM}/{INSTRUMENT}_1m_{YYYY-MM-DD}.parquet`
- **Replay mode**: Uses `HistoricalReplay.Replay()` to feed bars chronologically

**Bar Flow**:
```
Parquet/CSV → SnapshotParquetBarProvider → HistoricalReplay → engine.OnBar() → Stream.OnBar()
```

### SIM Mode
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs:83-85`

**Sources**:
- **NinjaTrader BarsRequest API** only
- Queries NinjaTrader's historical database
- Synchronous call: `barsRequest.Request()`

**Bar Flow**:
```
NinjaTrader DB → BarsRequest.Request() → LoadPreHydrationBars() → Stream.OnBar()
```

**Live Bars**:
- `OnBarUpdate()` → `engine.OnBar()` → `Stream.OnBar()`
- `TickTimerCallback()` → `engine.Tick()`

**Key Difference**: DRYRUN = file-based replay, SIM = NinjaTrader API + live feed

---

## 4. Host Environment

### DRYRUN Mode
**File**: `modules/robot/harness/Program.cs`

**Host**: Standalone .NET console application (harness)
- No NinjaTrader dependency
- Runs independently
- Uses `HistoricalReplay` for bar replay
- Can run multiple days via `--replay --start YYYY-MM-DD --end YYYY-MM-DD`

**Execution Context**:
- No broker connection
- No account
- No live market data feed
- Pure historical replay

### SIM Mode
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Host**: NinjaTrader Strategy (runs inside NinjaTrader)
- **Requires NinjaTrader 8**
- Runs as a Strategy in NinjaTrader
- Must be attached to a chart
- Requires SIM account (verified at startup)

**Execution Context**:
- NinjaTrader account (SIM)
- Live market data feed
- Real-time bar updates
- Order execution via NinjaTrader API

**Key Difference**: DRYRUN = standalone harness, SIM = NinjaTrader Strategy

---

## 5. Order Execution Flow

### DRYRUN Mode
**File**: `modules/robot/core/StreamStateMachine.cs:2348-2400` (`RecordIntendedEntry`)

**Flow**:
```csharp
RecordIntendedEntry(...)
  → if (DRYRUN) {
      Log entry intent
      Return (no execution)
    }
  → if (SIM/LIVE) {
      Check idempotency (ExecutionJournal)
      Evaluate RiskGate
      Submit via adapter
    }
```

**Result**: 
- Entry intent logged as `ENTRY_INTENT_DETECTED`
- **No orders placed**
- **No risk checks** (skipped)
- **No execution journal** entries

### SIM Mode
**File**: `modules/robot/core/StreamStateMachine.cs:2348-2400` (`RecordIntendedEntry`)

**Flow**:
```csharp
RecordIntendedEntry(...)
  → Check idempotency (ExecutionJournal)
  → Evaluate RiskGate (all gates must pass)
  → Submit entry order via NinjaTraderSimAdapter
  → Record submission in ExecutionJournal
```

**Result**:
- Entry order **placed** in NinjaTrader Sim account
- RiskGate evaluated (position limits, daily loss limits, etc.)
- ExecutionJournal tracks orders
- Fill callbacks update state

**Key Difference**: DRYRUN = log only, SIM = real orders with risk checks

---

## 6. State Machine Behavior

### DRYRUN Mode
**File**: `modules/robot/core/StreamStateMachine.cs:521-539`

**Pre-Hydration**:
- Loads bars from CSV/parquet files
- Waits for file loading to complete
- Transitions to ARMED when bars loaded

**Transition Logic**:
```csharp
if (_preHydrationComplete && GetBarBufferCount() > 0)
    Transition to ARMED
```

### SIM Mode
**File**: `modules/robot/core/StreamStateMachine.cs:544-552`

**Pre-Hydration**:
- Skips CSV loading
- Waits for BarsRequest bars
- Transitions to ARMED when bars arrive OR past range start time

**Transition Logic**:
```csharp
if (IsSimMode()) {
    if (barCount > 0 || nowChicago >= RangeStartChicagoTime)
        Transition to ARMED
}
```

**Key Difference**: DRYRUN waits for file loading, SIM waits for BarsRequest or time threshold

---

## 7. Logging Differences

### DRYRUN Mode
**Events**:
- `ENTRY_ORDER_DRYRUN` - Entry intent logged
- `PROTECTIVE_STOP_DRYRUN` - Stop intent logged
- `TARGET_ORDER_DRYRUN` - Target intent logged
- `PRE_HYDRATION_COMPLETE` - CSV/parquet loading complete
- `HYDRATION_SUMMARY` - File-based hydration stats

### SIM Mode
**Events**:
- `ENTRY_ORDER_SUBMITTED` - Order actually submitted
- `PROTECTIVE_STOP_SUBMITTED` - Stop order submitted
- `TARGET_ORDER_SUBMITTED` - Target order submitted
- `HYDRATION_SUMMARY` - BarsRequest hydration stats
- `ORDER_FILLED` - Order fill callbacks
- `RISK_GATE_FAILED` - Risk checks logged

**Key Difference**: DRYRUN logs intents, SIM logs actual order submissions and fills

---

## 8. Risk Management

### DRYRUN Mode
**File**: `modules/robot/core/StreamStateMachine.cs:2348`

**Risk Checks**: **SKIPPED**
```csharp
if (_executionMode == ExecutionMode.DRYRUN) {
    // Log and return - no risk checks
    return;
}
```

### SIM Mode
**File**: `modules/robot/core/Execution/RiskGate.cs`

**Risk Checks**: **ENFORCED**
- Position limits
- Daily loss limits
- Max positions per instrument
- All gates must pass before order submission

**Key Difference**: DRYRUN = no risk checks, SIM = full risk gate evaluation

---

## 9. Execution Journal

### DRYRUN Mode
**File**: `modules/robot/core/StreamStateMachine.cs:2348`

**Journal**: **NOT USED**
- No idempotency checks
- No order tracking
- No fill tracking

### SIM Mode
**File**: `modules/robot/core/Execution/ExecutionJournal.cs`

**Journal**: **ACTIVE**
- Tracks all order submissions
- Prevents duplicate orders (idempotency)
- Records fills and rejections
- Persists to disk for restart recovery

**Key Difference**: DRYRUN = no journal, SIM = full journal tracking

---

## Summary Table

| Aspect | DRYRUN | SIM |
|--------|--------|-----|
| **Execution Adapter** | `NullExecutionAdapter` (log only) | `NinjaTraderSimAdapter` (real orders) |
| **Pre-Hydration Source** | CSV/Parquet files | NinjaTrader BarsRequest only |
| **Bar Data** | File-based replay | NinjaTrader API + live feed |
| **Host** | Standalone harness | NinjaTrader Strategy |
| **Order Execution** | Logged only | Real orders in Sim account |
| **Risk Checks** | Skipped | Enforced (RiskGate) |
| **Execution Journal** | Not used | Active (idempotency + tracking) |
| **State Transitions** | Wait for file loading | Wait for BarsRequest or time |
| **Logging** | Intent logs | Order submission + fill logs |

---

## Code References

### Key Files
- **Execution Mode**: `modules/robot/core/ExecutionMode.cs`
- **Adapter Factory**: `modules/robot/core/Execution/ExecutionAdapterFactory.cs`
- **Null Adapter**: `modules/robot/core/Execution/NullExecutionAdapter.cs`
- **SIM Strategy**: `modules/robot/ninjatrader/RobotSimStrategy.cs`
- **Pre-Hydration**: `modules/robot/core/StreamStateMachine.cs:520-570`
- **Execution Flow**: `modules/robot/core/StreamStateMachine.cs:2348-2400`
- **DRYRUN Harness**: `modules/robot/harness/Program.cs`
- **Historical Replay**: `modules/robot/harness/HistoricalReplay.cs`

---

## Conclusion

**DRYRUN** is a **pure testing/validation mode** with no real execution, while **SIM** is a **live trading mode** (in simulated account) with full execution infrastructure, risk management, and order tracking.
