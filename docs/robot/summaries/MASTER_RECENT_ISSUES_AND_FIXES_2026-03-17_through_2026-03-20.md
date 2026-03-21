# Master summary: issues & fixes (2026-03-17 through 2026-03-20)

**Purpose:** Single **large roll-up** of the main problems surfaced across this week cluster (connectivity, timetable, S1 execution, adoption/restart, reconciliation) and **what was done** for each.  
**Detail:** Each row links to a deeper incident or audit.

**Navigation:** [Summaries hub](README.md) · [Issues index](issues/INDEX.md) · [Fixes index](fixes/INDEX.md)

---

## A. Master table (issue → cause → fix → verify)

| # | Issue (symptom) | Root cause (short) | Fix / mitigation | Where to read / verify |
|---|-----------------|-------------------|------------------|------------------------|
| **1** | **ES1 / NG1 NO_TRADE**, no entry brackets; `NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION` | Bad **`timetable_current.json`** revision applied mid–range-build: **S1 slot_time `07:30`** on ES/NG rows (likely export/merge error; **07:30** is YM1’s real slot). Robot correctly applied directive; “minutes past slot” used wrong boundary. | **Prevention:** `validate_streams_before_execution_write` on all live execution timetable writes; `TimetableEngine._write_execution_timetable_file` guard; dashboard POST guard; **`tools/validate_timetable_execution_file.py`**; tests **`tests/test_timetable_write_guard.py`**. **Ops:** eliminate oscillating/bad writers; run validator on saved JSON. | [2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md](../incidents/2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md), [TIMETABLE_WRITE_PATHS_AUDIT.md](../TIMETABLE_WRITE_PATHS_AUDIT.md) |
| **2** | **ES1 working orders cancelled** ~10 min after submit; `UNOWNED_ENTRY_RESTART` → flatten | Strategy **restart / new `run_id`** → **IEA registry empty**; broker still had QTSW2 orders. Restart scan did not **adopt** into registry before classifying **UNOWNED**. | **Adoption hardening (see rows 3–5)**; **`TryRecoveryAdoption`** on reconciliation path (already present); ops: stable **`QTSW2_PROJECT_ROOT`**. | [2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md](../incidents/2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md) |
| **3** | ES1 “**didn’t remember**” orders (registry vs broker) | Same as **#2**, plus **deferral bug**: adoption deferral used **scan count + wall time**; many NT **execution updates/sec** exhausted cap in **&lt;1s** → false “grace expired” → UNOWNED. | **`AdoptionDeferralDecision`**: defer on **wall-clock only** (removed scan-count gate from decision). **`DelayedJournalVisibilityTests`**: `TestManyRapidScansDoNotForceUnowned`. | [2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md](../incidents/2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md) |
| **4** | Adoption candidates **empty** though journals exist | **Instrument string mismatch**: journal `Instrument` like **`MES 03-26`** vs execution key **`MES`** → strict equality failed. | **`JournalInstrumentMatchesExecutionKey`** / normalize root symbol (strip after first space) in **`ExecutionJournal`** (RobotCore + modules copy). | Same as #3 |
| **5** | **ES2** “no orders when broker started” | **By design:** ES2 submits at **its slot / range lock**, not broker connect. **Coupling:** shared **MES** IEA — ES1 **flatten / recovery / NO_TRADE** blocks **all** MES streams until cleared. | Same adoption + timetable fixes reduce spurious flatten; check logs: `NO_TRADE`, `STAND_DOWN`, `UNOWNED_*`, `FLATTEN_*`. | Same as #3 |
| **6** | **`RECONCILIATION_QTY_MISMATCH`** / journal qty wrong with partial exits | Open journal quantity summed **entry fills** without subtracting **exits**. | **`GetOpenJournalQuantitySumForInstrument`** uses **remaining** = `EntryFilledQuantityTotal - ExitFilledQuantityTotal` (clamped). | [2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md](../incidents/2026-03-17_YM1_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md) |
| **7** | **`ORDER_REGISTRY_MISSING`** / broker working vs local | Empty IEA registry after restart; wrong “local working” source in reconciliation logic (historical). | **Local working** from **IEA** `GetOwnedPlusAdoptedWorkingCount`; **recovery adoption** attempt before fail-closed; adoption fixes above. | `RobotEngine` reconciliation section (comments + logs), [2026-03-16_ORDER_REGISTRY_MISSING_INVESTIGATION.md](../incidents/2026-03-16_ORDER_REGISTRY_MISSING_INVESTIGATION.md) |
| **8** | **Connection loss / disconnect**; strategy stop; orders at risk | NinjaTrader / network / session; prior incidents tied to **07:30** window and long outages. | Platform/stability (outside code); robot: **adoption** + **journal path** reduce secondary damage on reconnect; see disconnect reports for timeline. | [2026-03-19_INCIDENT_REPORT_0730_DISCONNECT.md](../incidents/2026-03-19_INCIDENT_REPORT_0730_DISCONNECT.md), [2026-03-19_DISCONNECT_DEEP_INVESTIGATION_COMPLETE.md](../incidents/2026-03-19_DISCONNECT_DEEP_INVESTIGATION_COMPLETE.md), [2026-03-17_INCIDENT_REPORT_CONNECTION_LOSS_RESTART_ORDERS_DELETED.md](../incidents/2026-03-17_INCIDENT_REPORT_CONNECTION_LOSS_RESTART_ORDERS_DELETED.md) |
| **9** | **M2K** adopted-order fill / ownership | Fill routing after restart adoption. | Targeted fix documented in incident (see link). | [2026-03-17_M2K_ADOPTED_ORDER_FILL_FIX.md](../incidents/2026-03-17_M2K_ADOPTED_ORDER_FILL_FIX.md) |
| **10** | **Cross-stream blast radius** on shared execution instrument | Single IEA + symbol-level recovery/flatten/reconciliation freeze. | **P2 Phase 1 (2026-03-20):** `RecoveryOwnershipAttributionEvaluator`, `CanEscalateToInstrumentScopedRecovery`, `STREAM_SCOPED_CONTAIN`, `StandDownSingleStreamForOwnershipAmbiguity`, reconciliation `HandleReconciliationQtyMismatchP2`, aggregation guard `AGGREGATION_CANCEL_BLOCKED_DUE_TO_ATTRIBUTION`. Tests: `--test RECOVERY_P2_PHASE1`. | `modules/robot/contracts/P2RecoveryOwnershipAttribution.cs`, `InstrumentExecutionAuthority.RecoveryPhase3.cs`, `NinjaTraderSimAdapter.NT.cs`, `RobotEngine.cs`, [IEA_ARCHITECTURE_AND_FAILURE_MODE_AUDIT.md](../audits/IEA_ARCHITECTURE_AND_FAILURE_MODE_AUDIT.md) |
| **11** | **Account-wide QTSW2 cancel** before flatten (cross-symbol blast radius) | `CancelRobotOwnedWorkingOrdersReal` iterated all account orders. | **P2.6 (2026-03-20):** instrument-scoped cancel (+ optional explicit id set + opt-in account fallback); **`DestructiveActionPolicy`** + **`NtFlattenInstrumentCommand`** metadata; strict emergency prefixes; **`DESTRUCTIVE_ACTION_*`** / **`CANCEL_SCOPE_*`** logs; bootstrap flatten gated in **`ExecuteRecoveryFlatten`**. Tests: **`--test P2_6_DESTRUCTIVE_POLICY_TESTS`**. | `DestructiveActionPolicy.cs`, `StrategyThreadExecutor.cs` (`NtFlattenInstrumentCommand`), `NinjaTraderSimAdapter.NT.cs`, `InstrumentExecutionAuthority.RecoveryPhase3.cs`, `RobotEngine.cs`, [IEA_ARCHITECTURE_AND_FAILURE_MODE_AUDIT.md](../audits/IEA_ARCHITECTURE_AND_FAILURE_MODE_AUDIT.md) § P2.6 |

---

## B. Code / tool touchpoints (quick reference)

| Area | Files / commands |
|------|------------------|
| Adoption deferral | `modules/robot/core/Execution/AdoptionDeferralDecision.cs` |
| IEA scan + grace | `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` (`RestartAdoptionGraceSeconds`, `ScanAndAdoptExistingOrders`) |
| Journal matching | `ExecutionJournal.cs` (RobotCore + `modules/robot/core/Execution/`) |
| Heartbeat → retry adoption | `RobotEngine.cs` (`TryRetryDeferredAdoptionScan` on timer) |
| Timetable guard | `modules/timetable/timetable_write_guard.py`, `timetable_engine.py`, `modules/dashboard/backend/main.py` |
| Validate timetable JSON | `python tools/validate_timetable_execution_file.py data/timetable/timetable_current.json` |
| Timetable guard tests | `pytest tests/test_timetable_write_guard.py` |
| Adoption / gate unit tests (harness) | `dotnet run --project modules/robot/harness/Robot.Harness.csproj` — `--test DELAYED_JOURNAL_VISIBILITY`, `--test MISMATCH_ESCALATION`, `--test STATE_CONSISTENCY_GATE`, **`--test RECOVERY_P2_PHASE1`**, **`--test P2_6_DESTRUCTIVE_POLICY_TESTS`** |
| **P2 Phase 1 stream-scoped recovery** | `P2RecoveryOwnershipAttribution.cs`, `RecoveryPhase3DecisionRules` (`RecoveryPhase3Types.cs`), `InstrumentExecutionAuthority.NT.cs` (aggregation guard), `NinjaTraderSimAdapter.NT.cs` (`OnRecoveryRequested`), `RobotEngine` / `NT_ADDONS/RobotEngine` |
| **P2.6 destructive path closure** | `DestructiveActionPolicy.cs`, `NtFlattenInstrumentCommand` (`StrategyThreadExecutor.cs`), `NinjaTraderSimAdapter.NT.cs` (`ExecuteFlattenInstrument`, `CancelRobotOwnedWorkingOrdersReal`), `InstrumentExecutionAuthority.RecoveryPhase3.cs` (`ExecuteRecoveryFlatten`, `RecoveryFlattenNtMetadata`), `RobotEngine.cs` (`HandleReconciliationQtyMismatchP2`) |
| Project root for journals | `QTSW2_PROJECT_ROOT` env + `ProjectRootResolver.cs` |
| **P1 / P1.5 State-consistency gate** (2026-03-17+) | **Closed-loop gate (P1.5):** explicit **`GateLifecyclePhase`** (`DetectedBlocked` → `Reconciling` → `StablePendingRelease` → release); **`StateConsistencyReleaseEvaluator.Evaluate`** defines release (explainable broker position + working + coherent local state, **no pending adoption**, no unexplained risk); **continuous stability window** (`STATE_CONSISTENCY_STABLE_WINDOW_MS_LIVE` 10s / **SIM** 12s) before **`STATE_CONSISTENCY_GATE_RELEASED`**; **`STATE_CONSISTENCY_GATE_RESTABILIZATION_RESET`** if invariants fail during window; **`ReconciliationRunner.ForceRunGateRecoveryForInstrument`** = **GATE_RECOVERY** (skips qty-mismatch freeze + destructive orphan close for that instrument); **`RunInstrumentGateReconciliation`** on **`RobotEngine`** runs gate pass + **`TryRecoveryAdoption`**; lifecycle events: **`STATE_CONSISTENCY_GATE_*`** (engaged, reconciliation started/result, stable pending, released, restabilization reset, persistent mismatch, recovery failed). **No auto-release** from **`PERSISTENT_MISMATCH` / FAIL_CLOSED**. Policy: **`StateConsistencyGateActionPolicy`**. Tests: `--test STATE_CONSISTENCY_GATE` + **`MismatchEscalation`**. Files: `StateConsistencyGateModels.cs`, `MismatchEscalationCoordinator.cs`, `MismatchEscalationModels.cs`, `ReconciliationRunner.cs`, `RobotEngine.cs`, `NT_ADDONS/RobotEngine.cs`, `modules/robot/core` parity. |

---

## C. Operational checklist after deploy

1. Set **`QTSW2_PROJECT_ROOT`** to repo root on the NinjaTrader host.  
2. After any manual timetable edit: run **`validate_timetable_execution_file.py`**.  
3. On incident: grep logs for **`ADOPTION_SCAN_START`**, **`ADOPTION_DEFERRED_CANDIDATES_EMPTY`**, **`ADOPTION_SUCCESS`**, **`UNOWNED_ENTRY_RESTART`**, **`TIMETABLE_UPDATED`**, **`DIRECTIVE_UPDATE_APPLIED`**, **`NO_TRADE`**.  
4. Rebuild/deploy **`Robot.Core`** DLL after NT-side changes.

---

## D. Related roll-ups

- **2026-03-12 session:** [2026-03-12_FULL_ISSUES_AND_FIXES_SUMMARY.md](../incidents/2026-03-12_FULL_ISSUES_AND_FIXES_SUMMARY.md)  
- **Replay packs (IEA):** [INCIDENT_PACKS_SUMMARY.md](../incidents/INCIDENT_PACKS_SUMMARY.md)  

---

*If “today” means a single calendar day, narrow this doc to the rows whose incident date matches; the hub above stays the canonical index.*
