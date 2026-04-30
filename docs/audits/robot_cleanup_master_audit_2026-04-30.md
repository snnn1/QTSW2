# QTSW2 Robot Cleanup Master Audit

Audit date: 2026-04-30

Purpose:
- Create one cleanup map for the robot codebase.
- Separate active runtime source from legacy mirror/source noise.
- Rank cleanup work by risk, payoff, and runtime relevance.
- Avoid asking for each small extraction one at a time.

Scope:
- `system/modules/robot/core`
- `system/RobotCore_For_NinjaTrader`
- `system/NT_ADDONS`

Runtime anchor:
- Latest run pointer: `runs/LATEST_RUN.txt`
- Latest run: `runs/18e98895c74a404b8498878e3088a123`
- Latest run summary: `status=WARN`, `status_reason=DIAGNOSTIC_CONTRADICTION`, `trades=11`, `errors=0`, `robot_log_errors=0`, `robot_log_critical=0`, `had_open_exposure_at_shutdown=false`, `broker_position_qty_at_shutdown=0`, `broker_working_orders_at_shutdown=0`, `execution_command_stalled_events=0`, `had_crash_or_freeze_signal=false`.

Validation already performed after current cleanup slice:
- `dotnet build system/modules/robot/core/Robot.Core.csproj -c Release -v:minimal` passed with `0 Error(s)`.
- `dotnet build system/RobotCore_For_NinjaTrader/Robot.Core.csproj -c Release -v:minimal` passed with `0 Error(s)`.
- Harness checks passed: `RANGE_BUILDING_SNAPSHOT`, `SESSION_AUTHORITY_GATE`, `EXECUTION_EVENT_REPLAY`, `REENTRY_TIMING`, `FORCED_FLATTEN_POLICY`, `RUN_SUMMARY`, `AUTHORITY_CONTRADICTIONS`, `ORDER_RECONCILIATION`.

## 1. Source Of Truth Boundary

| Area | Runtime role | Evidence | Cleanup rule |
|---|---|---|---|
| `system/RobotCore_For_NinjaTrader` | Runtime DLL source for NinjaTrader. | `docs/robot/DEPLOY_SOURCE_OF_TRUTH_2026-03-19.md` says NinjaTrader uses pre-built `Robot.Core.dll` and strategy source only. | Primary NT runtime cleanup target. |
| `system/modules/robot/core` | Shared/core source and harness target. | `system/RobotCore_For_NinjaTrader/Robot.Core.csproj` links many shared files from this tree. | Primary shared cleanup target. |
| `system/NT_ADDONS` | Legacy/manual mirror only. | `tools/sync_nt_addons_from_robotcore.ps1` header says normal deploy is DLL-only and AddOns source is not compiled/copied. | Do not hand-refactor first. Sync or quarantine after active source changes. |

Important mirror fact:
- `tools/sync_nt_addons_from_robotcore.ps1 -CheckOnly` reports `86` managed mirror files, `124` RobotCore/runtime source files missing from the legacy mirror, and drift in `RobotEngine.cs`.
- The tool now reports source-only files and can create missing mirror files when explicitly run without `-CheckOnly`. Do not run that as part of normal deploy; `NT_ADDONS` is a legacy/manual mirror only.

## 2. Current Cleanup State

`RobotEngine.cs` has already been split in both active trees:

| File | Lines |
|---|---:|
| `system/modules/robot/core/RobotEngine.cs` | 2249 |
| `system/modules/robot/core/RobotEngine.Bars.cs` | 1322 |
| `system/modules/robot/core/RobotEngine.Connection.cs` | 109 |
| `system/modules/robot/core/RobotEngine.Diagnostics.cs` | 429 |
| `system/modules/robot/core/RobotEngine.Heartbeat.cs` | 97 |
| `system/modules/robot/core/RobotEngine.Ownership.cs` | 517 |
| `system/modules/robot/core/RobotEngine.Timetable.cs` | 1320 |
| `system/modules/robot/core/RobotEngine.Streams.cs` | 715 |
| `system/modules/robot/core/RobotEngine.PlaybackStall.cs` | 623 |
| `system/modules/robot/core/RobotEngine.Reconciliation.cs` | 630 |
| `system/modules/robot/core/RobotEngine.Reconciliation.Observations.cs` | 694 |
| `system/modules/robot/core/RobotEngine.Reconciliation.Release.cs` | 758 |
| `system/modules/robot/core/RobotEngine.Reconciliation.JournalIntegrity.cs` | 314 |
| `system/modules/robot/core/RobotEngine.Reconciliation.Recovery.cs` | 851 |
| `system/modules/robot/core/RobotEngine.Session.cs` | 1147 |
| `system/modules/robot/core/RobotEngine.Shutdown.cs` | 350 |

Same line counts exist in `system/RobotCore_For_NinjaTrader` for the active RobotEngine partials.

Worktree warning:
- The working tree contains many unrelated modified/untracked files and build artifacts.
- Before broad cleanup continues, checkpoint active source edits separately from run artifacts and build outputs.

## 3. Largest Hotspots

| Rank | File | Lines | Role | Cleanup classification |
|---:|---|---:|---|---|
| 1 | `system/NT_ADDONS/RobotEngine.cs` | 11932 | Legacy mirror, currently monolithic. | Mirror drift/quarantine issue, not first runtime target. |
| 2 | `system/RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | 11531 | NT API bridge: order update, execution update, submit, cancel, flatten, BE, account snapshot. | High payoff, high risk, split mechanically by NT domain. |
| 3 | `system/NT_ADDONS/Execution/NinjaTraderSimAdapter.NT.cs` | 11531 | Legacy mirror. | Sync/quarantine after active source cleanup. |
| 4 | `system/modules/robot/core/StreamStateMachine*.cs` | 2119 / 1777 / 1431 / 1430 / 830 / 612 / 615 / 525 / 375 / 119 | Stream lifecycle, range, brackets, commit, forced flatten, reentry, hydration. | Move-only split complete for current cleanup pass; optional finer split later. |
| 5 | `system/RobotCore_For_NinjaTrader/StreamStateMachine*.cs` | same as core | Same as core, identical copy. | Kept synchronized with core extraction. |
| 6 | `system/NT_ADDONS/StreamStateMachine.cs` | 9702 | Legacy mirror. | Sync/quarantine after active source cleanup. |
| 7 | `system/modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` | 5571 | Core NT-conditional adapter implementation. | Split after RobotCore NT adapter plan is defined. |
| 8 | `system/modules/robot/core/Execution/NinjaTraderSimAdapter.cs` | 5024 | Adapter orchestration, gates, intent state, BE, IEA command routing. | High payoff, medium risk, split into pure adapter domains. |
| 9 | `system/modules/robot/core/Execution/ExecutionJournal.cs` | 4978 | Durable order/fill lifecycle, indexing, repair, legacy field hydration. | High payoff, medium risk, split by write/read/repair/model. |
| 10 | `system/modules/robot/core/Execution/MismatchEscalationCoordinator.cs` | 3366 | Mismatch/fail-closed escalation and release. | High conceptual risk; refactor only after authority/frame cleanup. |
| 11 | `system/modules/robot/core/RobotEngine.Reconciliation*.cs` | 630 / 694 / 758 / 314 / 851 | Engine reconciliation assembly, recovery, state consistency, forced convergence. | Completed mechanical split; future work should be behavioral simplification only after runtime validation. |
| 12 | `system/RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | 2983 | NT strategy callback shell. | Split later; runtime-sensitive but mostly platform adapter shell. |

## 4. Cleanup Lanes

### Lane 0 - Checkpoint And Hygiene

Status: required before further large edits.

Work:
- Stage only active source cleanup files, not run artifacts or build outputs.
- Keep generated `bin/obj` changes out of source commits.
- Re-run core build, NT build, and smoke harness after every batch.

Why:
- Current `git status --short` contains active source edits, run artifacts, docs, and build outputs together.
- Continuing without a checkpoint increases merge/conflict risk.

Risk: low.

### Lane 1 - Source-Of-Truth And Mirror Policy

Status: partially implemented.
Decision: `NT_ADDONS` is documented as legacy/non-runtime source, not an active cleanup target.

Work:
- Treat `system/modules/robot/core` and `system/RobotCore_For_NinjaTrader` as active source.
- Treat `system/NT_ADDONS` as legacy/manual mirror unless explicitly syncing it.
- Mirror tool now reports source-only files and can create missing mirror files when intentionally run without `-CheckOnly`.
- `system/NT_ADDONS/README.md` states it is not runtime compiled/deployed.

Why:
- `NT_ADDONS` still contains the huge monolithic `RobotEngine.cs`; active runtime trees have been split.
- Sync check now reports both existing-file drift and missing runtime source files, so partial mirror state is explicit.

Risk: low if documented; medium if deleting/quarantining before all tools are checked.

### Lane 2 - Finish RobotEngine Mechanical Decomposition

Status: active mechanical split complete; behavioral simplification deferred.

Already split:
- Playback stall handling
- Shutdown
- Session truth
- Reconciliation
- Bars
- Ownership
- Timetable
- Stream/status helpers

Remaining useful splits:
- `RobotEngine.Diagnostics.cs`: `LogEvent`, `LogEngineEvent`, `ConvertToRobotLogEvent`, runtime fingerprint/status logging, health monitor setup.
- `RobotEngine.ExecutionCallbacks.cs`: adapter callback wiring, execution event callbacks, strategy/account status hooks.
- `RobotEngine.Reconciliation.*.cs`: split into orchestration, observation assembly, release readiness, journal integrity, forced convergence, and recovery.

Why:
- Mechanical partial extraction has already proven safe with builds/harnesses.
- Main `RobotEngine.cs` is still 2249 lines, but the high-risk reconciliation mass is now split into smaller partials.

Risk: low if strictly move-only.

### Lane 3 - StreamStateMachine Decomposition

Status: active move-only split complete for current cleanup pass.

Extracted partials:
- `StreamStateMachine.Hydration.cs`
- `StreamStateMachine.RangeFlow.cs`
- `StreamStateMachine.EntryBrackets.cs`
- `StreamStateMachine.Commit.cs`
- `StreamStateMachine.Protectives.cs`
- `StreamStateMachine.TimeAndJournal.cs`
- `StreamStateMachine.ForcedFlatten.cs`
- `StreamStateMachine.Reentry.cs`
- `StreamStateMachine.Bars.cs`

Evidence:
- Active `StreamStateMachine.cs` is now 1431 lines in core and RobotCore; the legacy `NT_ADDONS` mirror remains monolithic by policy.
- `StreamStateMachine.RangeFlow.cs` owns pre-hydration/range-state handlers and is 1430 lines.
- `StreamStateMachine.EntryBrackets.cs` owns entry-order recovery, `OnBar`, breakout validation, deferred bracket authority, and bracket submit at lock, and is 2119 lines.
- Builds and smoke harnesses passed after extraction.

Cleanup rule:
- Move-only extraction first.
- No lifecycle logic changes in the same batch.

Risk: medium because stream lifecycle is central, but move-only extraction is manageable.

### Lane 4 - NinjaTraderSimAdapter Decomposition

Status: not started.

Proposed shared adapter partials:
- `NinjaTraderSimAdapter.Context.cs`
- `NinjaTraderSimAdapter.StrategyThread.cs`
- `NinjaTraderSimAdapter.IntentRegistry.cs`
- `NinjaTraderSimAdapter.SubmitGate.cs`
- `NinjaTraderSimAdapter.Reentry.cs`
- `NinjaTraderSimAdapter.Protective.cs`
- `NinjaTraderSimAdapter.BreakEven.cs`
- `NinjaTraderSimAdapter.FlattenCommands.cs`
- `NinjaTraderSimAdapter.IeaCommands.cs`
- `NinjaTraderSimAdapter.Diagnostics.cs`

Proposed NT adapter partials:
- `NinjaTraderSimAdapter.NT.Ingress.cs`
- `NinjaTraderSimAdapter.NT.OrderUpdate.cs`
- `NinjaTraderSimAdapter.NT.ExecutionUpdate.cs`
- `NinjaTraderSimAdapter.NT.SubmitEntry.cs`
- `NinjaTraderSimAdapter.NT.SubmitProtective.cs`
- `NinjaTraderSimAdapter.NT.Flatten.cs`
- `NinjaTraderSimAdapter.NT.Cancel.cs`
- `NinjaTraderSimAdapter.NT.AccountSnapshot.cs`
- `NinjaTraderSimAdapter.NT.BreakEven.cs`

Evidence:
- `system/RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` is 11531 lines.
- `system/modules/robot/core/Execution/NinjaTraderSimAdapter.cs` is 5024 lines.
- This file owns the areas that caused earlier freeze/crash work: NT-thread queueing, cancel/flatten, execution update continuation, unresolved retry, and protective handling.

Cleanup rule:
- Split first, simplify later.
- No removal of fail-closed paths while latest runtime evidence is only `WARN/MONITOR`, not multiple proven green runs.

Risk: medium-high.

### Lane 5 - ExecutionJournal Decomposition

Status: not started.

Proposed partials:
- `ExecutionJournal.Models.cs`
- `ExecutionJournal.Submission.cs`
- `ExecutionJournal.EntryFill.cs`
- `ExecutionJournal.ExitFill.cs`
- `ExecutionJournal.Query.cs`
- `ExecutionJournal.Repair.cs`
- `ExecutionJournal.LegacyHydration.cs`
- `ExecutionJournal.AdoptionCandidates.cs`

Evidence:
- `ExecutionJournal.cs` is 4978 lines in all trees.
- It contains obsolete compatibility entry fill API, legacy timestamp hydration, adoption candidate indexes, repair/close helpers, and normal fill writes in one file.

Cleanup rule:
- Keep legacy persisted fields and hydration until run artifact compatibility is intentionally migrated.
- Do not remove obsolete members just because compiler says obsolete; they are still compatibility contracts.

Risk: medium.

### Lane 6 - Authority/Feature Flag Cleanup

Status: not ready for deletion; map first.

Feature flags currently preserving alternate behavior include:
- `CanonicalOwnershipLedgerEnabled=false`
- `UnifiedExecutionAuthorityEnabled=false`
- `ReconciliationRepairExecutorEnabled=false`
- `StructuralLayerUseLedgerOwnership=false`
- `StructuralLayerPhase3DemoteParityNotOkSubmitDeny=false`
- `StructuralLayerPhase3DemoteRepairActiveSubmitDeny=false`
- `EnablePostFillAlignmentGate=true`
- `QuantExecutionControlStoreEnabled=true`
- `AggregatedProtectiveSingleWaveEnabled=true`

Evidence:
- `system/modules/robot/core/Execution/FeatureFlags.cs`
- `RobotEngine` force-enables canonical ledger, UEA, and structural ledger ownership for playback/audit startup paths.
- Submit authority still contains active/fallback logic around `UnifiedExecutionAuthorityEnabled`.

Cleanup rule:
- Do not delete legacy/fallback authority paths until fresh run evidence proves the active flags across multiple sessions.
- First create an authority flag matrix documenting default, runtime override, and owner.

Risk: high if mixed with mechanical cleanup.

### Lane 7 - Health/Logging/Diagnostics Cleanup

Status: safe after RobotEngine split.

Candidates:
- `HealthMonitor.cs` (1354 core, 1655 RobotCore/NT_ADDONS)
- `RobotLoggingService.cs` (1285 core, 1442 RobotCore/NT_ADDONS)
- `RobotEventTypes.cs` (1367 core, 1375 RobotCore/NT_ADDONS)
- diagnostics trace writers and runtime audit helpers

Evidence:
- Health/logging are sizeable, but latest run has no crash/freeze/platform disable signal.
- Some config fields are obsolete/ignored but retained for compatibility.

Cleanup rule:
- Extract/centralize, but do not remove alert compatibility until dashboard/watchdog consumers are checked.

Risk: low-medium.

## 5. Safe-Now, Conditional, Keep

| Candidate | Classification | Reason |
|---|---|---|
| Mechanical partial extraction from active source files | Safe-now | Builds and harnesses have passed after similar `RobotEngine` extractions. |
| `NT_ADDONS` manual refactor | Conditional | It is legacy mirror, not runtime source; sync/quarantine policy must come first. |
| Feature flag fallback removal | Keep for now | Active runtime is only recently stable; fallback paths still protect rollback. |
| Fail-closed recovery/mismatch criticals | Keep | These are safety-critical and were involved in earlier runtime failures. |
| Obsolete journal timestamp/field compatibility | Keep | Persisted run artifacts may still deserialize legacy fields. |
| `NinjaTraderLiveAdapter` stubs | Keep or clearly mark out-of-scope | Not used for SIM playback, but removing breaks interface expectations. |
| HealthMonitor obsolete config properties | Conditional | Can remove only after config/dashboard consumers are checked. |

## 6. One-Pass Execution Plan

This is the plan to keep moving without asking for each small file.

### Batch A - Checkpoint Current Proven Cleanup

1. Stage active source partials and modified active files.
2. Exclude run artifacts and build outputs unless intentionally committing audit data.
3. Build core and RobotCore.
4. Run smoke harnesses.

Exit criteria:
- `0 Error(s)` in both builds.
- Smoke harness list passes.

### Batch B - Finish RobotEngine Structure

Completed:
- Extracted `RobotEngine.Diagnostics.cs`, `RobotEngine.Connection.cs`, and `RobotEngine.Heartbeat.cs`.
- Split `RobotEngine.Reconciliation.cs` into smaller responsibility partials.

Exit criteria:
- Same build/harness list passes. Completed after commits `4401fff` and `ec12774`.
- `RobotEngine.cs` and each partial are below roughly 2000 lines where practical.

### Batch C - Decide NT_ADDONS Policy

Completed:
- Updated mirror sync to report source-only files and create them only when an explicit sync is run.
- Added `system/NT_ADDONS/README.md` and deploy-doc wording that this tree is legacy/non-runtime source.

Exit criteria:
- `tools/sync_nt_addons_from_robotcore.ps1 -CheckOnly` intentionally documents partial legacy mirror state.

### Batch D - StreamStateMachine Move-Only Split

Completed:
- Extracted hydration, commit, protectives, time/journal helpers, forced flatten, reentry, and bar/transition helpers.
- Extracted range-state flow and entry/bracket submission flow.
- Kept identical active copies in core and RobotCore.

Remaining:
- No required move-only StreamStateMachine split remains for this pass.
- Optional later cleanup: split `StreamStateMachine.EntryBrackets.cs` more finely after adapter cleanup if it remains a hotspot.

Exit criteria:
- Stream lifecycle harnesses pass: `RANGE_BUILDING_SNAPSHOT`, `REENTRY_TIMING`, `FORCED_FLATTEN_POLICY`, `EXECUTION_EVENT_REPLAY`, `RUN_SUMMARY`.

### Batch E - Adapter Move-Only Split

1. Split shared adapter first.
2. Split NT-specific adapter second.
3. Keep NT-thread and fail-closed behavior untouched.

Exit criteria:
- Core + RobotCore builds pass.
- Execution/reconciliation/protective harnesses pass.
- Fresh playback run has no crash/freeze/stall/order-thread violations.

### Batch F - Journal Split

1. Split `ExecutionJournal.cs` by write/read/repair/model/legacy-hydration domains.
2. Keep serialized model compatibility unchanged.

Exit criteria:
- Journal/reconciliation/run-summary harnesses pass.
- Latest run artifacts still load.

### Batch G - Authority Simplification

Only after multiple clean playback runs:
1. Document active runtime flag values.
2. Retire proven-shadow fallback paths one at a time.
3. Remove or harden stale compatibility paths only with runtime evidence.

Exit criteria:
- No mismatch drift, stale recovery, NT-thread violation, protective failure, crash/freeze, or open exposure at shutdown over several fresh runs.

## 7. Biggest Remaining Cleanup Risks

| Risk | Why it matters | Control |
|---|---|---|
| Mistaking `NT_ADDONS` for active runtime source | It can waste cleanup effort or create confusing mirror drift. | Source-of-truth policy first. |
| Combining move-only extraction with behavior change | Makes runtime regression blame difficult. | One batch type at a time. |
| Removing authority fallback too soon | Recent stability is promising but not enough to delete safety rails. | Require multiple clean playback runs. |
| Editing generated/build/run artifacts into source commits | Worktree is noisy and can hide real changes. | Stage explicit file paths only. |
| Splitting `NinjaTraderSimAdapter.NT.cs` carelessly | It touches exact freeze/crash-sensitive NT API paths. | Split by domain, build often, no logic changes. |

## 8. Verdict

Cleanup should continue, but in batches:

1. Completed: checkpoint current proven `RobotEngine` extraction.
2. Completed: finish remaining `RobotEngine` diagnostics/callback/reconciliation splits.
3. Completed: decide `NT_ADDONS` mirror policy.
4. Completed: split `StreamStateMachine`.
5. Next: split shared and NT adapter files.
6. Then split `ExecutionJournal`.
7. Only then remove authority/fallback/deprecated code with fresh runtime proof.

The system is stable enough for mechanical cleanup. It is not mature enough yet for aggressive deletion of fail-closed, recovery, journal compatibility, or authority fallback paths.
