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
- Latest run: `runs/2e1edfe08dc14685ac10bbf138e6222a`
- Latest run summary after proof-hardening playback: `status=WARN`, `status_reason=FLATTEN_OCCURRED`, `verdict_class=OPERATOR_REVIEW`, `recommended_action=MONITOR`, `confidence=MEDIUM`, `trades=11`, `errors=0`, `robot_log_errors=0`, `robot_log_critical=0`, `robot_log_hard_critical=0`, `had_open_exposure_at_shutdown=false`, `broker_position_qty_at_shutdown=0`, `broker_working_orders_at_shutdown=0`, `execution_command_stalled_events=0`, `had_crash_or_freeze_signal=false`, `diagnostic_contradictions=0`, `flatten_confirmed=3`, `had_flatten=true`, `strategy_disabled_events=0`.
- Latest run deploy check: the runtime loaded `Robot.Core.dll` from the NinjaTrader Custom folder with full SHA-256 `assembly_hash=7d82321ee84875988d141042b3caf043d4a99030e919fb8bd6a3872ac27a8180`, `assembly_last_write_utc=2026-04-30T23:09:55.2207133Z`, length `4267520`. `ROBOT_BUILD_SIGNATURE` was emitted from the engine-start runtime path.
- Latest run reality check: 11 runtime streams reached `DONE`; 11 filled trades completed; 22 execution intents were seen; no rejected journals; orphan-fill files were empty; no broker position or working orders at shutdown; no crash/freeze/thread violation; no `EXECUTION_BLOCKED`/`NT_CONTEXT_NOT_SET`; no transient MNG `PROTECTIVE_MISSING_STOP`; summary reported truthful flatten activity as operator review.
- MNG2/NG2 reentry proof from latest run: original NG2 intent `b4867746262eb34b` filled short quantity `2`, was session-close flattened, then stream `NG2` logged `FORCED_FLATTEN_COMPLETED_LATE_REENTRY_ARMED`, submitted reentry intent `44cc7fdfeb6a16e6`, filled it, submitted/accepted reentry protectives, and later flattened safely. Final stream journal had `Committed=true`, `LastState=DONE`, `CommitReason=SLOT_EXPIRED`, `ReentrySubmitted=true`, `ReentryFilled=true`, `ProtectionSubmitted=true`, and `ProtectionAccepted=true`.
- Reporting proof from latest run: `KEY_EVENTS.jsonl` remained empty, but derived execution summary hydration no longer collapsed to zeros: latest derived summary had `IntentsSeen=22`, `IntentsExecuted=11`, `OrdersSubmitted=62`, `OrdersFilled=27`, and `OrdersRejected=0`.
- Remaining source checkpoint note: the additional global-sweep/broker-flat reentry hardening is covered by focused harness tests and should be checkpointed with active source edits. Do not treat that narrow source hardening as runtime-proven until it is included in a deployed DLL and a matching run signature.

Validation already performed after current cleanup slice:
- `dotnet build system/modules/robot/core/Robot.Core.csproj -c Release -v:minimal --no-restore` passed with `0 Error(s)`.
- `dotnet build system/RobotCore_For_NinjaTrader/Robot.Core.csproj -c Release -v:minimal --no-restore` passed with `0 Error(s)`.
- Harness checks passed: `PROTECTIVE_AUDIT`, `EXECUTION_EVENT_REPLAY`, `AUTHORITY_CONTRADICTIONS`, `ORDER_RECONCILIATION`, `RUN_SUMMARY`, `RUN_SUMMARY_BUILDER`, `EXECUTION_CONTEXT_CONTRACT`, `REENTRY_TIMING`, `FORCED_FLATTEN_POLICY`, `RANGE_BUILDING_SNAPSHOT`.
- Move-only mismatch cleanup started after this proof point: extracted pending-IEA deferral state and `ObservePendingIeaDefer` into `Execution/MismatchEscalationCoordinator.PendingIea.cs`; extracted convergence window suppression/invariant helpers into `Execution/MismatchEscalationCoordinator.Convergence.cs`; extracted mismatch metric counters and `EmitMetrics` into `Execution/MismatchEscalationCoordinator.Telemetry.cs`; extracted harness-only helpers into `Execution/MismatchEscalationCoordinator.TestHooks.cs`; extracted public/wake/query entrypoints into `Execution/MismatchEscalationCoordinator.PublicApi.cs`; extracted release-readiness helper predicates/fingerprints into `Execution/MismatchEscalationCoordinator.ReleaseReadiness.cs`; extracted canonical diagnostics/payload helpers into `Execution/MismatchEscalationCoordinator.Diagnostics.cs`; linked all seven shared partials into the NinjaTrader project; core and RobotCore builds passed with `0 Error(s)`; focused checks passed: `AUTHORITY_CONTRADICTIONS`, `ORDER_RECONCILIATION`, `RUN_SUMMARY`, `RUN_SUMMARY_BUILDER`, `EXECUTION_CONTEXT_CONTRACT`, `MISMATCH_ESCALATION`, `MISMATCH_CONVERGENCE_CONTRACT`, `MISMATCH_CONVERGENCE_BRIDGE_PROBE`.
- Latest fix pass completed: startup NT context verification and pre-wire account/position/cancel snapshot calls now emit `EXECUTION_CONTEXT_NOT_READY` instead of `EXECUTION_BLOCKED`; run-summary diagnostic contradiction output now requires matching critical-log evidence; execution summary hydration now falls back from empty `KEY_EVENTS.jsonl` to execution journals and daily summary counts; RobotCore runtime marks `NtSubmitProtectivesCommand` as protective-submit pending before enqueueing/draining; completed journal `FLATTEN` exits now set flatten activity in run summaries; `ROBOT_BUILD_SIGNATURE` now emits from the loaded DLL engine-start path with SHA-256 hash evidence; normal NinjaTrader `State.Terminated` shutdown now logs `STRATEGY_TERMINATED_BY_NINJATRADER`.
- Deploy verification before latest run: `tools/rebuild_and_deploy.ps1 -ForceCacheClear` copied and hash-verified `Robot.Core.dll` to both NinjaTrader Custom folders. Source and both targets matched hash prefix `C90008549B57A95C8F7C1111`, length `4260864`, last write UTC `2026-04-30T18:37:40.2739176Z`.
- Deploy verification after this proof-hardening pass: `tools/rebuild_and_deploy.ps1 -ForceCacheClear` copied and hash-verified `Robot.Core.dll` to both NinjaTrader Custom folders. Source and both targets now match hash `7D82321EE84875988D141042B3CAF043D4A99030E919FB8BD6A3872AC27A8180`, length `4267520`, last write UTC `2026-04-30T23:09:55.2207133Z`.
- Pre-playback preflight after this proof-hardening pass: NinjaTrader was not running; both deployed `RobotSimStrategy.cs` copies contained `STRATEGY_TERMINATED_BY_NINJATRADER`; active source retains `STRATEGY_DISABLED_BY_NINJATRADER` only for explicit platform-disable run-summary handling/tests.
- Fresh playback validation completed for the proof-hardening pass: run `2e1edfe08dc14685ac10bbf138e6222a` loaded the expected `7D82321EE84875988D141042...` DLL, showed engine-start `assembly_hash` evidence, produced truthful flatten/operator-review summary output, hydrated the derived execution summary from journals, and ended flat with no crash/freeze/thread/protective/open-exposure failure.

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
| 2 | `system/RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter*.cs` | max NT partial 1836; max shared partial 1160 | NT API bridge plus shared adapter orchestration: order update, execution update, submit, cancel, flatten, BE, account snapshot, gates, IEA routing. | Move-only split complete; simplify behavior only after fresh playback validation. |
| 3 | `system/NT_ADDONS/Execution/NinjaTraderSimAdapter.NT.cs` | 11531 | Legacy mirror. | Sync/quarantine after active source cleanup. |
| 4 | `system/modules/robot/core/StreamStateMachine*.cs` | 2119 / 1777 / 1431 / 1430 / 830 / 612 / 615 / 525 / 375 / 119 | Stream lifecycle, range, brackets, commit, forced flatten, reentry, hydration. | Move-only split complete for current cleanup pass; optional finer split later. |
| 5 | `system/RobotCore_For_NinjaTrader/StreamStateMachine*.cs` | same as core | Same as core, identical copy. | Kept synchronized with core extraction. |
| 6 | `system/NT_ADDONS/StreamStateMachine.cs` | 9702 | Legacy mirror. | Sync/quarantine after active source cleanup. |
| 7 | `system/modules/robot/core/Execution/NinjaTraderSimAdapter*.cs` | max shared partial 1135; max NT partial 955 | Shared adapter orchestration plus NT-conditional implementation mirror. | Move-only split complete; NT partials are wildcard-excluded from the core build. |
| 8 | `system/RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs` | 2983 | NT strategy callback shell. | Split later; runtime-sensitive but mostly platform adapter shell. |
| 9 | `system/modules/robot/core/Execution/ExecutionJournal*.cs` | max partial 1572 | Durable order/fill lifecycle, indexing, repair, legacy field hydration. | Move-only split complete; persisted compatibility retained. |
| 10 | `system/modules/robot/core/Execution/MismatchEscalationCoordinator*.cs` | 2350 / 292 / 240 / 209 / 107 / 103 / 87 / 33 | Mismatch/fail-closed escalation and release; convergence suppression, diagnostics/payload helpers, release-readiness helpers, public API, pending-IEA deferral, telemetry counters, and test hooks now split to small partials. | Move-only split in progress; behavior simplification still requires fresh runtime proof. |
| 11 | `system/modules/robot/core/RobotEngine.Reconciliation*.cs` | 630 / 694 / 758 / 314 / 851 | Engine reconciliation assembly, recovery, state consistency, forced convergence. | Completed mechanical split; future work should be behavioral simplification only after runtime validation. |
| 12 | `system/modules/robot/core/HealthMonitor*.cs`, `RobotLoggingService*.cs`, `RobotEventTypes*.cs` | max partial 993 | Health/watcher state, logging pipeline, event level/type registry. | Move-only split complete; behavior cleanup deferred. |

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

Status: shared and NT-specific adapter move-only split complete for current cleanup pass.

Extracted shared adapter partials:
- `NinjaTraderSimAdapter.Context.cs`
- `NinjaTraderSimAdapter.RuntimeQueues.cs`
- `NinjaTraderSimAdapter.HostIea.cs`
- `NinjaTraderSimAdapter.SubmitGate.cs`
- `NinjaTraderSimAdapter.Ingress.cs`
- `NinjaTraderSimAdapter.Protective.cs`
- `NinjaTraderSimAdapter.BreakEven.cs`
- `NinjaTraderSimAdapter.FlattenCommands.cs`

Shared adapter current sizes:
- Core active tree: main `259`, submit gate `1135`, host/IEA `905`, flatten commands `852`, protective `758`, runtime queues `552`, context `436`, break-even `189`, ingress `97`.
- RobotCore active tree: main `269`, submit gate `1160`, host/IEA `906`, flatten commands `842`, protective `758`, runtime queues `560`, context `448`, break-even `191`, ingress `97`, callback ingress `285`.

Extracted NT adapter partials:
- `NinjaTraderSimAdapter.NT.PlatformHelpers.cs`
- `NinjaTraderSimAdapter.NT.OrderUpdate.cs`
- `NinjaTraderSimAdapter.NT.ExecutionUpdate.cs`
- `NinjaTraderSimAdapter.NT.SubmitEntry.cs`
- `NinjaTraderSimAdapter.NT.SubmitStopEntry.cs`
- `NinjaTraderSimAdapter.NT.ProtectiveSubmit.cs`
- `NinjaTraderSimAdapter.NT.TerminalCancel.cs`
- `NinjaTraderSimAdapter.NT.AccountSnapshot.cs`
- `NinjaTraderSimAdapter.NT.BreakEven.cs`
- `NinjaTraderSimAdapter.NT.Diagnostics.cs`
- RobotCore-only runtime partials: `NinjaTraderSimAdapter.NT.FlattenFills.cs`, `NinjaTraderSimAdapter.NT.Recovery.cs`

NT adapter current sizes:
- RobotCore active tree: main `40`, execution update `1836`, order update `1350`, submit stop entry `1296`, recovery `1206`, flatten fills `1140`, terminal cancel `1095`, platform helpers `1015`, protective submit `910`, submit entry `812`, break-even `727`, diagnostics `362`, account snapshot `142`.
- Core active tree: main `39`, execution update `949`, terminal cancel `955`, protective submit `871`, submit entry `844`, submit stop entry `761`, order update `661`, platform helpers `362`, break-even `177`, account snapshot `141`, diagnostics `122`.

Evidence:
- `system/RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` is now a `40` line shell; the NT bridge is split by platform helper, submit, order update, execution update, flatten fill, protective submit, cancel, account snapshot, break-even, recovery, and diagnostics domains.
- `system/modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` is now a `39` line shell, and `system/modules/robot/core/Robot.Core.csproj` excludes all `Execution\NinjaTraderSimAdapter.NT*.cs` files from the core build.
- This cleanup deliberately preserved NT-thread queueing, cancel/flatten, execution update continuation, unresolved retry, and fail-closed protective behavior.

Cleanup rule:
- Split first, simplify later.
- No removal of fail-closed paths while runtime evidence is only one proven green run after the large move-only split, not multiple clean sessions.

Risk: medium-high.

### Lane 5 - ExecutionJournal Decomposition

Status: active move-only split complete for current cleanup pass.

Extracted partials:
- `ExecutionJournal.Models.cs`
- `ExecutionJournal.Submission.cs`
- `ExecutionJournal.Query.cs`
- `ExecutionJournal.AdoptionRelease.cs`
- `ExecutionJournal.Recovery.cs`
- `ExecutionJournal.Reconciliation.cs`
- `ExecutionJournal.IO.cs`

Current sizes:
- Core active tree: main `259`, submission/fill writes `1572`, adoption/release `1349`, recovery `1028`, reconciliation completion `252`, query `249`, models `244`, IO `130`.
- RobotCore active tree: same line counts.

Evidence:
- `ExecutionJournal.cs` is now a `259` line shell in both active trees.
- The split kept obsolete compatibility entry fill API, legacy timestamp hydration, adoption candidate indexes, repair/close helpers, and normal fill writes intact.
- Core and RobotCore builds pass with `0 Error(s)`.
- Harnesses pass after the split: `EXECUTION_EVENT_REPLAY`, `AUTHORITY_CONTRADICTIONS`, `ORDER_RECONCILIATION`, `RUN_SUMMARY`, `REENTRY_TIMING`, `FORCED_FLATTEN_POLICY`.

Cleanup rule:
- Keep legacy persisted fields and hydration until run artifact compatibility is intentionally migrated.
- Do not remove obsolete members just because compiler says obsolete; they are still compatibility contracts.

Risk: medium.

### Lane 6 - Authority/Feature Flag Cleanup

Status: flag matrix complete; not ready for deletion.

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
- `docs/audits/authority_feature_flag_matrix_2026-04-30.md` documents defaults, isolated playback overrides, and cleanup classification.

Cleanup rule:
- Do not delete legacy/fallback authority paths until fresh run evidence proves the active flags across multiple sessions.
- Keep `CanonicalOwnershipLedgerEnabled`, `UnifiedExecutionAuthorityEnabled`, `UnifiedExecutionAuthorityShadowEnabled`, and `StructuralLayerUseLedgerOwnership` as conditional-remove-later, not safe-remove.

Risk: high if mixed with mechanical cleanup.

### Lane 7 - Health/Logging/Diagnostics Cleanup

Status: active move-only split complete for current cleanup pass.

Extracted partials:
- `HealthMonitor.Connection.cs`
- `HealthMonitor.Data.cs`
- `HealthMonitor.Evaluation.cs`
- `HealthMonitor.Notifications.cs`
- `HealthMonitor.Lifecycle.cs`
- `HealthMonitor.Models.cs` in core only, because RobotCore uses the platform `ConnectionStatus` type.
- `RobotLoggingService.DailySummary.cs`
- `RobotLoggingService.Instance.cs`
- `RobotLoggingService.Queue.cs`
- `RobotLoggingService.Lifecycle.cs`
- `RobotLoggingService.Worker.cs`
- `RobotLoggingService.Dispose.cs`
- `RobotEventTypes.LevelMap.cs`
- `RobotEventTypes.AllEvents.cs`

Evidence:
- Core `HealthMonitor` max partial is `401` lines; RobotCore `HealthMonitor` max partial is `453` lines.
- Core `RobotLoggingService` max partial is `676` lines; RobotCore max partial is `749` lines.
- Core `RobotEventTypes` max partial is `993` lines; RobotCore max partial is `997` lines.
- Core and RobotCore builds pass with `0 Error(s)` after the split.

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

Completed:
- Split shared adapter in both active trees into context, runtime queue, host/IEA, submit gate, ingress, protective, break-even, and flatten command partials.
- Split NT-specific adapter in both active trees into platform helper, submit, order update, execution update, protective submit, break-even, account snapshot, terminal cancel, submit-stop-entry, and diagnostics partials.
- Kept RobotCore-only flatten-fill and recovery logic in dedicated NT partials.
- Added the missing core pending-IEA workload probe used by playback quiescence.
- Restored RobotCore flatten-fill idempotency helper/field needed by the current NT-specific file.
- Updated the core project to exclude `Execution\NinjaTraderSimAdapter.NT*.cs` so NT-only partials do not compile into the net8 core target.

Remaining:
- No required move-only adapter split remains for this pass.
- The latest playback run proved the split runtime path had no crash/freeze/stall/order-thread regression and correctly surfaced flatten exits as operator review. The proof-hardening pass now has engine-start build-signature evidence on DLL hash `7D82321EE84875988D141042...`.

Exit criteria:
- Core + RobotCore builds pass.
- Execution/reconciliation/protective harnesses pass.
- Fresh playback run has no crash/freeze/stall/order-thread violations.

### Batch F - Journal Split

Completed:
- Split `ExecutionJournal.cs` by model, submission/fill writes, query, adoption/release, recovery, reconciliation completion, and IO domains.
- Kept serialized model compatibility unchanged.

Exit criteria:
- Journal/reconciliation/run-summary harnesses pass. Completed.
- Latest run artifacts still load through the run-summary harness path. Completed.

### Batch G - Authority Simplification

Completed:
- Created `docs/audits/authority_feature_flag_matrix_2026-04-30.md`.
- Created `docs/audits/mismatch_authority_next_level_map_2026-05-01.md`.

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
5. Completed: split shared and NT-specific adapter files.
6. Completed: split `ExecutionJournal`.
7. Completed: health/logging diagnostics cleanup and authority flag matrix.
8. Completed: restart NinjaTrader and validate proof-hardening playback on DLL hash prefix `7D82321EE84875988D141042`.
9. Next: checkpoint the diagnostics/payload coordinator split, then continue `MismatchEscalationCoordinator` move-only decomposition by responsibility before any authority behavior simplification.
10. Only then remove authority/fallback/deprecated code with fresh runtime proof.

The system is stable enough for mechanical cleanup. It is not mature enough yet for aggressive deletion of fail-closed, recovery, journal compatibility, or authority fallback paths.
