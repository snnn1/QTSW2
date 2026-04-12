# Phase 1 — Authority model (locked)

**Version:** 1.0  
**Status:** Normative for the Authority Model Lock Program  
**Scope:** Truth ownership, Market Reality Snapshot, tie-breaks, non-authoritative artifacts

---

## 1. Execution-history window contract (hard rule)

**Normative rule (single anchor):**

**Executions / fills in scope for Market Reality classification and reconciliation are those observed on or after the current engine run boundary — i.e. since successful `RobotEngine.Start()` for the active `run_id`.**

- **Anchor:** `run_id` issued at `Start()` (or environment override `QTSW2_RUN_ID` when applied before persistence bind, per engine semantics).  
- **Excludes:** Fill/execution records that exist only in historical files or prior runs without being re-validated through the current run’s adapter ingress.  
- **Ingress:** Broker/platform execution callbacks and `GetAccountSnapshot`-consistent state for positions and working orders.  

**Forbidden:** Using the word “recent” without referencing this run boundary; using **different** execution windows in different subsystems for the same decision class.

**Rationale:** Run boundary is inspectable, aligns with Phase 5 run namespace, and matches operational questions (“what happened this run?”).

---

## 2. Market Reality Truth (Market Reality Snapshot)

**Definition:** The unified broker-side **current** state used to compare against internal ledgers:

| Component | Included | Authority |
|-----------|----------|-----------|
| Positions | Net/abs exposure per instrument from broker | Latest successful `AccountSnapshot` (or equivalent adapter snapshot) |
| Working orders | Resting/active orders scoped to reconciliation | Same snapshot |
| Executions in scope | Per §1 run-boundary rule | Callback ingress + snapshot-derived consistency checks |

**Tie-break vs internal state:** **Market Reality wins** for “what the world is” when classifying broker-vs-journal mismatch, **closure/repair eligibility** when broker is flat in scope, and similar **reconciliation** decisions.

**Update sources:** Adapter callbacks; explicit `GetAccountSnapshot` pulls on reconciliation and realtime transitions.

---

## 3. Authority matrix

| Truth domain | Primary owner | Secondary evidence | Never authoritative |
|--------------|---------------|-------------------|---------------------|
| **Market Reality** (positions, working orders, in-scope executions) | Broker snapshot + run-bounded execution ingress | ExecutionJournal last-known fills (for correlation) | Robot JSONL, health sink, canonical JSONL, hydration/range side files |
| **Intent / execution ledger** | `ExecutionJournal` (per-intent JSON under state root) | IEA in-memory registry (ephemeral) | Logs, canonical JSONL |
| **Stream / slot state** | `JournalStore` stream journal (`StreamJournal` persisted) | In-memory `StreamStateMachine` while running | Logs, range/hydration JSONL alone |
| **Control / permission** | Kill switch file; recovery guard (`IExecutionRecoveryGuard`); risk latch files; engine frozen sets | Health monitor notifications | Logs alone |
| **Reconciliation outcome (derived)** | Outcome of `ReconciliationRunner` + policy | Mismatch coordinator state | Raw `RECONCILIATION_*` log lines without journal/snapshot |
| **Operator narrative** | N/A (no decision authority) | `RobotLoggingService` / health sink | N/A |

---

## 4. Tie-break matrix

| Conflict | Winner | Rule | Escalation |
|----------|--------|------|------------|
| Market Reality vs ExecutionJournal open qty | **Market Reality** for broker-flat / working-order facts; journal updated to align when closure rules say so | ReconciliationRunner uses snapshot vs journal | MismatchEscalationCoordinator / policy per Phase 2–3 |
| Market Reality vs in-memory adapter cache | **Market Reality** (fresh snapshot) | Prefer snapshot on reconciliation pass | Log and hold if snapshot unavailable |
| Stream journal vs SSM memory | **Persisted stream journal** after successful `Save`; on load, journal seeds memory | `TryLoad` then SSM | Corruption → fail-closed stand-down per engine |
| Timetable file vs `_eligibleSet` | **In-memory set** after last successful `ApplyTimetable` | File is input; set is authority until reload | Reload on poll |
| Kill switch file unreadable | **Enabled (fail-closed)** | `KillSwitch` implementation | Operator fixes file |
| Risk latch file vs `_frozenInstruments` | **Both:** hydrate merges file into runtime set; explicit unfreeze clears both | `RiskLatchManager` + engine | See Phase 3 |

---

## 5. What is never authoritative

The following **must not** be the sole basis for permission to trade, submit/cancel/flatten, or journal repair:

- Primary robot JSONL (`RobotLoggingService` output)
- Health sink JSONL
- Emergency / fallback logger files
- Canonical execution JSONL (`ExecutionEventWriter`) — unless Phase 4 elevates specific events (still not permission **by itself**)
- Hydration / range locked / range building append-only files
- Execution incident JSON (except as human audit)
- Daily markdown summaries
- Strategy lifecycle trace log
- **Any** log line without backing journal or broker snapshot

---

## 6. Pass criteria (Phase 1)

- Every major question (position, intents, stream committed, permission, recovery) maps to **one primary row** above.  
- Every listed conflict has **one winner**.  
- Execution-history scope is **run-bounded** per §1 — no soft “recent.”
