# Phase 4: Crash / Disconnect Determinism

## Overview

Phase 4 adds deterministic bootstrap/reconnect handling on top of the IEA position authority, order ownership, and Phase 3 recovery state machine. Restart and reconnect are treated as controlled state-reconstruction phases, not normal runtime.

## Architecture

```
Entry Points:
  - Strategy Start / SetNTContext → BeginBootstrapForInstrument
  - Connection Recovered → BeginReconnectRecovery (per instrument)
  - First Execution Update → ScanAndAdopt (only after bootstrap ready)

Bootstrap Layer:
  - BeginBootstrapForInstrument / BeginReconnectRecovery
  - Snapshot Collection (four views: broker, registry, journal, runtime)
  - Bootstrap Classification (BootstrapClassifier)
  - Bootstrap Decision (BootstrapDecider)

Phase 3 Integration:
  - RESUME / ADOPT → RESOLVED
  - FLATTEN_THEN_RECONSTRUCT → RECOVERY_ACTION_REQUIRED → ExecuteRecoveryFlatten
  - HALT → HALTED
```

## Bootstrap States

New states in `RecoveryState` (before NORMAL in lifecycle):

- `BOOTSTRAP_PENDING` – Bootstrap requested
- `SNAPSHOTTING` – Gathering four views
- `BOOTSTRAP_DECIDING` – Classification done, choosing decision
- `BOOTSTRAP_ADOPTING` – Adoption in progress (ADOPT path)

`IsInBootstrap` is true for these states. `IsInRecovery` includes them so existing Phase 3 guards block normal management.

## Types

- **BootstrapReason**: STRATEGY_START, PLATFORM_RESTART, CONNECTION_RECOVERED, etc.
- **BootstrapClassification**: CLEAN_START, RESUME_WITH_NO_POSITION_NO_ORDERS, ADOPTION_REQUIRED, POSITION_PRESENT_NO_OWNED_ORDERS, LIVE_ORDERS_PRESENT_NO_POSITION, JOURNAL_RUNTIME_DIVERGENCE, MANUAL_INTERVENTION_PRESENT, UNSAFE_STARTUP_AMBIGUITY
- **BootstrapDecision**: RESUME, ADOPT, RECONCILE_THEN_RESUME, FLATTEN_THEN_RECONSTRUCT, HALT
- **BootstrapSnapshot**: BrokerPositionQty, BrokerWorkingOrderCount, JournalQty, UnownedLiveOrderCount, RegistrySnapshot, etc.

## Decision Rules

| Classification | Decision |
|----------------|----------|
| CLEAN_START | RESUME |
| RESUME_WITH_NO_POSITION_NO_ORDERS | RESUME |
| ADOPTION_REQUIRED | ADOPT |
| POSITION_PRESENT_NO_OWNED_ORDERS | FLATTEN_THEN_RECONSTRUCT |
| LIVE_ORDERS_PRESENT_NO_POSITION | FLATTEN_THEN_RECONSTRUCT |
| JOURNAL_RUNTIME_DIVERGENCE | FLATTEN_THEN_RECONSTRUCT |
| MANUAL_INTERVENTION_PRESENT | HALT |
| UNSAFE_STARTUP_AMBIGUITY | HALT |

## Entry Points

1. **SetNTContext** (NinjaTraderSimAdapter): After IEA binding, runs `HydrateIntentsFromOpenJournals` first (so runtime snapshot and adoption have correct IntentMap), then calls `BeginBootstrapForInstrument(executionInstrumentKey, BootstrapReason.STRATEGY_START, utcNow)`.

2. **Connection Recovery** (RobotEngine.RunRecovery): When IEAs exist for the account, calls `BeginReconnectRecovery(instrument, utcNow)` for each IEA instead of account-level recovery.

## Snapshot Stale Handling

When a critical event (fill or order state change to Working/Filled/Cancelled/Rejected) arrives during bootstrap, `MarkBootstrapSnapshotStale` is called. This triggers a re-run of the snapshot callback. Valid transition `BOOTSTRAP_DECIDING → SNAPSHOTTING` supports reruns.

## ScanAndAdoptExistingProtectives

- Runs only when bootstrap is complete (NORMAL or RESOLVED) and not during bootstrap.
- For ADOPT decision, runs explicitly via `RunBootstrapAdoption` (ScanAndAdopt + OnBootstrapAdoptionCompleted).

## Constraints

- Fail-closed: do not resume from ambiguous state
- No bypass of IEA flatten authority
- No silent auto-adopt of ambiguous orders
- No normal management during bootstrap
- No assumption of stable event ordering during startup

## Files

| File | Purpose |
|------|---------|
| `modules/robot/contracts/BootstrapPhase4Types.cs` | BootstrapReason, BootstrapClassification, BootstrapDecision, BootstrapSnapshot, BootstrapClassifier, BootstrapDecider |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.BootstrapPhase4.cs` | BeginBootstrapForInstrument, BeginReconnectRecovery, ProcessBootstrapResult, CanCompleteBootstrap |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.RecoveryPhase3.cs` | Bootstrap states, IsInBootstrap, valid transitions |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` | SetNTContext → BeginBootstrapForInstrument |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | OnBootstrapSnapshotRequested callback |
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | RunRecovery → BeginReconnectRecovery per instrument |
| `RobotCore_For_NinjaTrader/RobotEventTypes.cs` | Phase 4 event types |

## Tests

Run: `dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BOOTSTRAP_PHASE4`

Tests cover: CLEAN_START→RESUME, RESUME_WITH_NO_POSITION_NO_ORDERS→RESUME, MANUAL_INTERVENTION_PRESENT→HALT, POSITION_PRESENT_NO_OWNED_ORDERS→FLATTEN, ADOPTION_REQUIRED→ADOPT, JOURNAL_RUNTIME_DIVERGENCE→FLATTEN, LIVE_ORDERS_PRESENT_NO_POSITION→FLATTEN.
