# Replay Phase 0 — Target and Done Signal

**Purpose:** Define the replay unit, target, and acceptance criteria before implementation. This is the "done" signal for Phase 0 and the north star for Phases 1–7.

**Reference:** IEA_REPLAY_CONTRACT.md, IEA_RUNTIME_DECONSTRUCTION.md

---

## 1. Replay Unit

| Attribute | Definition |
|-----------|------------|
| **Unit** | One IEA instance keyed by `(account, executionInstrumentKey)` |
| **Scope** | Single lane. No concurrent replay. One unit per replay run. |
| **Input** | Ordered event stream for that unit only |
| **Output** | Final IEA snapshot hash + optional emitted events for inspection |

**Implications:**

- Replay file contains events for exactly one `(account, executionInstrumentKey)`.
- Cross-unit events (e.g. different instruments) are out of scope for v1.
- Replay is not a full-engine simulation; it is IEA-state reconstruction only.

---

## 2. Target Definition

**What we are building:**

A deterministic replay harness that:

1. Loads an ordered event stream (JSONL) for one IEA unit.
2. Feeds events into IEA via replay-safe entry points (no NT types).
3. Produces a stable snapshot hash of IEA internal state after processing all events.
4. Optionally emits IEA events (fills, BE triggers, etc.) for inspection.

**What we are NOT building (Phase 0 scope):**

- Full-system replay (broker simulation, EnqueueAndWait, OCO).
- Multi-unit or multi-instrument replay.
- NinjaTrader runtime integration.
- Live trading path changes that alter behavior.

---

## 3. Done Signal — Deterministic Acceptance

Replay is **deterministic** and **done** when all of the following hold:

| Criterion | Requirement |
|-----------|--------------|
| **Hash stability** | Run the same replay file 3 times from clean process start. Snapshot hash must match exactly across all 3 runs. |
| **Type independence** | No reliance on NinjaTrader runtime types. Replay path uses only POCOs and primitive fields. |
| **Process isolation** | Each run is a fresh process (or equivalent: clean static reset). No shared mutable state between runs. |
| **Snapshot scope** | Hash covers: OrderMap, IntentMap, dedup state, BE state, instrument block state. |

**Operational definition of "done":**

```
Run 1: dotnet run -- replay --file events.jsonl --account Sim101 --instrument MNQ  → hash = H1
Run 2: dotnet run -- replay --file events.jsonl --account Sim101 --instrument MNQ  → hash = H2
Run 3: dotnet run -- replay --file events.jsonl --account Sim101 --instrument MNQ  → hash = H3

DONE ⟺ H1 = H2 = H3
```

---

## 4. Snapshot Hash Specification

The snapshot hash is the **determinism oracle**. It must be:

- **Stable:** Same state → same hash, always.
- **Canonical:** Serialization order fixed (e.g. keys sorted, ISO8601 timestamps).
- **Complete:** All branch-relevant state included.

**Snapshot contents (from contract):**

- OrderMap (stable sorted by key)
- IntentMap (stable sorted by key)
- Dedup dictionary (keys + timestamps, event-clock derived)
- BE state fields (per-intent dictionaries sorted)
- Instrument block state

**Hash algorithm:** SHA-256 of canonical JSON.

---

## 5. Phase 0 Checklist

- [x] Replay unit defined: `(account, executionInstrumentKey)`
- [x] Input defined: ordered event stream for that unit
- [x] Output defined: snapshot hash + optional emitted events
- [x] Deterministic acceptance defined: 3 runs, exact hash match
- [x] Type independence stated: no NT runtime types
- [x] Snapshot scope enumerated
- [x] Done signal operationalized (CLI example)

**Phase 0 complete when:** This document is agreed and no ambiguity remains on unit, target, or done signal.

---

*End of Phase 0 Target*
