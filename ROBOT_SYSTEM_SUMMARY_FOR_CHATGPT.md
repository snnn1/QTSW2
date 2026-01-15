# QTSW2 Robot Trading System - Complete Summary for ChatGPT

**Generated**: 2026-01-15  
**Purpose**: Comprehensive documentation of the robot trading system architecture, implementation, and current state

---

## Executive Summary

The **QTSW2 Robot** is a C# trading system that runs inside NinjaTrader 8, implementing a breakout trading strategy with strict parity to a Python Analyzer system. It processes live market data, builds trading ranges, detects breakouts, and executes trades with risk management.

**Key Characteristics**:
- **Language**: C# (.NET Framework 4.8)
- **Platform**: NinjaTrader 8 (SIM and LIVE modes)
- **Strategy Type**: Range breakout with immediate entry
- **Execution Modes**: DRYRUN, SIM, LIVE
- **Parity Requirement**: Must match Python Analyzer behavior exactly

---

## System Architecture

### High-Level Components

```
NinjaTrader 8
    ↓
RobotSimStrategy.cs (NinjaTrader Strategy Host)
    ↓
RobotEngine.cs (Core Orchestration)
    ↓
StreamStateMachine.cs (Per-Stream State Management)
    ↓
Execution Adapters (SIM/LIVE)
    ↓
RiskGate, HealthMonitor, Notifications
```

### Core Directories

- **`modules/robot/core/`**: Source C# files (authoritative)
- **`RobotCore_For_NinjaTrader/`**: Synced copy for NinjaTrader compilation
- **`modules/robot/ninjatrader/`**: NinjaTrader-specific integration code
- **`configs/analyzer_robot_parity.json`**: Parity specification (canonical contract)
- **`data/timetable/timetable_current.json`**: Trading schedule configuration

---

## State Machine Architecture

### Stream States

Each trading stream (instrument + session + slot_time) has its own state machine:

```csharp
public enum StreamState
{
    PRE_HYDRATION,    // Loading historical data from CSV
    ARMED,            // Waiting for range start time
    RANGE_BUILDING,   // Actively building range from live bars
    RANGE_LOCKED,     // Range locked, monitoring for breakouts
    DONE              // Slot complete (no re-arming)
}
```

### State Transitions

| From | To | Condition | Example Time |
|------|-----|-----------|--------------|
| PRE_HYDRATION | ARMED | `_preHydrationComplete == true` | Immediately after CSV load |
| ARMED | RANGE_BUILDING | `utcNow >= RangeStartUtc` | 08:00 UTC (Chicago 02:00) |
| RANGE_BUILDING | RANGE_LOCKED | `utcNow >= SlotTimeUtc` | 15:00 UTC (Chicago 09:00) |
| RANGE_BUILDING | DONE | `_rangeInvalidated == true` | Anytime if gaps violate tolerance |
| RANGE_LOCKED | DONE | Market close or trade complete | 22:00 UTC (Chicago 16:00) |

### Complete Flow Example: S1 09:00 Slot

**Timeline (Chicago Time)**:
- **00:00**: Robot starts → `PRE_HYDRATION` (loads CSV)
- **00:05**: Pre-hydration complete → `ARMED` (waits)
- **02:00**: Range start → `RANGE_BUILDING` (collects bars)
- **09:00**: Slot time → `RANGE_LOCKED` (monitors breakouts)
- **16:00**: Market close → `DONE` (if no trade)

---

## Key Components

### 1. RobotEngine.cs

**Purpose**: Central orchestration engine

**Responsibilities**:
- Loads parity spec (`analyzer_robot_parity.json`)
- Manages timetable polling and updates
- Creates/manages `StreamStateMachine` instances
- Handles trading date rollover
- Coordinates bar ingestion (`OnBar()`)
- Calls `Tick()` every second via timer

**Key Methods**:
- `Start()`: Initializes engine, loads spec, starts health monitor
- `Tick(DateTimeOffset utcNow)`: Called every second, processes all streams
- `OnBar(...)`: Receives bars from NinjaTrader, routes to streams
- `ReloadTimetableIfChanged()`: Polls timetable file for changes
- `ApplyTimetable()`: Creates/updates streams based on timetable

**Critical Fields**:
- `_activeTradingDate`: Current trading date (DateOnly)
- `_streams`: Dictionary of StreamStateMachine instances
- `_spec`: ParitySpec configuration
- `_time`: TimeService for timezone conversions

### 2. StreamStateMachine.cs

**Purpose**: Per-stream state management and trading logic

**Responsibilities**:
- Manages state transitions
- Loads historical data (pre-hydration)
- Computes trading ranges
- Detects breakouts
- Manages entry/exit logic
- Tracks gap violations
- Commits journals

**Key Methods**:
- `Tick(DateTimeOffset utcNow)`: State machine logic, transition checks
- `OnBar(...)`: Processes incoming bars, updates range, detects breakouts
- `PerformPreHydration()`: Loads CSV files, filters bars
- `ComputeRangeRetrospectively()`: Calculates range high/low/freeze_close
- `UpdateTradingDate()`: Handles trading day rollover
- `Commit()`: Finalizes journal, marks stream as DONE

**Critical Fields**:
- `State`: Current stream state
- `_barBuffer`: List of bars for range computation
- `RangeHigh`, `RangeLow`, `FreezeClose`: Computed range values
- `_preHydrationComplete`: Flag indicating historical data loaded
- `_rangeInvalidated`: Flag for gap tolerance violations

### 3. ParitySpec (analyzer_robot_parity.json)

**Purpose**: Canonical contract ensuring robot matches analyzer behavior

**Key Sections**:
- **Sessions**: S1 (range_start: 02:00), S2 (range_start: 08:00)
- **Slot Times**: Allowed slot_end_times per session
- **Instruments**: Tick sizes, base targets, micro scaling
- **Breakout Formula**: `brk_long = range_high + tick_size`, `brk_short = range_low - tick_size`
- **Entry Semantics**: Immediate entry at lock if freeze_close breaks out
- **Target/Stop**: Formulas for profit target and stop-loss
- **Break-Even**: 65% trigger, ±1 tick offset

**Governance Rule**: "Analyzer execution change → update this spec → update robot"

### 4. Timetable System

**Purpose**: Defines which streams are active for each trading day

**File**: `data/timetable/timetable_current.json`

**Structure**:
```json
{
  "trading_date": "2026-01-15",
  "timezone": "America/Chicago",
  "streams": [
    {
      "stream": "ES1",
      "instrument": "ES",
      "session": "S1",
      "slot_time": "09:00",
      "enabled": true
    }
  ]
}
```

**Behavior**:
- Robot polls timetable file every 2 seconds
- Creates/updates streams based on enabled directives
- Validates slot_time against allowed values in spec
- Updates streams when timetable changes

### 5. Execution Adapters

**NinjaTraderSimAdapter** (SIM mode):
- Submits orders to NinjaTrader SIM account
- Handles order updates and executions
- Manages protective orders (stop-loss, target, break-even)
- Correlates orders with intents via order.Tag

**DryRunAdapter** (DRYRUN mode):
- Logs all order operations
- No actual order submission
- Used for testing and validation

**LiveAdapter** (LIVE mode - not yet enabled):
- Will submit to live account
- Currently blocked with error

### 6. Risk Management

**RiskGate**:
- Validates order parameters
- Checks position limits
- Enforces risk rules

**KillSwitch**:
- Emergency stop mechanism
- Can disable trading globally

**HealthMonitor**:
- Monitors data feed health
- Detects connection issues
- Sends notifications (Pushover)
- Tracks bar reception and engine ticks

---

## Data Flow

### Bar Ingestion

```
NinjaTrader OnBarUpdate()
    ↓
RobotSimStrategy.OnBarUpdate()
    ↓
RobotEngine.OnBar(barUtc, instrument, open, high, low, close, utcNow)
    ↓
[Check trading date rollover]
    ↓
StreamStateMachine.OnBar(barUtc, open, high, low, close, utcNow)
    ↓
[Add to _barBuffer]
[Update range if RANGE_BUILDING]
[Check breakout if RANGE_LOCKED]
```

### Time Progression

```
NinjaTrader Timer (1 second interval)
    ↓
RobotSimStrategy.TimerCallback()
    ↓
RobotEngine.Tick(DateTimeOffset.UtcNow)
    ↓
[Poll timetable if needed]
    ↓
For each StreamStateMachine:
    stream.Tick(utcNow)
        ↓
[Check state transition conditions]
[Log diagnostics]
[Update internal state]
```

### Pre-Hydration Flow

```
StreamStateMachine created
    ↓
State = PRE_HYDRATION
    ↓
Tick() called → PerformPreHydration()
    ↓
Read CSV: data/raw/{INSTRUMENT}/1m/{yyyy}/{MM}/{INSTRUMENT}_1m_{yyyy-MM-dd}.csv
    ↓
Filter bars: [RangeStartChicagoTime, SlotTimeChicagoTime)
    ↓
Add to _barBuffer
    ↓
_preHydrationComplete = true
    ↓
Transition to ARMED
```

---

## Trading Logic

### Range Computation

**Window**: `[RangeStartChicagoTime, SlotTimeChicagoTime)`

**Sources**:
1. **Pre-hydrated bars**: Historical CSV data
2. **Live bars**: Real-time bars from NinjaTrader

**Computation**:
- `RangeHigh = MAX(bar.high)` for all bars in window
- `RangeLow = MIN(bar.low)` for all bars in window
- `FreezeClose = bar.close` at SlotTimeChicagoTime (or last bar if before slot)

**Timing**:
- Initial computation at `RANGE_BUILDING` transition
- Incremental updates as live bars arrive
- Final computation at `RANGE_LOCKED` transition

### Breakout Levels

```
brk_long = RangeHigh + tick_size
brk_short = RangeLow - tick_size
```

**Tick Rounding**: Must match `UtilityManager.round_to_tick()` from Python analyzer exactly.

### Entry Detection

**Immediate Entry** (at lock time):
- If `freeze_close >= brk_long` → Enter long immediately
- If `freeze_close <= brk_short` → Enter short immediately

**Breakout Entry** (after lock):
- If `bar.high >= brk_long` → Enter long
- If `bar.low <= brk_short` → Enter short

**Market Close Cutoff**: No entry after 16:00 Chicago (22:00 UTC)

### Risk Management

**Target Price**:
- Long: `entry + base_target`
- Short: `entry - base_target`

**Stop-Loss**:
- Formula: `min(range_size, 3 * base_target)`
- Long: `entry - sl_points`
- Short: `entry + sl_points`

**Break-Even**:
- Trigger: 65% of target reached
- Stop adjustment: ±1 tick from entry
- Does not rearm if price retraces

---

## Gap Tolerance Rules

**Purpose**: Ensure data quality before trading

**Rules**:
- **MAX_SINGLE_GAP_MINUTES**: 3.0 minutes
- **MAX_TOTAL_GAP_MINUTES**: 6.0 minutes
- **MAX_GAP_LAST_10_MINUTES**: 2.0 minutes

**Violation Handling**:
- Sets `_rangeInvalidated = true`
- Prevents trading (no entry)
- Commits journal with "RANGE_INVALIDATED"
- Sends notification (if configured)

---

## Journal System

**Purpose**: Persist stream state and prevent duplicate processing

**Location**: `logs/robot/journal/{trading_date}_{stream}.json`

**Structure**:
```json
{
  "TradingDate": "2026-01-15",
  "Stream": "ES1",
  "Committed": false,
  "CommitReason": null,
  "LastState": "RANGE_BUILDING",
  "LastUpdateUtc": "2026-01-15T08:00:00Z",
  "TimetableHashAtCommit": "..."
}
```

**Behavior**:
- Created when stream is initialized
- Updated on every state change
- Committed when slot completes
- Prevents re-processing if committed

---

## Logging System

### Log Files

- **`logs/robot/robot_{instrument}.jsonl`**: Per-instrument event logs
- **`logs/robot/robot_ENGINE.jsonl`**: Engine-level events
- **`logs/robot/journal/`**: Stream journals

### Event Types

**State Transitions**:
- `PRE_HYDRATION_COMPLETE`
- `STREAM_ARMED` / `ARMED`
- `RANGE_WINDOW_STARTED`
- `RANGE_LOCKED` / `RANGE_LOCKED_INCREMENTAL`
- `RANGE_COMPUTE_COMPLETE`

**Trading Events**:
- `RANGE_INTENT_ASSERT`
- `ENTRY_DETECTED`
- `ORDER_SUBMITTED`
- `ORDER_FILLED`
- `TARGET_HIT` / `STOP_HIT`

**Errors**:
- `RANGE_COMPUTE_FAILED`
- `RANGE_INVALIDATED`
- `PRE_HYDRATION_ZERO_BARS`

**Diagnostics** (if enabled):
- `ENGINE_TICK_HEARTBEAT`
- `ENGINE_BAR_HEARTBEAT`
- `ARMED_STATE_DIAGNOSTIC`
- `SLOT_GATE_DIAGNOSTIC`

### Logging Configuration

**File**: `configs/robot/logging_config.json` (if exists)

**Options**:
- `enable_diagnostic_logs`: Enable verbose diagnostics
- `diagnostic_rate_limits`: Rate limits for diagnostic events

---

## Recent Issues & Fixes

### Issue 1: ARMED → RANGE_BUILDING Transition Failure

**Problem**: Streams stuck in ARMED state, never transitioning to RANGE_BUILDING

**Root Cause**: `UpdateTradingDate()` reset `_preHydrationComplete = false` but preserved `ARMED` state, causing early break in ARMED handler

**Fix**: Reset state to `PRE_HYDRATION` on trading day rollover, ensuring pre-hydration re-runs

**Status**: ✅ Fixed

### Issue 2: Rollover Spam

**Problem**: 600+ `TRADING_DAY_ROLLOVER` events on startup, preventing state transitions

**Root Causes**:
1. Initialization treated as rollover (empty journal TradingDate)
2. Backward date progression (replay mode) triggering resets
3. ENGINE-level rollover logging on initialization

**Fixes**:
1. Added `isInitialization` guard - only updates journal/times, no state reset
2. Added `isBackwardDate` guard - preserves state for historical data
3. Suppressed ENGINE-level rollover logging on initialization

**Status**: ✅ Fixed (requires recompilation)

---

## Configuration Files

### analyzer_robot_parity.json

**Location**: `configs/analyzer_robot_parity.json`

**Purpose**: Canonical parity contract

**Key Fields**:
- `sessions`: S1/S2 range start times and slot end times
- `instruments`: Tick sizes, base targets, micro scaling
- `breakout`: Formula and tick rounding rules
- `entry_semantics`: Entry detection rules
- `target` / `stop_loss` / `break_even`: Risk management formulas

### timetable_current.json

**Location**: `data/timetable/timetable_current.json`

**Purpose**: Defines active streams for trading day

**Structure**: Array of stream directives with instrument, session, slot_time, enabled flag

**Polling**: RobotEngine polls every 2 seconds for changes

### health_monitor.json (Optional)

**Location**: `configs/robot/health_monitor.json`

**Purpose**: Health monitoring and notification configuration

**Features**:
- Data feed stall detection
- Connection monitoring
- Pushover notifications

---

## Timezone Handling

**Critical**: All times stored/compared in UTC internally, Chicago times are derived

**TimeService**:
- `ConstructChicagoTime()`: Creates DateTimeOffset in Chicago timezone
- `ConvertUtcToChicago()`: Converts UTC to Chicago
- `GetChicagoDateToday()`: Gets Chicago date from UTC timestamp
- `TryParseDateOnly()`: Parses date strings

**Key Times** (S1 09:00 slot example):
- RangeStartUtc: 08:00 UTC (Chicago 02:00)
- SlotTimeUtc: 15:00 UTC (Chicago 09:00)
- MarketCloseUtc: 22:00 UTC (Chicago 16:00)

---

## Execution Modes

### DRYRUN
- No actual order submission
- All operations logged
- Used for testing and validation

### SIM
- Submits to NinjaTrader SIM account
- Full order lifecycle
- Used for paper trading

### LIVE
- Currently blocked (not enabled)
- Will submit to live account
- Requires explicit enablement

---

## NinjaTrader Integration

### Strategy Host

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Responsibilities**:
- Implements NinjaTrader Strategy interface
- Verifies SIM account
- Initializes RobotEngine
- Wires NinjaTrader context (Account, Instrument)
- Starts tick timer (1 second interval)
- Forwards bars and order/execution events

**Key Methods**:
- `OnStateChange()`: Handles strategy lifecycle
- `OnBarUpdate()`: Receives bars from NinjaTrader
- `TimerCallback()`: Calls `RobotEngine.Tick()` every second
- `OnOrderUpdate()` / `OnExecutionUpdate()`: Forwards to adapter

### Compilation

**Process**:
1. Source files in `modules/robot/core/`
2. Sync to `RobotCore_For_NinjaTrader/` via PowerShell script
3. NinjaTrader compiles from `RobotCore_For_NinjaTrader/`
4. Strategy appears in NinjaTrader strategy list

**Sync Script**: `sync_robotcore_to_ninjatrader.ps1`

---

## Testing & Validation

### Parity Testing

**Purpose**: Ensure robot matches analyzer behavior exactly

**Method**: Compare robot output with analyzer output for same inputs

**Key Areas**:
- Range computation (high/low/freeze_close)
- Breakout level calculation
- Tick rounding
- Entry detection timing
- Target/stop calculation

### Smoke Tests

**Location**: `docs/robot/execution/SIM_SMOKE_TEST_EVIDENCE.md`

**Coverage**:
- State transitions
- Range computation
- Order submission
- Journal persistence

---

## Current Status & Known Issues

### Working
- ✅ State machine transitions
- ✅ Pre-hydration from CSV
- ✅ Range computation
- ✅ Breakout detection
- ✅ Order submission (SIM mode)
- ✅ Journal persistence
- ✅ Health monitoring

### Recent Fixes
- ✅ ARMED → RANGE_BUILDING transition fix
- ✅ Rollover spam prevention (initialization guard)
- ✅ Backward date handling (replay mode)

### Pending
- ⚠️ Rollover spam fix needs recompilation
- ⚠️ Some diagnostic logging disabled by default
- ⚠️ LIVE mode not yet enabled

---

## Key Code Locations

### State Machine Logic
- **File**: `modules/robot/core/StreamStateMachine.cs`
- **Lines**: 
  - PRE_HYDRATION: 361-372
  - ARMED: 374-516
  - RANGE_BUILDING: 518-815
  - RANGE_LOCKED: 817-900+
  - UpdateTradingDate: 222-363

### Engine Orchestration
- **File**: `modules/robot/core/RobotEngine.cs`
- **Lines**:
  - Start(): 154-244
  - Tick(): 340-388
  - OnBar(): 390-500
  - ApplyTimetable(): 600-750

### NinjaTrader Integration
- **File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`
- **Lines**:
  - OnStateChange(): 29-78
  - OnBarUpdate(): 110-146
  - TimerCallback(): 213-228

---

## Development Workflow

1. **Edit Source**: Modify files in `modules/robot/core/`
2. **Sync**: Run `sync_robotcore_to_ninjatrader.ps1`
3. **Compile**: Build in NinjaTrader IDE
4. **Test**: Run strategy in SIM account
5. **Monitor**: Check logs in `logs/robot/`

---

## Important Notes for ChatGPT

1. **Parity is Critical**: Robot must match Analyzer behavior exactly. Changes to trading logic require updating `analyzer_robot_parity.json` first.

2. **State Machine is Central**: All trading logic flows through `StreamStateMachine.Tick()` and `OnBar()` methods.

3. **Time Handling**: Always use UTC internally, convert to Chicago only for display/logging.

4. **Journal System**: Prevents duplicate processing. Committed journals cannot be re-armed.

5. **Gap Tolerance**: Strict rules prevent trading on poor data quality.

6. **Timer-Based Tick**: `Tick()` is called every second by timer, not by bar arrivals. This ensures time-based transitions occur even without bars.

7. **Pre-Hydration**: Historical data loaded from CSV files before live trading begins.

8. **Timetable Reactivity**: Robot polls timetable file and creates/updates streams dynamically.

9. **Execution Adapters**: Abstract order submission, allowing SIM/LIVE/DRYRUN modes.

10. **Health Monitor**: Optional but recommended for production monitoring.

---

## Questions ChatGPT Should Ask

If working on this system, ChatGPT should clarify:
- What execution mode? (DRYRUN/SIM/LIVE)
- What instrument/session/slot_time?
- What trading date?
- What state is the stream in?
- Are there any errors in logs?
- Is the timetable configured correctly?
- Is pre-hydration data available?
- Are bars being received?
- Is Tick() being called?

---

**End of Summary**
