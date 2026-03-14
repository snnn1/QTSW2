# IEA Execution Command Architecture

## Overview

The Execution Command Layer sits between strategy/runtime logic and the Instrument Execution Authority (IEA). Strategy layers request execution outcomes via commands; the IEA orchestrates how execution occurs. Adapters remain transport-only.

## Design Principles

- **Strategy decides WHAT** — flatten, cancel, submit entry
- **IEA determines HOW** — order lifecycle, flatten orchestration, NT thread dispatch
- **Adapters are transport** — no lifecycle logic; only translate to NinjaTrader API

## Command Types

| Command | Purpose |
|---------|---------|
| `FlattenIntentCommand` | Request flatten of an intent's position. Includes `FlattenReason` (SLOT_EXPIRED, FORCED_FLATTEN, EMERGENCY, RECOVERY, BOOTSTRAP, IEA_BLOCK). |
| `CancelIntentOrdersCommand` | Request cancellation of working orders for an intent. |
| `SubmitEntryIntentCommand` | Request submission of entry stop brackets (long/short). Includes BreakLong, BreakShort, Quantity, OcoGroup, protective prices. |

All commands inherit `ExecutionCommandBase` with: `CommandId` (GUID for lifecycle correlation), `Instrument`, `IntentId`, `Reason`, `CallerContext`, `TimestampUtc`.

## Command Flow

```
StreamStateMachine / RobotEngine
    → adapter.EnqueueExecutionCommand(command)
    → IEA.EnqueueExecutionCommand
    → IEA worker: DispatchCommand
    → Handler creates NtAction (NtFlattenInstrumentCommand, NtCancelOrdersCommand, NtSubmitEntryIntentCommand)
    → Executor.EnqueueNtAction(ntAction)
    → Strategy thread drains NtActions
    → Adapter executes (RequestFlatten, CancelIntentOrdersReal, ExecuteSubmitEntryIntent)
    → NinjaTrader API
```

## Strategy vs Execution Responsibilities

| Layer | Responsibility |
|-------|-----------------|
| **Strategy** | Decide when flatten/cancel/submit is needed; emit command with context |
| **IEA** | Serialize execution per instrument; dispatch to handlers; ensure NT thread safety |
| **Adapter** | Forward commands to IEA; execute NtActions on strategy thread |

## Integration with Recovery / Bootstrap / Supervisory

| System | Integration |
|--------|-------------|
| **RecoveryPhase3** | Unchanged. Recovery flatten uses existing `RequestFlatten` path. |
| **BootstrapPhase4** | Unchanged. Bootstrap adoption and snapshot logic unchanged. |
| **SupervisoryPhase5** | Unchanged. Supervisory actions use existing `RequestSupervisoryAction`. |
| **OrderRegistry** | Unchanged. Handlers use existing registry and protective cancellation logic. |

The command layer sits above these components and uses them. No changes to recovery, bootstrap, registry, or supervisory logic.

## NinjaTrader Thread Safety

All broker interaction must execute through the existing NT execution path:

- **IEA worker** → creates NtAction → `Executor.EnqueueNtAction`
- **Strategy thread** → `DrainNtActions` → adapter executes NtAction

Command handlers never call NinjaTrader APIs directly. They enqueue NtActions for strategy-thread execution.

## Audit Rule

**Strategy layers should NOT call adapter.Flatten, adapter.SubmitEntryOrders, or adapter.CancelOrders directly.** Use `EnqueueExecutionCommand` instead so execution flows through the IEA as the single authority.

## Per-Instrument Command Serialization

Each IEA instance owns a single `_executionQueue`. All commands for that instrument are enqueued to the same queue and processed sequentially by the worker. This guarantees commands execute in enqueue order for the same instrument (e.g. CancelIntentOrdersCommand before FlattenIntentCommand when emitted in that order).

## Logging Events

| Event | When |
|-------|------|
| `EXECUTION_COMMAND_RECEIVED` | IEA receives command |
| `EXECUTION_COMMAND_DISPATCHED` | Handler dispatches to NtAction |
| `EXECUTION_COMMAND_COMPLETED` | Handler finishes (NtAction enqueued) |
| `EXECUTION_COMMAND_REJECTED` | Executor not set |
| `EXECUTION_COMMAND_ERROR` | Handler throws |

Payload includes: `commandId`, `instrument`, `intentId`, `commandType`, `reason`, `callerContext`, `timestampUtc`. Use `commandId` to correlate the full lifecycle (RECEIVED → DISPATCHED → COMPLETED).

## Files

| File | Purpose |
|------|---------|
| `modules/robot/contracts/ExecutionCommandTypes.cs` | Command types, FlattenReason enum |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.Commands.cs` | EnqueueExecutionCommand, DispatchCommand, handlers |
| `RobotCore_For_NinjaTrader/Execution/StrategyThreadExecutor.cs` | NtSubmitEntryIntentCommand, INtActionExecutor.ExecuteSubmitEntryIntent |
| `RobotCore_For_NinjaTrader/Execution/StreamStateMachine.cs` | HandleForcedFlatten, HandleSlotExpiry use commands |
