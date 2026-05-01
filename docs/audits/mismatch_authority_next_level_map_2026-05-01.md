# QTSW2 Mismatch And Authority Next-Level Map

Audit date: 2026-05-01

Purpose:
- Turn the next cleanup level into a proof map before deleting safety code.
- Identify which authority/mismatch paths are runtime-active, fallback-only, or still safety-critical.
- Keep mechanical cleanup separate from behavior simplification.

Runtime anchor:
- Latest run pointer: `runs/LATEST_RUN.txt`
- Latest inspected run: `runs/2e1edfe08dc14685ac10bbf138e6222a`
- Run verdict: `status=WARN`, `status_reason=FLATTEN_OCCURRED`, `verdict_class=OPERATOR_REVIEW`, `recommended_action=MONITOR`, `confidence=MEDIUM`.
- Runtime DLL proof: `assembly_hash=7d82321ee84875988d141042b3caf043d4a99030e919fb8bd6a3872ac27a8180`, `assembly_last_write_utc=2026-04-30T23:09:55.2207133Z`.
- Safety proof: no robot log errors/criticals/hard criticals, no broker position or working orders at shutdown, no ownership active/orphan/open journal quantity at shutdown, no diagnostic contradictions, no crash/freeze/thread/protective failure evidence.
- MNG2/NG2 proof: session-close flatten armed reentry, submitted reentry intent `44cc7fdfeb6a16e6`, filled it, accepted protectives, and ended flat.

Important limitation:
- This run proves the deployed runtime path. The additional source hardening for global-sweep broker-flat reentry is harness-proven but should not be called runtime-proven until a deployed DLL with that source is confirmed by `ROBOT_BUILD_SIGNATURE`.

## 1. Current Authority Layers

| Layer | Primary files | Runtime role | Cleanup posture |
|---|---|---|---|
| Mismatch detection | `Execution/ReconciliationRunner.cs`, `RobotEngine.Reconciliation*.cs` | Compares broker/account state with journal/ownership state and invokes mismatch handling. | Keep. Detection is safety-critical and not a cleanup deletion target. |
| Mismatch escalation | `Execution/MismatchEscalationCoordinator*.cs` | Maintains per-instrument mismatch state, blocks execution, drives convergence/release, and escalates to fail-closed when needed. | Move-only split in progress. Keep release/fail-closed behavior unchanged until runtime proof catches up. |
| Submit authority | `Execution/NinjaTraderSimAdapter.SubmitGate.cs`, `Execution/InstrumentExecutionAuthority*.cs`, `Execution/UnifiedExecutionAuthority*.cs` | Decides whether entry/protective/flatten/reentry submits are allowed. | Conditional. Playback uses UEA, but fallback/default paths remain rollback safety. |
| Structural authority | `Execution/ExecutionStructuralLayer.cs` | Classifies submit safety from parity, ownership, repair, recovery, and quant-control snapshots. | Conditional. Ledger ownership is active in playback, but journal fallback remains safety evidence. |
| Ownership authority | `Execution/InstrumentOwnershipLedger.cs`, `Execution/OwnershipEventJournal.cs`, `Execution/ExecutionJournal*.cs` | Provides durable ownership truth and open quantity evidence. | Keep. May become the only read path later, but not yet. |
| Quant control | `Execution/QuantExecutionControlStore.cs` | Tracks expected post-fill/recovery control state and escalation timing. | Keep. Active default and used by structural authority. |
| Fail-closed recovery | `HardFailClosedExecutionModel`, adapter flatten paths, mismatch release code | Prevents silent divergence and forces flat/release decisions. | Keep. No deletion until multiple clean mismatch/recovery sessions prove redundancy. |

## 2. Runtime-Proven Flags

Playback/audit startup force-enables:
- `CanonicalOwnershipLedgerEnabled=true`
- `UnifiedExecutionAuthorityShadowEnabled=true`
- `UnifiedExecutionAuthorityEnabled=true`
- `StructuralLayerUseLedgerOwnership=true`

Latest proof:
- Run `2e1edfe08dc14685ac10bbf138e6222a` completed with these playback/audit flags active.
- Submit/reentry/protective paths worked for 11 filled trades.
- MNG2 reentry used the deployed authority path and ended flat.

Not proven enough for deletion:
- Static defaults still preserve rollback values.
- Only recent runs have validated the active playback authority path.
- No intentional persistent-mismatch/fail-closed recovery playback has been run against the newest hash.

## 3. Keep, Conditional, Candidate

| Candidate | Classification | Reason |
|---|---|---|
| `ReconciliationRunner` mismatch callback wiring | Keep | It is the upstream safety detector. |
| `MismatchEscalationCoordinator` fail-closed state machine | Keep | It owns execution block, convergence, release, and hard fail-closed escalation. |
| `FailClosedStrictReleaseConfirmationEnabled` | Keep | Release safety depends on fresh snapshot and quiet-window proof. |
| `EnableHardFailClosedJournalIntegrity` | Keep | Guards journal/parity invariants after execution activity. |
| `EnableHardFailClosedBrokerFlatten` | Keep | Final broker safety rail. |
| `ReconciliationRepairExecutorEnabled=false` default | Keep | Ordinary playback intentionally observes/classifies without repair mutations. |
| `CanonicalOwnershipLedgerEnabled` fallback | Conditional remove later | Playback uses ledger writes, but production default still keeps rollback. |
| `UnifiedExecutionAuthorityEnabled` fallback | Conditional remove later | Playback uses UEA, but old submit chain is still rollback. |
| `StructuralLayerUseLedgerOwnership` fallback | Conditional remove later | Playback reads ledger ownership, but journal-based structural fallback remains rollback. |
| `StructuralLayerPhase3DemoteParityNotOkSubmitDeny` | Keep | Rollout gate for demoting hard parity denies. |
| `StructuralLayerPhase3DemoteRepairActiveSubmitDeny` | Keep | Rollout gate for demoting repair-active submit denies. |
| `KEY_EVENTS.jsonl` primary evidence path | Candidate fix | Latest summary hydrated from journals, but key events stayed empty. Reporting should not rely on it as the only evidence source. |

## 4. MismatchEscalationCoordinator Map

Primary responsibilities in `system/modules/robot/core/Execution/MismatchEscalationCoordinator.cs`:
- Tracks instrument state in `_stateByInstrument`.
- Publishes mismatch execution block authority through `_onMismatchExecutionBlockAuthorityChanged`.
- Suppresses transient first mismatch escalation during convergence windows.
- Processes audit ticks through `OnAuditTick`.
- Drives mismatch-present and mismatch-absent state transitions.
- Evaluates release readiness through `StateConsistencyReleaseReadinessResult`.
- Coordinates stable pending release, quiet-window checks, fingerprint stability, pending IEA work, and final release.
- Escalates persistent mismatch to fail-closed only after convergence/progress controls are exhausted.
- Emits operational diagnostics for release blockers, throttling, reentry blocking, and fail-closed counts.

Move-only split started:
- `MismatchEscalationCoordinator.PendingIea.cs` now owns pending-IEA defer state, decision data, throttle constants, and `ObservePendingIeaDefer`.
- `MismatchEscalationCoordinator.Convergence.cs` now owns convergence-window state, first-escalation suppression, convergence invariant telemetry, pending-convergence diagnostics, and convergence test hooks.
- `MismatchEscalationCoordinator.Telemetry.cs` now owns mismatch metric counters, test telemetry counters, type-metric incrementing, and `EmitMetrics`.
- `MismatchEscalationCoordinator.TestHooks.cs` now owns harness-only state/gate test helpers.
- `MismatchEscalationCoordinator.PublicApi.cs` now owns public/wake/query entrypoints and execution-trigger notification.
- `MismatchEscalationCoordinator.ReleaseReadiness.cs` now owns release quiet-window/fingerprint helpers, soft-transition predicates, residual-release blocker classification, and fail-closed strict stability keying.
- `MismatchEscalationCoordinator.Diagnostics.cs` now owns canonical execution-event emission, gate progress-control payload merging, and shared gate payload builders.
- `MismatchEscalationCoordinator.Progress.cs` now owns gate progress/throttle counter resets, throttled/cap/reentry-blocked progress emitters, expensive-pass progress accounting, and throttle-baseline helpers.
- The NinjaTrader runtime project links all eight shared partials explicitly.
- Verification after extraction: core build `0 Error(s)`, RobotCore build `0 Error(s)`, and focused checks passed: `AUTHORITY_CONTRADICTIONS`, `ORDER_RECONCILIATION`, `RUN_SUMMARY`, `RUN_SUMMARY_BUILDER`, `EXECUTION_CONTEXT_CONTRACT`, `MISMATCH_ESCALATION`, `MISMATCH_CONVERGENCE_CONTRACT`, `MISMATCH_CONVERGENCE_BRIDGE_PROBE`.

Cleanup implication:
- The coordinator is too conceptually dense for deletion work.
- First safe move is a documentation-assisted split by responsibility:
  - state models and counters
  - pending-IEA deferral
  - convergence suppression
  - audit tick orchestration
  - release readiness and quiet-window release
  - fail-closed escalation
  - telemetry/diagnostics

Rule:
- Split only after a checkpoint.
- Keep behavior identical.
- Run mismatch, reconciliation, authority, and run-summary harnesses after any split.

## 5. Next Proof Plan

Step 1 - Checkpoint proven state:
- Stage active source fixes, this map, authority matrix update, and master audit update only.
- Exclude run artifacts and build outputs unless intentionally creating a runtime-evidence commit.

Step 2 - Keep one more runtime proof loop:
- Deploy only after source checkpoint.
- Confirm `ROBOT_BUILD_SIGNATURE` hash changes if source changes are included.
- Validate no `EXECUTION_BLOCKED:NT_CONTEXT_NOT_SET`, no protective failure, no crash/freeze/thread issue, no open exposure at shutdown.

Step 3 - Start move-only coordinator split:
- Started: extracted pending-IEA deferral first because it was a contained state/helper pocket.
- Continued: extracted convergence suppression/invariant helpers while leaving forced-convergence release/fail-closed machinery in the main coordinator.
- Continued: extracted telemetry counters and `EmitMetrics` while leaving gate release/fail-closed telemetry emitters in the main coordinator.
- Continued: extracted harness-only test hooks.
- Continued: extracted public/wake/query entrypoints and execution-trigger notification.
- Continued: extracted release-readiness helper predicates/fingerprints while leaving the release/fail-closed state machine in the main coordinator.
- Continued: extracted canonical diagnostic emission and gate payload builders while leaving event-call sites in the main coordinator.
- Continued: extracted gate progress/throttle helper accounting while leaving `AdvanceStateConsistencyGate` in the main coordinator.
- Continue with coordinator state helpers or release telemetry helpers next.
- Do not change release/fail-closed decisions.
- Run `AUTHORITY_CONTRADICTIONS`, `ORDER_RECONCILIATION`, `RUN_SUMMARY`, `RUN_SUMMARY_BUILDER`, `EXECUTION_CONTEXT_CONTRACT`, and mismatch-specific harnesses.

Step 4 - Only after multiple clean runs:
- Consider making playback-proven authority flags harder defaults.
- Remove one fallback path at a time.
- Require fresh runtime proof after each removal.

## 6. Verdict

This unlocks the next level: authority simplification can start, but as a proof-driven map and move-only split, not deletion.

The system has enough evidence to stop re-proving the MNG2 reentry path. It does not yet have enough evidence to remove fail-closed recovery, mismatch release confirmation, UEA fallback, or journal/ledger compatibility.
