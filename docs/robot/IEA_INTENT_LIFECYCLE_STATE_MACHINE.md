# IEA Intent Lifecycle State Machine

The InstrumentExecutionAuthority (IEA) owns an explicit **Intent Lifecycle State Machine** that formalizes the lifecycle of a trading intent and enforces valid transitions. Execution commands are only executed when the intent is in an allowed state.

## Lifecycle States

| State | Description |
|-------|-------------|
| `CREATED` | Intent created; no orders submitted yet |
| `ENTRY_SUBMITTED` | Entry order submitted to broker |
| `ENTRY_WORKING` | Entry order accepted and working |
| `ENTRY_PARTIALLY_FILLED` | Entry order partially filled |
| `ENTRY_FILLED` | Entry order fully filled |
| `PROTECTIVES_ACTIVE` | Stop and target orders placed |
| `EXIT_PENDING` | Exit in progress (cancel, flatten, or protective fill) |
| `TERMINAL` | Intent completed; no further actions |

## Transition Rules

Valid transitions (current state → transition → new state):

| Current State | Transition | New State |
|---------------|------------|-----------|
| CREATED | SUBMIT_ENTRY | ENTRY_SUBMITTED |
| CREATED | EXIT_STARTED | EXIT_PENDING |
| ENTRY_SUBMITTED | ENTRY_ACCEPTED | ENTRY_WORKING |
| ENTRY_SUBMITTED | ENTRY_PARTIALLY_FILLED | ENTRY_PARTIALLY_FILLED |
| ENTRY_SUBMITTED | ENTRY_FILLED | ENTRY_FILLED |
| ENTRY_SUBMITTED | EXIT_STARTED | EXIT_PENDING |
| ENTRY_WORKING | ENTRY_PARTIALLY_FILLED | ENTRY_PARTIALLY_FILLED |
| ENTRY_WORKING | ENTRY_FILLED | ENTRY_FILLED |
| ENTRY_WORKING | EXIT_STARTED | EXIT_PENDING |
| ENTRY_PARTIALLY_FILLED | ENTRY_FILLED | ENTRY_FILLED |
| ENTRY_PARTIALLY_FILLED | EXIT_STARTED | EXIT_PENDING |
| ENTRY_FILLED | PROTECTIVES_PLACED | PROTECTIVES_ACTIVE |
| ENTRY_FILLED | EXIT_STARTED | EXIT_PENDING |
| PROTECTIVES_ACTIVE | EXIT_STARTED | EXIT_PENDING |
| EXIT_PENDING | INTENT_COMPLETED | TERMINAL |

Invalid transitions (e.g. CREATED → TERMINAL, TERMINAL → SUBMIT_ENTRY) are rejected and emit `INTENT_LIFECYCLE_TRANSITION_INVALID`.

## Command Legality Matrix

| Command | Allowed States |
|---------|----------------|
| SubmitEntryIntentCommand | CREATED |
| CancelIntentOrdersCommand | ENTRY_WORKING, ENTRY_PARTIALLY_FILLED, PROTECTIVES_ACTIVE |
| FlattenIntentCommand | Any non-TERMINAL state |

## Event Triggers

Lifecycle transitions are driven by execution events:

| Event | Transition |
|-------|------------|
| SubmitEntryIntentCommand accepted | SUBMIT_ENTRY → ENTRY_SUBMITTED |
| Entry order accepted by broker | ENTRY_ACCEPTED → ENTRY_WORKING |
| Entry partial fill | ENTRY_PARTIALLY_FILLED |
| Entry full fill | ENTRY_FILLED |
| Protective orders placed successfully | PROTECTIVES_PLACED → PROTECTIVES_ACTIVE |
| CancelIntentOrdersCommand / FlattenIntentCommand accepted | EXIT_STARTED → EXIT_PENDING |
| Stop/Target fill or flatten fill | INTENT_COMPLETED → TERMINAL |

## Logging Events

- **INTENT_LIFECYCLE_TRANSITION**: Valid transition applied. Payload: `intentId`, `previousState`, `newState`, `transition`, `commandId`, `timestampUtc`.
- **INTENT_LIFECYCLE_TRANSITION_INVALID**: Invalid transition rejected. Payload: `intentId`, `currentState`, `attemptedTransition`, `commandType`, `timestampUtc`.

## Integration with Existing Components

The lifecycle state machine **layers on top** of existing structures; it does not replace them.

### OrderRegistry

- **Unchanged.** OrderRegistry remains the authoritative source for broker order ownership.
- Lifecycle state is orthogonal: it tracks intent state, not order state.

### RecoveryPhase3

- **Unchanged.** Recovery logic continues to use journal, registry, and runtime snapshots.
- Lifecycle state is not used for recovery decisions.

### BootstrapPhase4

- **Unchanged.** Bootstrap snapshot and classification logic unchanged.
- Lifecycle state is not persisted or restored from bootstrap.

### SupervisoryPhase5

- **Unchanged.** Supervisory triggers and actions unchanged.
- Lifecycle state does not affect supervisory decisions.

### ExecutionJournal

- **Unchanged.** Journal remains the disk lifecycle record (EntryFilled, TradeCompleted, etc.).
- Lifecycle state is a runtime validation layer; journal is the durable truth.

## Files

| File | Purpose |
|------|---------|
| `modules/robot/contracts/IntentLifecycleTypes.cs` | `IntentLifecycleState` and `IntentLifecycleTransition` enums |
| `modules/robot/contracts/IntentLifecycleValidator.cs` | Transition validation and command legality |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.IntentLifecycle.cs` | IEA lifecycle tracking (`#if NINJATRADER`) |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.Commands.cs` | Command handlers with lifecycle checks |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | Lifecycle updates from execution events |

## Testing

Run intent lifecycle tests:

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test INTENT_LIFECYCLE
```

Tests cover: valid transitions, invalid transitions, and command legality (IsSubmitEntryIntentAllowed, IsCancelIntentOrdersAllowed, IsFlattenIntentAllowed).
