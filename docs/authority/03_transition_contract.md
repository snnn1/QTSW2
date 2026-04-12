# Phase 3 — Transition contract

**Version:** 1.0  
**Status:** Normative  
**Related:** [01_authority_model.md](01_authority_model.md), [02_decision_authority.md](02_decision_authority.md)

---

## 1. Delegation register

**Policy:** Delegation is **rare**; default **decision owner = EPA** ([02_decision_authority.md](02_decision_authority.md)). Only rows listed here may decide instead of EPA for the named scope.

| Delegate | Allowed transition types | Scope | Max authority | Observable chain |
|----------|-------------------------|-------|---------------|------------------|
| *(empty)* | — | — | — | No delegation in v1.0 normative model |

**Rule:** Any future delegate MUST be added as a row here before implementation claims delegated decision rights.

---

## 2. Rules for “committed” (stream / slot)

**Official committed terminal state** exists when:

1. `StreamJournal.Committed == true` with non-null `CommitReason` (or equivalent fields), **and**  
2. `JournalStore.Save` has completed successfully for `{trading_date, stream}` (atomic temp+move semantics).

Until both hold, the stream is **not** officially committed for forensic reconstruction.

---

## 3. Allowed lag

| Artifact | May lag behind truth? | Max lag / rule |
|----------|------------------------|----------------|
| Robot JSONL / health | Yes | Async queue + flush interval; **never** used to infer permission |
| Canonical execution JSONL | Yes | Emit-after-action; not permission authority |
| Stream journal on disk | **No** for commit — commit is official only after `Save` |
| Broker snapshot | Bounded | Staleness acceptable only within reconciliation throttle window; documented in ops |

---

## 4. Transition table

Columns: **Trigger** | **Cause owner** | **Decision owner** | **Effect owner** | **State change** | **Persistence** | **Log side effects** | **Official when**

| Transition | Trigger | Cause owner | Decision owner | Effect owner | State change | Persistence | Logs (narrative) | Official when |
|------------|---------|-------------|----------------|--------------|--------------|-------------|------------------|---------------|
| Fill received | Execution callback | Adapter / IEA | EPA (implicit allow path) | `NinjaTraderSimAdapter` + `ExecutionJournal` | Journal rows updated | `ExecutionJournal` file | `RobotLogger` | Journal write durable |
| Mismatch detected | Reconciliation pass | `ReconciliationRunner` | EPA | N/A (detection only in current code; EPA should log decision) | None if detect-only | Optional | `RECONCILIATION_*` | Observation timestamp |
| Mismatch escalated | Coordinator threshold | `MismatchEscalationCoordinator` | **EPA** | Coordinator applies block | Block flag | In-memory + Phase 5 | Events | Block flag set |
| Recovery entered | Connection loss | Connection layer | EPA via recovery guard | Engine | `_recoveryState` | N/A | ENGINE events | State variable |
| Recovery exited | Connection OK | Connection layer | EPA | Engine | `_recoveryState` | N/A | ENGINE events | State variable |
| Stream committed (terminal) | SSM commit path | `StreamStateMachine` | EPA (had to allow trades leading up) | SSM + `JournalStore` | Terminal `StreamJournal` | `JournalStore.Save` | Stream events | After Save |
| Flatten triggered | Operator / policy / emergency | Varies | **EPA** | Adapter enqueue | Order state | Journal + adapter | CRITICAL | Broker confirms or journal records |
| Trading blocked | Gate failure | Detector | **EPA** | RiskGate / engine | `deny` | N/A | `GLOBAL_KILL_SWITCH_*` etc. | Return from EPA |
| Trading unblocked | Gate clear | EPA / explicit unfreeze | **EPA** | Engine + `RiskLatchManager.Clear` | Frozen cleared | Latch file | `INSTRUMENT_UNFROZEN_*` | After Clear + EPA allow |
| Orphan fill | Fill without intent | Adapter | **EPA** | Adapter block path | Block new risk | Incident file | `ORPHAN_FILL_*` | Incident + journal markers per code |
| Journal repair | Broker-ahead repair | `ReconciliationRunner` | **EPA** | Adapter `TryRepair*` | Journal rows | `ExecutionJournal` | ENGINE | Journal upsert durable |
| Explicit unfreeze | Operator API | Operator | **EPA** | `RobotEngine.TryUnfreezeInstrument` | Frozen remove | Latch clear | Event | `TryUnfreezeInstrument` returns true |

---

## 5. Cross-reference: Phase 2 repair delegation

Reconciliation **repair** actions that mutate journal or broker MUST appear as **EPA-authorized** in implementation; until delegation rows exist, **EPA is the decision owner** for authorizing those mutations (implementation may centralize EPA inside adapter **only if** adapter is the sole mutation choke point — Phase 6 tracks violations).

---

## 6. Pass criteria (Phase 3)

- Every row has **cause**, **decision**, **effect** owners.  
- **Decision owner** is **EPA** or a **registered delegate** (register currently empty).  
- “Committed” and “allowed lag” are defined above.
