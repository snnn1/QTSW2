# Robot Modules Summary
## Architecture, Complexity, and Responsibilities

**Date**: Post Hybrid Range Initialization Changes  
**Status**: Production-ready with defensive safety systems

---

## Executive Summary

The robot is a **range breakout trading system** with **6 core modules** and **15+ supporting systems**. It implements an ultra-simple strategy (compute range → place orders → cancel opposite side) wrapped in extensive safety, monitoring, and defensive logic.

**Complexity Level**: **Medium-High** (due to safety systems, not core strategy)

---

## Core Modules

### 1. **RobotEngine** (`RobotEngine.cs`)
**Complexity**: ⭐⭐⭐⭐ (High)  
**Lines of Code**: ~884 lines  
**Purpose**: Central orchestrator and lifecycle manager

**Responsibilities**:
- Manages all stream state machines
- Polls timetable for updates
- Handles trading date rollover
- Coordinates execution adapters, risk gates, health monitoring
- Routes bars to appropriate streams
- Manages engine-level heartbeats and diagnostics

**Key Features**:
- File polling for timetable changes
- Stream creation/destruction based on timetable
- Trading date derivation from bar timestamps
- Stand-down capability for failed streams
- Account/environment info tracking

**Dependencies**: StreamStateMachine, ExecutionAdapter, RiskGate, HealthMonitor, JournalStore

**Complexity Drivers**:
- Multi-stream coordination
- Timetable change detection
- Trading date rollover logic
- Engine-level diagnostics

---

### 2. **StreamStateMachine** (`StreamStateMachine.cs`)
**Complexity**: ⭐⭐⭐⭐⭐ (Very High)  
**Lines of Code**: ~1,600 lines  
**Purpose**: Manages individual trading stream lifecycle and range computation

**Responsibilities**:
- State machine: IDLE → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE
- Range computation (high/low) from bars
- Breakout level calculation
- Entry detection and intent creation
- Protective order intent creation
- Journal persistence (daily state)

**Key Features**:
- **Hybrid Range Initialization**: Computes range immediately from history when enabled mid-range
- Historical bar hydration (from `IBarProvider`)
- Live bar buffering and range updates
- Range locking at slot_time
- Breakout detection (immediate entry or breakout)
- Freeze close tracking (last bar close before slot_time)

**State Transitions**:
```
IDLE → ARMED (when timetable loaded)
ARMED → RANGE_BUILDING (when range_start_time reached)
RANGE_BUILDING → RANGE_LOCKED (when slot_time reached)
RANGE_LOCKED → DONE (when entry detected or market close)
```

**Complexity Drivers**:
- Timezone conversion (UTC ↔ Chicago time)
- Bar filtering by Chicago time window
- Range computation from mixed sources (history + live)
- State machine with recovery states
- Journal persistence

**Recent Changes**:
- ✅ Removed late-start blocking
- ✅ Removed NO_DATA blocking
- ✅ Enabled hybrid initialization
- ✅ Removed execution mode differences

---

### 3. **Execution Layer** (`Execution/` directory)
**Complexity**: ⭐⭐⭐⭐ (High)  
**Files**: 14 files  
**Purpose**: Broker-agnostic order submission and execution tracking

#### **3a. Execution Adapters** (3 implementations)
- **NullExecutionAdapter**: DRYRUN mode - logs intents, no real orders
- **NinjaTraderSimAdapter**: SIM mode - places orders in NinjaTrader simulation
- **NinjaTraderLiveAdapter**: LIVE mode - places orders in live brokerage (stub)

**Complexity**: ⭐⭐⭐ (Medium)  
**Key Features**:
- Retry logic for protective orders (3 attempts)
- Stand-down on protective order failure
- Position flattening on failure
- Incident persistence
- High-priority alerts

#### **3b. RiskGate** (`RiskGate.cs`)
**Complexity**: ⭐⭐ (Low-Medium)  
**Purpose**: Fail-closed safety checks before ANY order submission

**Gates** (6 remaining after Gate 7 removal):
1. Kill switch not enabled
2. Timetable validated
3. Stream armed
4. Session/slot time valid
5. Intent completeness = COMPLETE
6. Trading date set

**Complexity**: Simple boolean checks, but blocks trading if any fail

#### **3c. ExecutionJournal** (`ExecutionJournal.cs`)
**Complexity**: ⭐⭐⭐ (Medium)  
**Purpose**: Idempotency and audit trail

**Features**:
- Intent ID computation (hash of 15 canonical fields)
- Per-intent state tracking (SUBMITTED, FILLED, REJECTED)
- Prevents double-submission
- Persistent storage per `(trading_date, stream, intent_id)`

#### **3d. KillSwitch** (`KillSwitch.cs`)
**Complexity**: ⭐ (Low)  
**Purpose**: Emergency stop mechanism

**Features**:
- File-based kill switch (`configs/robot/kill_switch.json`)
- Global kill switch blocks all trading
- Used by RiskGate

---

### 4. **HealthMonitor** (`HealthMonitor.cs`)
**Complexity**: ⭐⭐⭐ (Medium)  
**Purpose**: Operational monitoring and alerting

**Responsibilities**:
- Engine tick heartbeat monitoring
- Timetable poll heartbeat monitoring
- Data stall detection (missing bars within trading sessions)
- Pushover notification integration
- Session-aware monitoring

**Key Features**:
- Detects if engine stops ticking (stuck process)
- Detects if timetable polling stops
- Detects missing data within defined trading sessions
- High-priority alerts for critical incidents

**Complexity**: Medium - time-based monitoring with configurable thresholds

---

### 5. **Logging System** (`RobotLogger.cs`, `RobotLoggingService.cs`)
**Complexity**: ⭐⭐⭐ (Medium)  
**Purpose**: Structured logging with rotation and filtering

**Components**:
- **RobotLogger**: Synchronous logging (backward compatibility)
- **RobotLoggingService**: Async logging service (singleton, prevents file locks)
- **LoggingConfig**: Configuration for log rotation, filtering, diagnostics

**Features**:
- Log rotation (max file size, max rotated files)
- Log level filtering (min_log_level)
- Diagnostic log control (enable_diagnostic_logs)
- Rate limiting for diagnostic logs
- Log archiving (old logs moved to archive/)

**Complexity**: Medium - file I/O, rotation logic, async batching

---

### 6. **Time Service** (`TimeService.cs`)
**Complexity**: ⭐⭐ (Low-Medium)  
**Purpose**: Timezone conversion and date handling

**Responsibilities**:
- UTC ↔ Chicago time conversion
- Trading date derivation from timestamps
- Date rollover detection
- Timezone-aware date arithmetic

**Key Methods**:
- `ConvertUtcToChicago(DateTimeOffset utc)` - Converts UTC to Chicago time
- `GetChicagoDateToday(DateTimeOffset utc)` - Gets trading date from UTC timestamp
- Handles DST transitions

**Complexity**: Low-Medium - timezone math, but critical for correctness

---

## Supporting Systems

### 7. **JournalStore** (`JournalStore.cs`)
**Complexity**: ⭐⭐ (Low-Medium)  
**Purpose**: Persists stream state per trading day

**Features**:
- Daily journal files (`data/execution_journals/{trading_date}/{stream}.json`)
- Stream state persistence (committed, reason, last state)
- Resume capability on restart
- .NET Framework 4.8 compatibility (File.Move workaround)

---

### 8. **FilePoller** (`FilePoller.cs`)
**Complexity**: ⭐ (Low)  
**Purpose**: Polls timetable file for changes

**Features**:
- Configurable poll interval
- Hash-based change detection
- Triggers timetable reload on change

---

### 9. **Bar Provider Interface** (`IBarProvider.cs`, `SnapshotParquetBarProvider.cs`)
**Complexity**: ⭐⭐ (Low-Medium)  
**Purpose**: Historical bar access for hybrid initialization

**Features**:
- Query bars by instrument and time range
- Used for mid-range initialization
- Optional (null in SIM/LIVE if not provided)

---

### 10. **Notification System** (`Notifications/`)
**Complexity**: ⭐⭐ (Low-Medium)  
**Purpose**: High-priority alerts via Pushover

**Components**:
- **NotificationService**: Queue-based notification service
- **PushoverClient**: HTTP client for Pushover API

**Features**:
- Priority levels (normal, high, emergency)
- Rate limiting
- Used for protective order failures, missing data incidents

---

### 11. **Models** (`Models.ParitySpec.cs`, `Models.TimetableContract.cs`)
**Complexity**: ⭐ (Low)  
**Purpose**: Data contracts for configuration

**Features**:
- ParitySpec: Strategy configuration (sessions, breakout rules, risk parameters)
- TimetableContract: Timetable structure (streams, slot times, instruments)

---

### 12. **Tick Rounding** (`TickRounding.cs`)
**Complexity**: ⭐ (Low)  
**Purpose**: Price rounding to tick size

**Features**:
- Rounds breakout levels to tick size
- Supports different rounding methods (floor, ceiling, nearest)

---

### 13. **NinjaTrader Integration** (`ninjatrader/`)
**Complexity**: ⭐⭐⭐ (Medium)  
**Purpose**: NinjaTrader strategy hosts

**Files**:
- **RobotSimStrategy.cs**: SIM mode strategy host
- **RobotSkeletonStrategy.cs**: DRYRUN mode strategy host

**Features**:
- Timer-driven Tick() method
- Account verification
- Connection status handling
- Forwards NT events to execution adapter

---

### 14. **Harness** (`harness/`)
**Complexity**: ⭐⭐⭐ (Medium)  
**Purpose**: Standalone testing harness

**Features**:
- Historical replay capability
- Bar provider integration
- Testing without NinjaTrader

---

## Complexity Analysis

### By Module

| Module | Complexity | Lines | Primary Complexity Driver |
|--------|-----------|-------|---------------------------|
| **StreamStateMachine** | ⭐⭐⭐⭐⭐ | ~1,600 | State machine + timezone + range computation |
| **RobotEngine** | ⭐⭐⭐⭐ | ~884 | Multi-stream coordination + timetable polling |
| **Execution Layer** | ⭐⭐⭐⭐ | ~1,200 | Adapter pattern + retry logic + stand-down |
| **HealthMonitor** | ⭐⭐⭐ | ~300 | Time-based monitoring + session awareness |
| **Logging System** | ⭐⭐⭐ | ~400 | Async I/O + rotation + filtering |
| **Time Service** | ⭐⭐ | ~150 | Timezone conversion |
| **Supporting Systems** | ⭐-⭐⭐ | ~500 | Various utilities |

### Complexity Drivers (Top 5)

1. **State Machine Complexity** (StreamStateMachine)
   - 6 states with transitions
   - Recovery states
   - State persistence
   - **Impact**: High cognitive load

2. **Timezone Handling** (StreamStateMachine, TimeService)
   - UTC ↔ Chicago conversion everywhere
   - Bar filtering by Chicago time
   - DST transitions
   - **Impact**: Critical for correctness, easy to get wrong

3. **Multi-Source Range Computation** (StreamStateMachine)
   - Historical bars + live bars
   - Hybrid initialization
   - Bar deduplication
   - **Impact**: Complex data merging logic

4. **Defensive Safety Systems** (RiskGate, Stand-Down, HealthMonitor)
   - Multiple blocking gates
   - Failure recovery
   - Monitoring overhead
   - **Impact**: Adds complexity but prevents disasters

5. **Execution Adapter Pattern** (Execution Layer)
   - Multiple adapter implementations
   - Retry logic
   - Stand-down callbacks
   - **Impact**: Abstraction overhead

---

## Module Dependencies

```
RobotEngine
  ├─> StreamStateMachine (one per stream)
  │     ├─> TimeService
  │     ├─> RobotLogger
  │     ├─> JournalStore
  │     ├─> RiskGate
  │     ├─> ExecutionAdapter
  │     └─> IBarProvider (optional)
  │
  ├─> ExecutionAdapter (selected by ExecutionMode)
  │     ├─> ExecutionJournal
  │     ├─> RiskGate
  │     └─> NotificationService
  │
  ├─> RiskGate
  │     └─> KillSwitch
  │
  ├─> HealthMonitor
  │     └─> NotificationService
  │
  ├─> FilePoller (timetable)
  ├─> JournalStore
  ├─> RobotLoggingService
  └─> TimeService
```

---

## What Each Module Does (Simple Summary)

### Core Strategy Flow

1. **RobotEngine**: "I manage all streams and coordinate everything"
2. **StreamStateMachine**: "I compute ranges and detect breakouts for one stream"
3. **ExecutionAdapter**: "I place orders with the broker"
4. **RiskGate**: "I check if it's safe to trade before any order"
5. **HealthMonitor**: "I watch for problems and alert operators"
6. **Logging System**: "I record everything that happens"

### Supporting Systems

- **TimeService**: "I convert times between UTC and Chicago"
- **JournalStore**: "I save stream state so we can resume later"
- **FilePoller**: "I watch the timetable file for changes"
- **Bar Provider**: "I provide historical bars when needed"
- **NotificationService**: "I send alerts to operators"
- **KillSwitch**: "I can stop all trading instantly"

---

## Recent Simplifications (Post-Audit)

### ✅ Removed Complexity

1. **Late-Start Blocking** - Removed `_lateStartAfterSlot` flag and logic
2. **NO_DATA Blocking** - Removed zero-bars NO_TRADE commit
3. **Execution Mode Differences** - DRYRUN/SIM now execute identically
4. **Historical Bar Restrictions** - Can use history in all modes
5. **Range Data Missing Block** - Allows retry instead of NO_TRADE

### ⚠️ Remaining Complexity

1. **RiskGate** - 6 operational safety gates still block trading
2. **Stand-Down Logic** - Stops trading on protective order failure
3. **State Machine** - 6 states (could be simplified to 3-4)
4. **Timezone Handling** - Necessary but complex
5. **Multi-Source Range Computation** - Necessary for hybrid init

---

## Recommendations for Further Simplification

### If Ultra-Simple Compliance Required

1. **Remove RiskGate** (or make non-blocking warnings)
   - Impact: Removes 6 safety checks
   - Risk: No operational safety gates

2. **Remove Stand-Down Logic**
   - Impact: Continues trading even on protective failure
   - Risk: Unprotected positions possible

3. **Simplify State Machine**
   - Reduce to: RANGE_BUILDING → RANGE_LOCKED → DONE
   - Remove: IDLE, ARMED, RECOVERY_MANAGE
   - Impact: Simpler state transitions

4. **Remove HealthMonitor**
   - Impact: No operational monitoring
   - Risk: Silent failures possible

5. **Remove Execution Journal**
   - Impact: No idempotency
   - Risk: Double-submission possible

---

## Conclusion

The robot implements a **simple strategy** (range breakout) with **extensive safety systems**. The core strategy logic is straightforward, but defensive systems add significant complexity.

**Current State**: 
- ✅ Core strategy: Simple
- ⚠️ Safety systems: Complex but protective
- ✅ Hybrid initialization: Working
- ⚠️ Ultra-simple compliance: 71% (5 of 7 blockers removed)

**Trade-off**: Safety vs. Simplicity
- Keep safety systems → Protected but complex
- Remove safety systems → Simple but risky
