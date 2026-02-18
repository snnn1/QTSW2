# Instrument Execution Authority (IEA) — Specification

Institution-proof specification for the per-instrument execution authority.

---

## What It Is

**Instrument Execution Authority (IEA)** is a per-instrument execution controller that serializes all broker-state mutations (fills, break-even, protectives, flatten) for a given (account, execution instrument). It replaces per-chart order tracking with a single authority per instrument.

---

## Purpose

1. **Single mutation lane** — All broker state changes for an instrument go through one queue.
2. **Multi-chart safety** — Multiple charts on the same instrument share one authority and avoid races.
3. **Aggregation** — Multiple intents with the same stop price can be combined into one broker order. Aggregation is policy-gated by `require_identical_bracket` and `bracket_tolerance_ticks`, and rejects wrong-side stops.
4. **Deterministic ordering** — FIFO processing for audit and replay.

---

## Architecture

### Registry

- **Key:** `(accountName, executionInstrumentKey)` — e.g. `("Sim101", "MNQ")`.
- **Scope:** One IEA per instrument per account.
- **Location:** `InstrumentExecutionAuthorityRegistry.GetOrCreate()`.

### Execution Instrument Key

- **Source:** `ExecutionInstrumentResolver.ResolveExecutionInstrumentKey()`.
- **Prefer:** `engineExecutionInstrument` from the engine.
- **Fallback:** Instrument name (e.g. MNQ, MGC).
- **Invariant:** MNQ and NQ are distinct keys; no cross-authority merging.

### Instrument Matching (Not Keying)

- **`IsSameInstrument(a, b)`** — Treats NQ/MNQ, ES/MES, etc. as the same market.
- **Use:** Diagnostic and order association only — e.g. matching orders to intents when `order.Instrument.MasterInstrument.Name` differs from `orderInfo.Instrument`.
- **Never use for:** State ownership, IEA registry keying, or authority scoping. This prevents accidental cross-market coupling.

---

## Serialization Model

### Queue

- **Type:** `BlockingCollection<Action>` with one worker thread per IEA.
- **Worker:** `WorkerLoop` processes work in FIFO order.
- **Thread name:** `IEA-{ExecutionInstrumentKey}-{instanceId}`.

### Routed Through IEA

| Operation | Path |
|-----------|------|
| **Execution updates** (fills, partials) | `HandleExecutionUpdate` → `_iea.EnqueueExecutionUpdate` → worker → `HandleExecutionUpdateReal` |
| **Order updates** | `HandleOrderUpdate` → `_iea.EnqueueOrderUpdate` → worker |
| **BE evaluation** | `EvaluateBreakEven` → `Enqueue` → worker → `EvaluateBreakEvenCore` |
| **Entry aggregation** | `SubmitStopEntryOrderReal` → `EnqueueAndWait` → worker → `SubmitStopEntryOrder` |
| **Single-order fallback** | `SubmitStopEntryOrderReal` → `EnqueueAndWait` → worker → `SubmitSingleEntryOrderCore` |
| **Flatten** | `FlattenWithRetry` → `EnqueueFlattenAndWait` → worker |
| **Protective submission** | Via `HandleEntryFill` on worker |

---

## Configuration

**`configs/execution_policy.json`:**

```json
{
  "use_instrument_execution_authority": true,
  "aggregation": {
    "bracket_policy": { "stop": "TIGHTEST", "target": "TIGHTEST" },
    "require_identical_bracket": true,
    "bracket_tolerance_ticks": 1
  },
  "break_even": {
    "trigger_policy": "FIRST_TO_TRIGGER",
    "stop_price_policy": "TIGHTEST"
  },
  "canonical_markets": { ... }
}
```

- **TIGHTEST:** Long = highest stop below entry; short = lowest stop above entry.
- **FIRST_TO_TRIGGER:** First intent to reach threshold moves the stop for the net position.

---

## EnqueueAndWait Safety

- **Deadlock guard:** If called from the worker thread, runs inline.
- **Timeout:** 5s default; returns `(false, default)` and logs `IEA_ENQUEUE_AND_WAIT_TIMEOUT`.
- **Queue overflow:** Max depth 500; logs `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW`.
- **Fail-closed:** On timeout or overflow, `_instrumentBlocked = true`; no new work until restart. Blocking rejects all enqueue paths, including entry submission and BE evaluation, until restart.
- **Callback:** `SetOnEnqueueFailureCallback` → `StandDownStreamsForInstrument` to stand down streams.

---

## Adoption & Hydration

- **Hydration scan:** Runs once before the first execution update.
- **Adoption:** `ScanAndAdoptExistingProtectives()` adopts existing QTSW2 protectives that match intents.
- **Tag format:** `QTSW2:{intentId}:STOP` / `QTSW2:{intentId}:TARGET`.
- **Validation:** Only QTSW2-tagged orders; intent must be in journal; both stop and target required.
- **Mismatch:** `PROTECTIVE_QUANTITY_MISMATCH_FAIL_CLOSE` or `PROTECTIVE_MISMATCH_FAIL_CLOSE` when prices/quantities differ beyond tolerance.

---

## Observability

| Event | Purpose |
|-------|---------|
| `IEA_HEARTBEAT` | Periodic (60s) with queue depth, sequence numbers |
| `IEA_ENQUEUE_AND_WAIT_TIMEOUT` | Worker timeout |
| `IEA_ENQUEUE_AND_WAIT_QUEUE_OVERFLOW` | Queue overflow |
| `IEA_ENQUEUE_REJECTED_INSTRUMENT_BLOCKED` | Work rejected after prior failure |
| `IEA_QUEUE_WORKER_ERROR` | Worker exception |

Logs include `iea_instance_id` and `execution_instrument_key` for tracing.

---

## Rollback

- **Disable:** `use_instrument_execution_authority: false` in `execution_policy.json`.
- **Effect:** Adapter uses its own maps and `EvaluateBreakEvenNonIEA`; legacy behavior restored.

---

## Remaining Maturity Items

- No continuous broker reconciliation loop (periodic ledger vs broker compare)
- No deterministic replay harness
- No portfolio-level risk governor
- Formal state machine spec not yet extracted

---

## Related Files

| File | Role |
|------|------|
| `InstrumentExecutionAuthority.cs` | Core queue, EnqueueAndWait, registry binding |
| `InstrumentExecutionAuthority.NT.cs` | Aggregation, adoption, BE, protectives |
| `InstrumentExecutionAuthorityRegistry.cs` | Per-(account, instrument) IEA registry |
| `ExecutionInstrumentResolver.cs` | Execution key resolution and `IsSameInstrument` |
| `IIEAOrderExecutor` | Adapter interface for NT order operations |
| `OrderInfo.cs` | Per-order tracking (intent, state, protectives) |
