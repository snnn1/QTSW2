# Robot System Deep Dive

**Generated**: 2026-01-25  
**Purpose**: Comprehensive analysis of the Robot execution system architecture, operation, and issues

---

## Executive Summary

The **Robot** is a NinjaTrader-based automated trading execution engine that interprets daily trading timetables and executes trades using Analyzer-equivalent semantics. It operates as a deterministic interpreter of `data/timetable/timetable_current.json`, executing at most one trade per stream per trading date.

**Current Status**: 
- ✅ Core architecture complete
- ✅ DRYRUN mode fully functional
- ✅ SIM mode structured (stub implementation)
- ⚠️ LIVE mode not yet enabled
- ⚠️ Several known issues identified (see Issues section)

---

## Architecture Overview

### High-Level Flow

```
Timetable File (timetable_current.json)
    ↓
RobotEngine (main orchestrator)
    ├─ FilePoller (monitors timetable changes)
    ├─ TimeService (Chicago ↔ UTC conversion)
    ├─ StreamStateMachine[] (one per stream)
    │   ├─ PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE
    │   ├─ Bar buffer (retrospective range computation)
    │   └─ Execution integration (RiskGate, ExecutionJournal)
    ├─ ExecutionAdapter (DRYRUN/SIM/LIVE)
    ├─ RiskGate (fail-closed safety checks)
    ├─ ExecutionJournal (idempotency)
    └─ HealthMonitor (liveness monitoring)
```

### Core Components

#### 1. **RobotEngine** (`RobotCore_For_NinjaTrader/RobotEngine.cs`)
- **Purpose**: Main orchestrator, manages all streams
- **Responsibilities**:
  - Timetable loading and validation
  - Trading date locking (CME rollover logic)
  - Stream creation and lifecycle
  - Bar distribution to streams
  - Connection recovery state machine
  - Engine-level logging and diagnostics

**Key Features**:
- Thread-safe entry points (`Tick()`, `OnBar()`) via `_engineLock`
- Trading date authority: Only executes when `trading_date == today's Chicago date`
- Timetable reactivity: Polls for changes and applies updates (before commit)
- Connection recovery: Handles disconnect/reconnect scenarios
- Bar rejection tracking: Monitors date mismatches and partial bars

#### 2. **StreamStateMachine** (`RobotCore_For_NinjaTrader/StreamStateMachine.cs`)
- **Purpose**: Per-stream state machine managing execution lifecycle
- **State Progression**:
  ```
  PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE
  ```

**State Details**:
- **PRE_HYDRATION**: Loading historical bars before range start
- **ARMED**: Waiting for range start time, ready to build range
- **RANGE_BUILDING**: Actively tracking range high/low
- **RANGE_LOCKED**: Range frozen at slot_time, brackets placed
- **DONE**: Terminal state (TARGET, STOP, BE_STOP, FORCED_FLATTEN, NO_TRADE, ERROR_STANDDOWN)

**Key Features**:
- Bar buffering for retrospective range computation
- Bar source tracking (LIVE > BARSREQUEST > CSV) for deduplication
- Gap tolerance monitoring (invalidates range if gaps exceed thresholds)
- Range invalidation on data feed failures
- Break-even monitoring (65% of target → move stop to BE + 1 tick)

#### 3. **Execution Architecture** (Phases A, B, C1)

**Phase A** (Complete): Foundation
- `ExecutionMode` enum (DRYRUN, SIM, LIVE)
- `IExecutionAdapter` interface
- `ExecutionJournal` (idempotency)
- `RiskGate` (fail-closed checks)
- `KillSwitch` (global safety)

**Phase B** (Complete): Integration
- Wired into `RobotEngine` and `StreamStateMachine`
- `ExecutionAdapterFactory` (creates adapter based on mode)
- `NinjaTraderSimAdapter` (structured stub)
- `ExecutionSummary` tracking

**Phase C1** (Complete): NT Integration Structure
- SIM adapter ready for NT API calls
- Order submission sequencing documented
- Fill callback structure defined

**Current Status**:
- ✅ DRYRUN: Fully functional
- ✅ SIM: Structured, ready for NT API integration
- ❌ LIVE: Not yet enabled (throws if attempted)

#### 4. **TimeService** (`RobotCore_For_NinjaTrader/TimeService.cs`)
- **Purpose**: DST-aware Chicago ↔ UTC conversion
- **Critical**: No hardcoded offsets, uses real timezone math
- **Usage**: All slot times converted from Chicago to UTC for NinjaTrader

#### 5. **RiskGate** (`RobotCore_For_NinjaTrader/Execution/RiskGate.cs`)
- **Purpose**: Fail-closed safety checks before ANY order submission
- **Gates**:
  1. Recovery state guard (blocks during disconnect recovery)
  2. Kill switch (global safety)
  3. Timetable validated
  4. Stream armed
  5. Within allowed session window
  6. Intent completeness = COMPLETE
  7. Trading date set
  8. Execution mode validation

**Behavior**: All gates must pass, otherwise execution blocked

#### 6. **ExecutionJournal** (`RobotCore_For_NinjaTrader/Execution/ExecutionJournal.cs`)
- **Purpose**: Prevents double-submission (idempotency)
- **Mechanism**: Intent ID computed from 15 canonical fields
- **Storage**: Per `(trading_date, stream, intent_id)`
- **States**: SUBMITTED, FILLED, REJECTED

#### 7. **HealthMonitor** (`RobotCore_For_NinjaTrader/HealthMonitor.cs`)
- **Purpose**: Liveness monitoring and alerting
- **Tracks**:
  - Engine tick heartbeat
  - Bar heartbeat per instrument
  - Stuck streams
  - Data feed stalls
  - Critical events

---

## How It Works

### Startup Sequence

1. **RobotEngine Construction**:
   - Loads logging config (`configs/robot/logging.json`)
   - Loads parity spec (`configs/analyzer_robot_parity.json`)
   - Creates `FilePoller` for timetable monitoring
   - Creates `TimeService` for timezone conversion
   - Creates `ExecutionAdapter` based on mode
   - Creates `RiskGate`, `ExecutionJournal`, `KillSwitch`

2. **Start()**:
   - Validates timetable exists and is valid
   - Locks trading date (CME rollover logic: if Chicago hour >= 17, use next day)
   - Creates `StreamStateMachine` for each enabled stream
   - Starts timetable polling
   - Starts health monitoring

3. **Pre-Hydration**:
   - Each stream requests historical bars from `RangeStartUtc` to `SlotTimeUtc`
   - Bars loaded via `NinjaTraderBarProviderRequest` (BarsRequest API or Bars collection fallback)
   - Stream enters `PRE_HYDRATION` state
   - Once bars loaded, stream transitions to `ARMED`

### Runtime Operation

1. **Tick()** (called by NinjaTrader every second):
   - Engine-level: Updates heartbeat, checks connection status
   - Per-stream: Calls `Tick()` on each stream's state machine
   - Stream `Tick()`: Handles state transitions, checks market close, monitors break-even

2. **OnBar()** (called by NinjaTrader on bar close):
   - Engine-level: Validates bar date matches trading date
   - Distributes bar to appropriate stream(s)
   - Stream `OnBar()`: Buffers bar, updates range if in `RANGE_BUILDING`

3. **State Transitions**:
   - **ARMED → RANGE_BUILDING**: When `utcNow >= RangeStartUtc` and bars available
   - **RANGE_BUILDING → RANGE_LOCKED**: When `utcNow >= SlotTimeUtc`
   - **RANGE_LOCKED → IN_POSITION**: When entry order fills
   - **Any → DONE**: On target/stop/BE/forced flatten/no trade

### Range Building Logic

1. **Window**: `[RangeStartUtc, SlotTimeUtc)` (exclusive end)
2. **Computation**: At `SlotTimeUtc`, compute:
   - `range_high = max(bar.high)` for bars in window
   - `range_low = min(bar.low)` for bars in window
   - `range_size = range_high - range_low` (in points)
3. **Breakout Levels**:
   - `brk_long = round_to_tick(range_high + tick_size)`
   - `brk_short = round_to_tick(range_low - tick_size)`
4. **Entry Detection**:
   - Immediate if `freeze_close >= brk_long` (long) or `freeze_close <= brk_short` (short)
   - Otherwise, breakout on first bar where `high >= brk_long` or `low <= brk_short`

### Execution Flow (SIM/LIVE)

1. **Range Lock**:
   - Compute range and breakout levels
   - Place entry brackets (long + short, OCO within stream)
   - Log `DRYRUN_INTENDED_*` events (always, for parity)

2. **Entry Fill**:
   - If DRYRUN: Log only
   - If SIM/LIVE:
     - Build `Intent` object
     - Compute `intent_id`
     - Check `ExecutionJournal` (idempotency)
     - Evaluate `RiskGate` (all gates must pass)
     - Submit entry order via adapter
     - Record in `ExecutionJournal`

3. **Protective Orders**:
   - Submit stop + target after entry fill confirmation
   - Stop distance: `min(range_size, 3 * target_pts)`
   - Target: `entry ± base_target(instrument)`

4. **Break-Even**:
   - Monitor price: when reaches 65% of target distance
   - Modify stop to `entry ± 1 tick`

5. **Exit**:
   - Target fill → DONE
   - Stop fill → DONE
   - BE stop fill → DONE
   - Forced flatten → DONE

---

## Previously Identified Issues (Now Fixed)

### 1. **ARMED_WAITING_FOR_BARS Log Spam** ✅ FIXED
**Location**: `StreamStateMachine.cs:1424`

**Problem**: `ARMED_WAITING_FOR_BARS` event logged every `Tick()` when waiting for bars, causing log spam if `Tick()` called frequently.

**Impact**: Makes it harder to find important events, log file bloat.

**Fix**: Rate-limited to once per 5 minutes using `_lastArmedWaitingForBarsLogUtc` field.

**Status**: ✅ Fixed - Rate limiting implemented.

### 2. **OnBar Processing After Commit** ✅ FIXED
**Location**: `StreamStateMachine.cs:2111-2114`

**Problem**: `OnBar()` didn't check if stream is committed before processing bars.

**Impact**: Unnecessary processing after commit (harmless but inefficient).

**Fix**: Early return added if `_journal.Committed` is true.

**Status**: ✅ Fixed - Early return check implemented.

### 3. **Missing Bar Count Check in HandleRangeBuildingState** ✅ FIXED
**Location**: `StreamStateMachine.cs:1564-1574`

**Problem**: `HandleRangeBuildingState()` didn't check if bars are available before range computation.

**Impact**: Low (fix prevents this scenario, but defensive check safer).

**Fix**: Defensive check for bar availability added with warning log.

**Status**: ✅ Fixed - Defensive check implemented.

## Known Issues

### 4. **Race Condition: Bars Arrive Right After Check** ✅ ACCEPTABLE
**Location**: `StreamStateMachine.cs` (ARMED state handling)

**Problem**: Small window where bars could arrive right after `GetBarBufferCount() == 0` check.

**Impact**: Minimal - handled correctly by checking again on next `Tick()`.

**Status**: Current behavior acceptable, no fix needed.

### 5. **Race Condition: Transition to RANGE_BUILDING Right Before Market Close** ✅ ACCEPTABLE
**Location**: `StreamStateMachine.cs` (state transitions)

**Problem**: Stream could transition to `RANGE_BUILDING` right before market close.

**Impact**: Minimal - `HandleRangeBuildingState()` checks market close first.

**Status**: Current behavior acceptable.

### 6. **Inconsistent Diagnostic Logging** ✅ FIXED
**Location**: `StreamStateMachine.cs:1383-1407`

**Problem**: `ARMED_STATE_DIAGNOSTIC` used `_lastHeartbeatUtc` (7-minute cadence) while `ARMED_WAITING_FOR_BARS` used dedicated field (5-minute cadence).

**Impact**: Minor - inconsistent logging behavior.

**Fix**: Introduced dedicated `_lastArmedStateDiagnosticUtc` field and standardized both to 5-minute rate limit.

**Status**: ✅ Fixed - Consistent rate limiting implemented.

### 7. **LIVE Mode Not Yet Enabled** ⚠️ EXPECTED
**Location**: `ExecutionAdapterFactory.cs`

**Problem**: LIVE mode throws exception if attempted.

**Impact**: Expected - LIVE mode intentionally disabled until SIM fully tested.

**Status**: By design, not an issue.

### 8. **NinjaTraderLiveAdapter TODOs** ⚠️ EXPECTED
**Location**: `NinjaTraderLiveAdapter.cs`

**Problem**: Contains TODO comments for Phase C implementation.

**Impact**: Expected - LIVE adapter not yet implemented.

**Status**: By design, not an issue.

### 9. **HealthMonitor Missing run_id** ✅ FIXED
**Location**: `RobotEngine.cs:270-280`

**Problem**: HealthMonitor may use `UNKNOWN_RUN` dedupe key if `run_id` missing.

**Impact**: Low - warning logged, fallback works.

**Fix**: Defensive check added at top of `Start()` method to auto-generate `run_id` if missing, with warning log.

**Status**: ✅ Fixed - Defensive guarantee implemented.

---

## Potential Issues & Risks

### 1. **Bar Date Mismatch Handling**
**Risk**: Bars arriving with wrong trading date could cause issues.

**Mitigation**: 
- Engine-level validation rejects bars with date mismatch
- Logs `BAR_DATE_MISMATCH` events
- Tracks rejection statistics

**Status**: ✅ Handled

### 2. **Gap Tolerance Violations**
**Risk**: Data feed gaps could invalidate ranges unexpectedly.

**Mitigation**:
- Gap tolerance constants defined (`MAX_SINGLE_GAP_MINUTES = 3.0`, `MAX_TOTAL_GAP_MINUTES = 6.0`)
- Only `DATA_FEED_FAILURE` gaps invalidate (not `LOW_LIQUIDITY`)
- Range invalidation logged and notified

**Status**: ✅ Handled

### 3. **Connection Recovery**
**Risk**: Disconnect/reconnect scenarios could cause duplicate orders or missed fills.

**Mitigation**:
- Connection recovery state machine (`ConnectionRecoveryState`)
- Execution blocked during recovery (`RECOVERY_STATE` gate)
- Broker sync gate waits for order/execution updates
- `ExecutionJournal` prevents duplicate submissions

**Status**: ✅ Handled

### 4. **Timetable Reactivity**
**Risk**: Timetable updates mid-day could cause inconsistent state.

**Mitigation**:
- Timetable updates only apply before stream commit
- After commit (entry filled, NO_TRADE, forced flatten), updates ignored
- Per-stream idempotency prevents re-arming

**Status**: ✅ Handled

### 5. **Thread Safety**
**Risk**: Concurrent access to shared state could cause race conditions.

**Mitigation**:
- `_engineLock` serializes engine entry points (`Tick()`, `OnBar()`)
- `_barBufferLock` protects bar buffer access
- `_recoveryLock` prevents re-entrancy in recovery runner

**Status**: ✅ Handled

### 6. **Memory Usage**
**Risk**: Bar buffering could consume excessive memory for long sessions.

**Mitigation**:
- Bar buffer cleared after range computation
- Only buffers bars within range window
- Pre-hydration loads only necessary bars

**Status**: ✅ Handled (monitor in production)

### 7. **Execution Journal Persistence**
**Risk**: Journal corruption could allow duplicate submissions.

**Mitigation**:
- Journal stored per `(trading_date, stream, intent_id)`
- Atomic writes (temp file → rename)
- Fallback to account/order inspection if journal unreliable

**Status**: ✅ Handled

---

## Configuration Requirements

### Required Configuration Files

1. **`configs/analyzer_robot_parity.json`**:
   - Instrument tick sizes
   - Session range start times
   - Market close time
   - Target ladders
   - Slot end times validation

2. **`configs/robot/logging.json`**:
   - Log directory
   - Diagnostic log enablement
   - Rate limits
   - Event filtering

3. **`configs/robot/kill_switch.json`**:
   - Global kill switch enablement

4. **`data/timetable/timetable_current.json`**:
   - Trading date
   - Timezone (must be "America/Chicago")
   - Stream directives (stream, instrument, session, slot_time, enabled)

### Critical Configuration Values

- **Market Close Time**: Default 16:00 CT (must match Analyzer)
- **Range Start Times**: S1 = 02:00 CT, S2 = 08:00 CT (must match Analyzer)
- **Tick Sizes**: Must match Analyzer exactly (ES=0.25, CL=0.01, etc.)
- **Break-Even Trigger**: 65% of target (must match Analyzer)
- **Break-Even Offset**: ±1 tick (must match Analyzer)

---

## Testing & Validation

### Parity Testing
- Robot must match Analyzer execution semantics exactly
- Parity table (`docs/robot/ANALYZER_ROBOT_PARITY_TABLE.md`) is single source of truth
- Any Analyzer change must update parity table first

### Acceptance Tests (from Blueprint)
- ✅ Stale timetable → no orders
- ✅ Timezone mismatch → no orders
- ✅ Timetable update before commit → uses new slot_time
- ✅ Timetable update after commit → ignored
- ✅ Simultaneous same-instrument trades → both positions coexist
- ✅ Partial fill → robot completes fill
- ✅ Forced flatten → exits and cancels orders
- ✅ Restart in position → restores protection, no duplicate entry
- ✅ No-trade cutoff → logs NO_TRADE at market close

### Current Test Status
- ✅ DRYRUN: Fully tested, parity verified
- ⚠️ SIM: Structured, ready for NT API integration
- ❌ LIVE: Not yet enabled

---

## Recommendations

### High Priority

1. **Complete SIM Mode Testing**:
   - Integrate NT API calls in `NinjaTraderSimAdapter`
   - Test order submission, fill callbacks, OCO grouping
   - Validate execution summary generation

### Low Priority Enhancements

1. **Memory Monitoring**:
   - Add metrics for bar buffer sizes
   - Alert if memory usage exceeds thresholds

2. **Performance Profiling**:
   - Profile `Tick()` and `OnBar()` performance
   - Optimize hot paths if needed

3. **Enhanced Diagnostics**:
   - Add more granular state transition logging
   - Improve stuck stream detection

---

## Architecture Strengths

1. **Fail-Closed Design**: Multiple safety gates prevent unintended execution
2. **Idempotency**: ExecutionJournal prevents duplicate submissions
3. **Parity Enforcement**: Strict adherence to Analyzer semantics
4. **Comprehensive Logging**: Extensive event logging for auditability
5. **Recovery Handling**: Robust disconnect/reconnect handling
6. **Thread Safety**: Proper locking prevents race conditions
7. **Modular Design**: Clean separation of concerns (execution, risk, logging)

---

## Architecture Weaknesses

1. **Complex State Machine**: Many states and transitions can be hard to reason about
2. **Bar Buffer Management**: Complex deduplication logic across multiple sources
3. **Timetable Reactivity**: Complex rules about when updates apply
4. **Gap Tolerance Logic**: Complex gap detection and invalidation rules
5. **Large Codebase**: `StreamStateMachine.cs` is very large (~4600 lines)

---

## Conclusion

The Robot system is a well-architected execution engine with robust safety mechanisms and comprehensive logging. The core functionality is complete and working in DRYRUN mode. SIM mode is structured and ready for NT API integration. Several minor issues have been identified but don't prevent operation. The system follows fail-closed principles and maintains strict parity with the Analyzer.

**Key Takeaways**:
- ✅ Core architecture is solid
- ✅ Safety mechanisms are comprehensive
- ✅ Previously identified logging issues have been fixed
- ⚠️ SIM mode needs NT API integration
- ❌ LIVE mode not yet enabled (by design)

**Next Steps**:
1. ✅ Verify HealthMonitor run_id defensive guarantee (completed)
2. ✅ Standardize diagnostic logging rate limits (completed)
3. Complete SIM mode NT API integration
4. Test SIM mode thoroughly
5. Enable LIVE mode only after SIM validation

---

## Related Documentation

- **Blueprint**: `docs/robot/NinjaTrader Robot Blueprint (Execution Layer).txt`
- **Parity Table**: `docs/robot/ANALYZER_ROBOT_PARITY_TABLE.md`
- **Phase Summaries**: `docs/robot/execution/PHASE_*_SUMMARY.md`
- **Known Issues**: `docs/robot_issues_after_range_building_fix.md`
- **Logging Guide**: `docs/robot/LOGGING.md`
