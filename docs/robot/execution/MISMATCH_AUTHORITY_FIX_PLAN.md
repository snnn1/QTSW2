# Mismatch / reconciliation authority fix plan

This document turns the strategic phases into **codebase-anchored** work: files, responsibilities, and order of operations. It supersedes ad hoc “reconciliation is broken” threads with one roadmap.

**Already applied (Phase 2 slice):** `StateConsistencyReleaseEvaluator.Evaluate` no longer treats **legacy classifier gap** (`PendingAdoptionCandidateCount` with no classifier blockers) as a **structural release veto**. It still sets `LegacyClassifierGap`, logs contradiction `info_legacy_classifier_gap_pending_adoption`, and may set `PendingAdoptionExists` from adoptable count — but **only resolved classifier decisions** (`ADOPT` / `RETRY` / `ESCALATE` / `ALTERNATE_LANE`) hold release. See `system/modules/robot/core/Execution/StateConsistencyGateModels.cs`.

---

## Phase 1 — Authority cleanup

**Goal:** One authoritative source for **execution block** and **release**, with everything else in supporting roles.

**Recommended split (aligns with existing docs):**

| Concern | Role | Primary symbols |
|--------|------|-----------------|
| **Canonical family exposure + pending-fill / adoption reconciliation** | **Authority** for block/release convergence | `ReconciliationRunner` (family view), `BrokerPositionResolver`, pending-fill bridge (`AssembleMismatchObservations`), IEA adoption (`TryScheduleRecoveryAdoptionScan`) |
| **Parity view** | **Integrity monitor** only — not sole escalation authority | `JournalParityChecker.CheckJournalParity` (`JournalIntegrityGuarantee.cs`) |
| **Mismatch-sweep** | **Suspicion, diagnostics, escalation candidate** — must not be the only hard authority | `RobotEngine.AssembleMismatchObservations` → `MismatchObservation` → `MismatchEscalationCoordinator` |

**Work items**

1. **Document the authority chain** in `docs/robot/contracts/BROKER_QUANTITY_VIEWS.md` (append “authority” column: advisory vs enforcement).
2. **Refactor `MismatchEscalationCoordinator.ProcessMismatchPresent`** so first-line **G1 / `MISMATCH_EXECUTION_BLOCK`** requires a **canonical unexplained exposure** signal (family-aligned + IEA/journal), not raw sweep alone — or route sweep through a single `CanonicalExposureEvaluator` used by both runner and coordinator.
3. **Keep `RiskGate` split** (RobotCore): `IsMismatchExecutionAuthorityBlocked` vs `IsInstrumentFrozenSupervisoryProtectiveOnly` (`RobotEngine`, `RiskGate.cs`).

---

## Phase 2 — Model cleanup (release)

**Goal:** Classifier-backed blockers are authoritative; legacy counts are informational.

**Done:** Legacy gap no longer adds `hasStructuralBlocker` in `StateConsistencyReleaseEvaluator.Evaluate`.

**Remaining**

1. **Engine input builder** `BuildStateConsistencyReleaseEvaluationInput` — ensure classifier diagnostics are populated whenever `PendingAdoptionCandidateCount > 0` in steady state (reduce `LegacyClassifierGap` frequency).
2. **Tests:** `ReconciliationContractRefactorTests.Case4_LegacyClassifierGapInformational` — run harness `RECONCILIATION_CONTRACT_REFACTOR` when `Robot.Harness` builds.
3. **Audit any other “legacy count” paths** that gate release (grep `PendingAdoptionCandidateCount`, `LegacyClassifierGap`).

---

## Phase 3 — Convergence contract

**Goal:** Formal **post-event convergence** windows where non-canonical mismatch evidence cannot open a new hard block.

**Implemented (minimal slice, 2026):**

- Per-instrument state in `MismatchEscalationCoordinator`: `_convergenceByInstrument` (`MismatchConvergenceEntry`: cause, `ArmedAtUtc`, `ExpiresAtUtc`). TTL: `MismatchEscalationPolicy.MISMATCH_CONVERGENCE_WINDOW_MS` (4500 ms).
- **Arm:** `ArmConvergence(instrument, cause, utcNow)` — public; called from `RobotEngine` when `ReconciliationScheduleSignals.AdoptionWorkOrQueueInflight` after recovery adoption scan scheduling.
- **Execution fill / protective transition:** `NotifyExecutionTrigger` arms when `MismatchExecutionTriggerDetails.FillDelta != 0` or `EntryToProtectivesTransition` (causes `execution_fill`, `protective_transition`, or combined).
- **Canonical escape hatch:** optional delegate `probeCanonicallyUnexplainedExposure` → `MismatchConvergenceCanonicalProbeResult`. Engine wires `ProbeMismatchConvergenceCanonicalExposure` (uses `EvaluateStateConsistencyReleaseReadiness` — unexplained position/working + explainability flags). If probe reports unexplained exposure, **first escalation is not suppressed**. If probe is null, suppression is disabled (fail-safe).
- **Serious mismatch types** never suppressed: `MismatchConvergenceEscalationPolicy.IsSeriousMismatchType` (`NET_POSITION_MISMATCH`, `STRUCTURAL_MULTI_INTENT`, `UNCLASSIFIED_CRITICAL_MISMATCH`, `LIFECYCLE_BROKER_DIVERGENCE`, `UNKNOWN_EXECUTION_PERSISTENT`).
- **First-detect path only:** `ProcessMismatchPresent` when `EscalationState == NONE`; suppression skips setting `DETECTED` / block / gate engage. Emits `RECONCILIATION_MISMATCH_FIRST_ESCALATION_SUPPRESSED_FOR_CONVERGENCE` with `convergence_state`, `convergence_cause`, `convergence_expires_at_utc`, `suppression_reason`, probe fields.
- **Sync:** `RobotCore_For_NinjaTrader/Execution/MismatchEscalationModels.cs` must stay aligned with `modules/.../MismatchEscalationModels.cs` (NT csproj links some modules files but uses a **local copy** of this file).

**Tests:** `MismatchConvergenceContractTests` — harness `--test MISMATCH_CONVERGENCE_CONTRACT`.

**Remaining (later)**

- Explicit reconnect/bootstrap arming (separate from adoption).
- Optional `SuppressHardJournalIntegrityActions` as an additional arm signal if product wants cancel/replace parity windows.

---

## Phase 4 — Episode containment

**Goal:** One active mismatch episode = **one cause**, **one authoritative model**, **one release condition**. Other detectors attach **evidence**, they do not re-trigger the episode.

**Rules**

1. **Single episode key:** `(account, instrument family)` or execution root as already used in `ReconciliationStateTracker` — extend to coordinator so `DETECTED` → `PERSISTENT` does not reset on unrelated observation types.
2. **Attach-only events:** `RECONCILIATION_ORDER_SOURCE_BREAKDOWN`, `RECONCILIATION_IEA_*`, parity pending — log under same `episode_id` without bumping `FirstDetectedUtc`.
3. **Release:** Only the **canonical release evaluator** clears the episode when `ReleaseReady` + stability window satisfied (`AdvanceStateConsistencyGate`).

**Touchpoints:** `MismatchEscalationCoordinator` state, `ReconciliationStateTracker`, optional `episode_id` in payloads.

---

## Phase 5 — Observability cleanup

**Goal:** Every block/release line answers: **who**, **why**, **which truth model**, **what still prevents release**, **convergence vs confirmed mismatch**.

**Fields / event names (incremental)**

- `authority_source`: `canonical_family` | `parity_integrity` | `mismatch_sweep` | `classifier_blocker`
- `block_tier`: `none` | `advisory` | `execution_block_g1` | `freeze_stand_down`
- `convergence_armed`: bool + `reason`
- Deprecate ambiguous strings where `release_blocker_*` meant informational (replaced by `info_*` for Phase 2 legacy gap).

**Files:** `RobotEvents` payloads for `STATE_CONSISTENCY_*`, `RECONCILIATION_MISMATCH_*`, `MismatchExecutionBlockDecisionLog`.

---

## Suggested order

1. Phase 2 (remaining) + Phase 5 partial — low risk, improves operability.
2. Phase 1 authority routing — **highest architectural impact**; do behind feature flag if needed.
3. Phase 4 episode containment — reduces noise; depends on Phase 1 clarity.
4. Phase 3 convergence — crosses adapter + coordinator; last or parallel with Phase 4 with clear interfaces.

---

## References

- `docs/robot/contracts/BROKER_QUANTITY_VIEWS.md` — three quantity views (must not mix).
- `MismatchEscalationPolicy` — timings / persistence / fail-closed eligibility.
- `StateConsistencyReleaseEvaluator` — release readiness and contradictions.
