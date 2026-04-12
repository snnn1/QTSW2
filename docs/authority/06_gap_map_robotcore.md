# Phase 6 — Gap map vs deployed code (`RobotCore_For_NinjaTrader`)

**Version:** 1.0  
**Status:** Diagnostic (no code changes in this document)  
**Compared to:** Normative docs Phases 1–5

---

## Severity taxonomy

| Severity | Meaning |
|----------|---------|
| **S** — Safety-critical | Wrong/bypassed execution permission or execution-affecting path |
| **T** — Truth-critical | Split or wrong authority; EPA not traceable |
| **F** — Forensic | Reconstruction / run namespace / committed ambiguous |
| **O** — Operator | Visibility only |

---

## Violation register

| ID | Phase rule | Observed behavior | Domain | Sev | Notes |
|----|------------|-------------------|--------|-----|-------|
| V-01 | Phase 2: single EPA | Execution paths spread across `RiskGate`, `ExecutionSafetyGate`, `KillSwitch`, `MismatchEscalationCoordinator`, `RobotEngine._frozenInstruments` | Decision | T | No single named EPA facade in code |
| V-02 | Phase 2: mismatch detection ≠ decision | `ReconciliationRunner` invokes `onQuantityMismatch` → `LogReconciliationQuantityMismatchDiagnostics` only; `MismatchEscalationCoordinator` sets `Blocked` separately | Decision | T | Two parallel “mismatch” semantics |
| V-03 | Phase 1: Market Reality vs journal | Reconciliation compares `GetAccountSnapshot` to journal — aligned; but runner callback does not freeze | Market Reality | O | Intentional log-only; conflicts with “one story” unless EPA absorbs |
| V-04 | Phase 5: truth in run namespace | `RiskLatchManager` uses `_root` (`data/risk_latches`); `RobotEngine.Stop` writes summaries to `_root/data/execution_summaries`; journals may be under `data/playback/{run_id}` | Persistence | F | Split roots |
| V-05 | Phase 5: co-location | `HydrationEventPersister` / `RangeLockedEventPersister` / `RangeBuildingSnapshotPersister` are process singletons — first-init root wins | Persistence | F | Multi-run / isolated playback risk |
| V-06 | Phase 2: all mutations through EPA | Adapter may enqueue flatten / journal writes on paths not clearly centralized | Execution | S | Requires trace per call site |
| V-07 | Phase 4: MVEC | Events spread across JSONL types; no single MVEC enforcement | Forensic | F | |
| V-08 | modules vs RobotCore | `modules/robot/core/RobotEngine.cs` has `RebindRobotLogDirectoryIfNeeded`; `RobotCore_For_NinjaTrader/RobotEngine.cs` may lack it — logs vs state root divergence | Persistence | F | Verify sync on build |
| V-09 | Phase 3: delegation register | No explicit delegation table in code — implicit coordinator authority | Decision | T | |

---

## Dependency map

- **V-01** blocks clean resolution of **V-02** (must unify decision surface).  
- **V-04** / **V-05** block **Phase 5 pass** until roots unified.  
- **V-02** depends on **V-01** (EPA should own mismatch block reason).

---

## Pass criteria (Phase 6)

- Violations named, severitized, dependency-linked.  
- Implementation deferred to Phase 7.
