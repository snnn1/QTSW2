# Fixes index (remediations catalog)

Maps **problem themes** to **implemented or documented fixes**. Full narratives remain in incident docs and audits.

---

## Adoption / restart / IEA registry

| Fix | Problem addressed | Code / docs |
|-----|-------------------|-------------|
| **Time-only adoption deferral** | Burst NT execution updates exhausted scan-count cap → false `GraceExpiredUnowned` → UNOWNED → flatten. | `modules/robot/core/Execution/AdoptionDeferralDecision.cs`; `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` |
| **60s restart grace** | Journals / path resolution need more wall time before UNOWNED. | `RestartAdoptionGraceSeconds` in `InstrumentExecutionAuthority.NT.cs` |
| **Journal instrument normalization** | `MES 03-26` vs `MES` string mismatch → zero adoption candidates. | `ExecutionJournal.cs` (RobotCore + `modules/robot/core`) — `JournalInstrumentMatchesExecutionKey` |
| **Deferred adoption retry on heartbeat** | Retries not only on execution stream. | `RobotEngine` → `TryRetryDeferredAdoptionScan`; harness tests `DELAYED_JOURNAL_VISIBILITY` |
| **Reconciliation recovery adoption** | `ORDER_REGISTRY_MISSING` before fail-closed. | `RobotEngine` reconciliation path → `TryRecoveryAdoption()` |

**Write-up:** [2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md](../incidents/2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md)

---

## Timetable / NO_TRADE / execution file integrity

| Fix | Problem addressed | Code / docs |
|-----|-------------------|-------------|
| **validate_streams_before_execution_write** | Bad rows (e.g. ES1 S1 `07:30`) written to live timetable. | `modules/timetable/timetable_write_guard.py`, `timetable_engine.py`, dashboard `main.py` |
| **CLI validation tool** | Operator / CI check of `timetable_current.json`. | `tools/validate_timetable_execution_file.py` |
| **Unit tests** | Guard behavior locked. | `tests/test_timetable_write_guard.py` |

**Write-up:** [TIMETABLE_WRITE_PATHS_AUDIT.md](../TIMETABLE_WRITE_PATHS_AUDIT.md), [2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md](../incidents/2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md)

---

## Reconciliation / journal quantity

| Fix | Problem addressed | Code / docs |
|-----|-------------------|-------------|
| **Open qty uses remaining** | Partial exits: `journal_qty` overstated vs broker. | `ExecutionJournal.GetOpenJournalQuantitySumForInstrument` — remaining = entry − exit fills |
| **IEA local working from registry** | `ORDER_REGISTRY_MISSING` false logic using wrong “local” source. | `RobotEngine` / `GetOwnedPlusAdoptedWorkingCount` path (see reconciliation comments) |

**Write-up:** [2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md](../incidents/2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md)

---

## Operations / environment

| Fix | Problem addressed | Docs |
|-----|-------------------|------|
| **`QTSW2_PROJECT_ROOT`** | Wrong cwd → journals not where adoption reads. | `ProjectRootResolver.cs`; adoption investigation doc |

---

## How to update this file

When you merge a fix, add a row under the right theme (or create a theme). Link the PR or commit in the **Code** column when helpful.
