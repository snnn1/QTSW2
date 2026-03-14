# Phase 5: Operational Control and Supervisory Policy

## Overview

Phase 5 adds a supervisory control layer on top of the IEA position authority, order ownership, recovery state machine, and deterministic bootstrap/reconnect system. It makes the engine governable in live operations: halt/suspend trading, throttle repeated incidents, require operator acknowledgement, apply cooldowns, and expose operational state to logs/status/UI.

**Design principle**: A correct execution engine still needs operational governance. When the system is stressed, ambiguous, or repeatedly anomalous, it should escalate, suspend, cool down, or require operator action—not keep trying to fix itself forever.

## Supervisory States

Instrument-scoped states in `SupervisoryState`:

| State | Intent |
|-------|--------|
| ACTIVE | Normal operation allowed |
| COOLDOWN | No new entries for a period; recovery/bootstrap/flatten allowed |
| SUSPENDED | No new entries or normal trade management; may still observe, reconcile, bootstrap, recover |
| HALTED | Instrument stopped due to unsafe condition |
| AWAITING_OPERATOR_ACK | Explicit manual acknowledgement required before resuming |
| DISABLED | Intentionally disabled by config/operator |

## Valid State Transitions

| From | To |
|------|-----|
| ACTIVE | COOLDOWN, SUSPENDED, HALTED, AWAITING_OPERATOR_ACK, DISABLED |
| COOLDOWN | ACTIVE, SUSPENDED, HALTED |
| SUSPENDED | ACTIVE, HALTED, AWAITING_OPERATOR_ACK |
| HALTED | ACTIVE, AWAITING_OPERATOR_ACK |
| AWAITING_OPERATOR_ACK | ACTIVE, HALTED |
| DISABLED | ACTIVE |

Invalid transitions emit `SUPERVISORY_STATE_TRANSITION_INVALID`.

## Triggers and Escalation Policy

### Supervisory Triggers

| Trigger | Reason | Severity |
|---------|--------|----------|
| Repeated recovery triggers | REPEATED_RECOVERY_TRIGGERS | MEDIUM |
| Repeated bootstrap halts | REPEATED_BOOTSTRAP_HALTS | HIGH |
| Repeated unowned executions | REPEATED_UNOWNED_EXECUTIONS | HIGH |
| Registry/broker divergence | REPEATED_REGISTRY_DIVERGENCE | MEDIUM |
| Reconciliation quantity mismatch | REPEATED_RECONCILIATION_MISMATCH | MEDIUM |
| Repeated flatten actions | REPEATED_FLATTEN_ACTIONS | MEDIUM |
| Recovery halt | REPEATED_RECOVERY_HALT | HIGH |
| IEA enqueue failure | IEA_ENQUEUE_FAILURE | HIGH |
| Manual operator actions | MANUAL_OPERATOR_SUSPEND/HALT/DISABLE | varies |
| Global kill switch | GLOBAL_KILL_SWITCH | CRITICAL |
| Per-instrument kill switch | INSTRUMENT_KILL_SWITCH | CRITICAL |

### Rolling-Window Escalation

- **Window**: 5 minutes
- **Counters**: COOLDOWN_THRESHOLD=2, SUSPEND_THRESHOLD=3, HALT_THRESHOLD=4
- **Policy**: N incidents in window → COOLDOWN; repeated COOLDOWN → SUSPENDED; repeated HALT/flatten → AWAITING_OPERATOR_ACK or HALTED
- **Hysteresis (Phase 5 hardening)**: `MIN_DWELL_BEFORE_COOLDOWN_SECONDS = 120`. After resuming to ACTIVE (from COOLDOWN or AWAITING_OPERATOR_ACK), a new trigger will not re-escalate to COOLDOWN until 2 minutes have passed. Reduces flapping.

## Cooldown Policy

- **Duration**: 60 seconds
- **Effect**: Blocks new entries; recovery/bootstrap/flatten allowed
- **Expiry**: `TryExpireCooldown` called from IEA heartbeat; transitions COOLDOWN → ACTIVE when criteria pass
- **Events**: `INSTRUMENT_COOLDOWN_STARTED`, `INSTRUMENT_COOLDOWN_EXPIRED`

## Suspension vs Halt

| | SUSPENDED | HALTED |
|---|-----------|--------|
| New entries | Blocked | Blocked |
| Normal trade management | Blocked | Blocked |
| Observe/reconcile/bootstrap | Allowed | Allowed |
| Resumable by | Policy or operator | Explicit criteria or operator ack |
| Typical cause | Repeated cooldowns | Kill switch, critical severity, repeated severe incidents |

## Operator Acknowledgement

- **Required when**: AWAITING_OPERATOR_ACK (repeated severe incidents, repeated halt/flatten)
- **Method**: `AcknowledgeInstrument(instrument, reason, operatorContext, utcNow)`
- **Clears AWAITING_OPERATOR_ACK only if**: `CanResumeSupervisoryActive` returns true
- **Does not bypass**: Unresolved recovery/bootstrap/halt conditions
- **Events**: `OPERATOR_ACK_REQUIRED`, `OPERATOR_ACK_RECEIVED`, `OPERATOR_ACK_REJECTED`

## Kill Switch

### Per-Instrument

- **Method**: `ActivateInstrumentKillSwitch(instrument, reason, utcNow)`
- **Effect**: Sets HALTED
- **Event**: `INSTRUMENT_KILL_SWITCH_ACTIVATED`

### Global

- **File**: `configs/robot/kill_switch.json`
- **Effect**: Blocks all execution via RiskGate
- **Event**: `GLOBAL_KILL_SWITCH_ACTIVATED` (throttled, 60s)
- **Notification**: HealthMonitor.ReportCritical when enabled

## Resume Criteria

`CanResumeSupervisoryActive(instrument)` requires:

- No unresolved recovery bootstrap
- No active flatten in progress
- No unowned live orders
- Registry integrity passing
- Cooldown interval expired (if in COOLDOWN)
- Required operator acknowledgement complete (if in AWAITING_OPERATOR_ACK)

**Events**: `SUPERVISORY_RESUME_ALLOWED`, `SUPERVISORY_RESUME_BLOCKED`

## Metrics and Status Exposure

`SUPERVISORY_METRICS` emitted from IEA heartbeat includes:

- supervisory_state
- is_blocked, is_cooldown
- operator_ack_required
- supervisory_triggered_total, supervisory_cooldown_total, supervisory_suspended_total, supervisory_halted_total
- operator_ack_received_total

## Relation to Recovery/Bootstrap

- Supervisory layer **governs** when recovery/bootstrap may continue; it does not replace them
- `IsSupervisorilyBlocked` is checked by RiskGate (via `isInstrumentFrozen` callback)
- Recovery halt → `RecordRecoveryHalt` + `RequestSupervisoryAction(REPEATED_RECOVERY_HALT)`
- Recovery flatten → `RecordFlatten` + `RequestSupervisoryAction(REPEATED_FLATTEN_ACTIONS)`
- Bootstrap halt → `RecordBootstrapHalt` + `RequestSupervisoryAction(REPEATED_BOOTSTRAP_HALTS)`

## Remaining Manual-Operation Limitations

- Operator acknowledgement: no API/UI yet; requires code or external tool to call `AcknowledgeInstrument`
- Per-instrument kill switch: no API/UI yet; requires code to call `ActivateInstrumentKillSwitch`
- Supervisory state persistence: not persisted across restarts; state resets to ACTIVE

## Phase 5 Hardening Tests

Run: `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test PHASE5_HARDENING`

Covers:
- **IsExecutionAllowed + KillSwitch**: RobotEngine returns false when kill switch enabled
- **RiskGate + KillSwitch**: CheckGates blocks with KILL_SWITCH_ACTIVE when enabled
- **SupervisoryPolicy hysteresis**: ShouldSuppressCooldownEscalation (2 min dwell before re-escalation to COOLDOWN)

## Files

| File | Purpose |
|------|---------|
| `modules/robot/contracts/SupervisoryPhase5Types.cs` | SupervisoryState, SupervisorySeverity, SupervisoryTriggerReason |
| `modules/robot/core/Execution/SupervisoryPolicy.cs` | ShouldSuppressCooldownEscalation (testable hysteresis logic) |
| `modules/robot/core/Tests/Phase5HardeningTests.cs` | Phase 5 hardening unit tests |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.SupervisoryPhase5.cs` | RequestSupervisoryAction, AcknowledgeInstrument, ActivateInstrumentKillSwitch, CanResumeSupervisoryActive, TryExpireCooldown |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.RecoveryPhase3.cs` | RecordRecoveryHalt, ExecuteRecoveryFlatten → RecordFlatten |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.BootstrapPhase4.cs` | RecordBootstrapHalt |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.OrderRegistryPhase2.cs` | RequestSupervisoryAction on registry divergence |
| `RobotCore_For_NinjaTrader/Execution/RiskGate.cs` | Global kill switch event, SetOnGlobalKillSwitchBlocked |
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | IsInstrumentFrozenOrSupervisorilyBlocked, onSupervisoryCriticalCallback |
| `RobotCore_For_NinjaTrader/HealthMonitor.cs` | Phase 5 event types in ALLOWED_CRITICAL_EVENT_TYPES |

## Tests

Run: `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test SUPERVISORY_PHASE5`

Tests cover: SupervisoryState enum values, SupervisoryTriggerReason required triggers, valid transition pairs.

## State Transition Table

| From \ To | ACTIVE | COOLDOWN | SUSPENDED | HALTED | AWAITING_ACK | DISABLED |
|-----------+--------+----------+-----------+--------+--------------+----------|
| ACTIVE | - | ✓ | ✓ | ✓ | ✓ | ✓ |
| COOLDOWN | ✓ | - | ✓ | ✓ | - | - |
| SUSPENDED | ✓ | - | - | ✓ | ✓ | - |
| HALTED | ✓ | - | - | - | ✓ | - |
| AWAITING_ACK | ✓ | - | - | ✓ | - | - |
| DISABLED | ✓ | - | - | - | - | - |

## Escalation Decision Table

| Condition | Target State |
|-----------|--------------|
| MANUAL_OPERATOR_HALT, INSTRUMENT_KILL_SWITCH, GLOBAL_KILL_SWITCH, CRITICAL severity | HALTED |
| MANUAL_OPERATOR_SUSPEND, cooldown_count >= SUSPEND_THRESHOLD | SUSPENDED |
| MANUAL_OPERATOR_DISABLE | DISABLED |
| recovery_halt_count >= HALT_THRESHOLD, flatten_count >= HALT_THRESHOLD | AWAITING_OPERATOR_ACK |
| Otherwise (first incident, below thresholds) | COOLDOWN |

## Centralized/Replaced Operational Behaviors

| Prior Behavior | Location | Phase 5 Change |
|----------------|----------|---------------|
| _frozenInstruments | RobotEngine | Extended: RiskGate now also checks IsSupervisorilyBlocked |
| blockInstrumentCallback | RobotEngine, NinjaTraderSimAdapter | Calls RequestSupervisoryAction(IEA_ENQUEUE_FAILURE) before existing logic |
| onQuantityMismatch | RobotEngine | Calls RequestSupervisoryAction(REPEATED_RECONCILIATION_MISMATCH) |
| Registry divergence | OrderRegistryPhase2 | Calls RequestSupervisoryAction(REPEATED_REGISTRY_DIVERGENCE) |
| Unowned/manual order | NinjaTraderSimAdapter.NT, IEA.NT | Calls RequestSupervisoryAction(REPEATED_UNOWNED_EXECUTIONS) |
| Recovery halt | RecoveryPhase3 | Calls RecordRecoveryHalt + RequestSupervisoryAction(REPEATED_RECOVERY_HALT) |
| Recovery flatten | RecoveryPhase3 | Calls RecordFlatten + RequestSupervisoryAction(REPEATED_FLATTEN_ACTIONS) |
| Bootstrap halt | BootstrapPhase4 | Calls RecordBootstrapHalt + RequestSupervisoryAction(REPEATED_BOOTSTRAP_HALTS) |
| Kill switch block | RiskGate | Emits GLOBAL_KILL_SWITCH_ACTIVATED, invokes HealthMonitor callback |
| No cooldown | - | **New**: 60s cooldown after incidents |
| No operator ack | - | **New**: AWAITING_OPERATOR_ACK state, AcknowledgeInstrument |
| No per-instrument kill | - | **New**: ActivateInstrumentKillSwitch |

## Recommendation: Instrument vs Account/Global Scope

**Current**: Supervisory state is instrument-scoped only.

**Recommendation**: For most incident classes, instrument-scoped control is sufficient. **Escalate to account/global** when:

- **GLOBAL_KILL_SWITCH**: Already global; no change.
- **Repeated incidents across multiple instruments** (e.g. 3+ instruments in COOLDOWN/HALTED within 5 min): Consider account-level SUSPENDED or AWAITING_OPERATOR_ACK to prevent cascading failures.
- **Connection recovery failures** affecting all instruments: Consider global recovery state (already exists via IExecutionRecoveryGuard) rather than per-instrument only.

**Implementation path**: Add optional `InstrumentExecutionAuthorityRegistry.GetSupervisorySummary(accountName)` returning counts of instruments in each state. If `halted_count >= 3` or `awaiting_ack_count >= 2`, emit `SUPERVISORY_ACCOUNT_ESCALATION` and optionally block all new execution for the account until operator reviews.
