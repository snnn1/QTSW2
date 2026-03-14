# IEA Architecture Roadmap

Two architectural upgrades remain that would meaningfully improve the system. Everything else is optimization.

---

## Improvement 1 — Execution Event Stream

**Current state:** Logs capture events, but they are still just logs.

**Target state:** A true execution engine records an **append-only event stream** with a deterministic sequence.

### Example Event Types

| Event | When |
|-------|------|
| `COMMAND_RECEIVED` | IEA receives execution command |
| `COMMAND_DISPATCHED` | Handler dispatches to NtAction |
| `ORDER_SUBMITTED` | Order sent to broker |
| `ORDER_ACCEPTED` | Broker acknowledges |
| `ORDER_FILLED` | Fill received |
| `INTENT_TERMINALIZED` | Intent reaches terminal state |

### Benefits

- **Replay entire trading session** — events form a deterministic sequence
- **Rebuild robot state** — replay events to reconstruct exact state at any point
- **Observe exactly what happened** — eliminates most "what happened?" incidents

### Foundation Already in Place

- `commandId` correlates command lifecycle
- Structured logging with `RobotEvents.ExecutionBase`
- IEA command layer with RECEIVED → DISPATCHED → COMPLETED

### Implementation Direction

- Append-only store (e.g. JSONL per instrument or session)
- Monotonic sequence number per event
- Replay consumer that rebuilds state from stream

---

## Improvement 2 — Intent Lifecycle State Machine

**Current state:** Intent state is tracked implicitly through journal, registry, and runtime state.

**Target state:** IEA owns a **formal lifecycle** with explicit states and transitions.

### Proposed States

| State | Meaning |
|-------|---------|
| `CREATED` | Intent registered, no orders yet |
| `SUBMITTED` | Entry order submitted |
| `WORKING` | Entry order working |
| `PARTIALLY_FILLED` | Partial fill |
| `FILLED` | Entry fully filled |
| `PROTECTIVES_ACTIVE` | Stop and target working |
| `TERMINAL` | Intent closed (filled, cancelled, flattened) |

### Benefits

- **Guard commands by state** — `CancelIntentOrders` and `FlattenIntent` only when intent state allows
- **Explicit validation** — reject invalid transitions (e.g. Flatten when already TERMINAL)
- **Clear audit trail** — state transitions are first-class events

### Foundation Already in Place

- Journal tracks execution lifecycle
- OrderRegistry tracks order state
- Runtime intent map in IEA
- Flatten latch, protective submission logic

### Implementation Direction

- Formal state enum and transition rules
- State checks before command execution
- State transition events in execution stream

---

## Relationship

These two improvements reinforce each other:

1. **Event stream** records what happened (observability, replay).
2. **Lifecycle state machine** enforces what is allowed (correctness, guards).

Together they make the robot fully replayable and provably correct.
