# QTSW2 Canonical Authority Implementation Completeness Audit - 2026-05-15

Scope: read-only completeness audit of the current canonical execution authority implementation. This answers whether QTSW2 is actually controlled by one canonical authority frame, or whether legacy/parallel paths can still decide, mutate, flatten, terminalize, release, or report safety independently.

Evidence level: source-only plus latest SIM/runtime evidence from `runs/2b95759a6f23469f8fd1c853d6598c9c`, pointed to by `runs/LATEST_RUN.txt`. Prior build/harness commands in this work session proved the current code builds and selected harnesses pass, but this audit document itself did not create new runtime proof. No deployed-live-proven evidence exists here.

Important context:

- The worktree is dirty and includes current authority changes plus generated build outputs. This audit maps current source; it does not normalize or clean the tree.
- Latest run status is SIM/runtime-proven for the deployed DLL loaded in that run, not live proof.
- The current latest run emitted 810 `AUTHORITY_FRAME_SNAPSHOT` events, 0 `AUTHORITY_FRAME_DISAGREEMENT`, 0 `AUTHORITY_FRAME_UNSAFE_LEGACY_ALLOW`, 0 `UEA_ACTIVE_DENY`, 0 exact ERROR/CRITICAL log levels, and 2 `UNREGISTERED_EVENT_TYPE` warnings.
- The 2 unregistered event warnings were for `EXECUTION_JOURNAL_READ_SKIPPED` and `FLATTEN_VERIFY_RETIRED_LATE_SESSION_CLOSE_CONFIRMED`; current source now registers them at `system/modules/robot/core/RobotEventTypes.AllEvents.cs:119`, `:125` and levels them at `RobotEventTypes.LevelMap.cs:285`, `:323`. That registry fix is source/build/harness-proven, not yet SIM/runtime-proven in a later run.

## Executive Summary

UEA is now the dominant authority for the highest-risk submit path when `FeatureFlags.UnifiedExecutionAuthorityEnabled` is true. The active submit gate returns immediately after UEA allows or denies at `system/modules/robot/core/Execution/NinjaTraderSimAdapter.SubmitGate.cs:418-432`; the old EPA/structural/overlay chain only runs when UEA is not active at `:436-527`. Reentry, flatten, stream commit, journal broker-flat completion, latch clear/create, mismatch release, session-close sweep, protective recovery actions, and shutdown safe verdict all have UEA evaluation points.

The system is not yet a pure single-authority architecture. Several components still mutate execution truth or block state after validator-style checks:

- `MismatchEscalationCoordinator` still owns and mutates mismatch block/release state directly at `MismatchEscalationCoordinator.cs:706-725`, `MismatchEscalationCoordinator.FailClosed.cs:169-185`, and `MismatchEscalationCoordinator.AuditLoop.cs:304-309`.
- `ProtectiveCoverageCoordinator` still owns protective block/recovery state directly at `system/modules/robot/core/Execution/ProtectiveCoverageCoordinator.cs:258-268`, `:437-440`; it routes corrective/flatten actions through UEA but still decides the block state.
- `ExecutionJournal` now requires an allowed UEA broker-flat authority for `RecordReconciliationComplete` at `ExecutionJournal.Reconciliation.cs:113-182`, but other direct `TradeCompleted = true` paths remain for normal fills, rejects, manual override, recovery repair, and internal repair classifications.
- `RunSummaryBuilder` now applies shutdown-safe authority at `system/modules/robot/core/RunSummaryBuilder.cs:49`, `:232-308`, but watchdog/dashboard still consume `summary.json` as read-through display evidence, not a live authority frame.

Final verdict: UEA is dominant over more of the robot than before, but not over everything. The next promotion should be mismatch block/release state ownership and manual/repair journal completion surfaces. Do not delete legacy validators yet.

## Section 1 - Current Authority Architecture

| Subsystem | Current role | Final authority? | Can mutate execution truth? | Can independently block? | Can independently flatten? | Can independently terminalize? | Should remain? | Target future role |
|---|---|---:|---:|---:|---:|---:|---:|---|
| UEA | Central action authority via `UnifiedExecutionAuthority.EvaluateAction` at `UnifiedExecutionAuthority.cs:30` and submit facade `Evaluate` at `:787` | YES, partial | NO | YES | NO | NO | YES | FINAL_AUTHORITY |
| ExecutionAuthorityFrameBuilder | Canonical evidence frame builder at `ExecutionAuthorityFrameBuilder.cs:95` | NO | NO | NO | NO | NO | YES | FRAME_BUILDER |
| EPA | Legacy submit preflight validator at `NinjaTraderSimAdapter.SubmitGate.cs:444` when UEA inactive | NO | NO | YES if UEA inactive | NO | NO | YES | VALIDATOR |
| StructuralLayer | Legacy/validator order and flatten structure checks at `NinjaTraderSimAdapter.SubmitGate.cs:486`, `:877` | NO | NO | YES if UEA inactive or after UEA flatten allow | YES, can block flatten | NO | YES | VALIDATOR |
| Overlay/SafetyGate | Overlay/freshness/hard-lock validator at `NinjaTraderSimAdapter.SubmitGate.cs:504`, `:910`; unsafe lock APIs at `:655`, `:707` | NO | YES, unsafe lock state | YES | YES | NO | YES | VALIDATOR plus lock evidence |
| RiskGate | Legacy stream/operator gate, still feeds freezes/latches indirectly | NO | NO | YES | NO | NO | Temporarily | LEGACY_TO_DEMOTE |
| StreamStateMachine | Lifecycle executor; terminal commit UEA-gated at `StreamStateMachine.Commit.cs:489-690` | Partial | YES, stream journals | NO direct submit block after UEA paths | Requests flatten/reentry | YES | YES | EXECUTOR + EVIDENCE_PROVIDER |
| ReconciliationRunner | Broker/journal/ownership observer; journal broker-flat completion UEA-gated at `ReconciliationRunner.cs:750-842` | Partial | YES, via journal close call | YES through mismatch/release consequences | NO direct | NO | YES | EVIDENCE_PROVIDER + EXECUTOR |
| MismatchEscalationCoordinator | Mismatch block/release state machine | NO | YES, block state | YES | NO | NO | YES | VALIDATOR/REPORTER, then UEA-owned block executor |
| ProtectiveCoverageCoordinator | Protective audit, block, corrective submit, emergency flatten coordinator | NO | YES, protective block/recovery state | YES | YES through UEA-gated emergency flatten callback | NO | YES | VALIDATOR + EXECUTOR |
| QEC | Static protective/pending/convergence state | NO | YES, QEC process state | Indirect | Indirect | NO | YES | EVIDENCE_PROVIDER + VALIDATOR |
| IEA | Command serializer and registry owner | NO | YES, command lifecycle/registry | Indirect supervisory blocks | Executes flatten | NO | YES | EXECUTOR + EVIDENCE_PROVIDER |
| OrderRegistry | Working/adopted order truth | NO | YES, registry/adoption state | Indirect | NO | NO | YES | EVIDENCE_PROVIDER + VALIDATOR |
| ExecutionJournal | Durable journal truth | NO | YES, trade completion/open qty | Indirect | NO | NO | YES | EVIDENCE_PROVIDER + EXECUTOR |
| OwnershipLedger | Exposure explanation ledger | NO | YES, ownership slots/events | Indirect | NO | NO | YES | EVIDENCE_PROVIDER |
| Session-close sweep | Fallback exposure sweep; UEA-gated at `RobotEngine.Session.cs:267-364` | Partial | YES, sweep claims/interrupted stream state | NO | YES, via adapter request at `:458` | NO | YES | EXECUTOR + EVIDENCE_PROVIDER |
| RunSummaryBuilder | Reporter and shutdown-safe verdict writer; UEA-gated at `RunSummaryBuilder.cs:232-308` | Partial | Writes report truth | NO | NO | NO | YES | REPORTER |
| Watchdog | Operator reporter, reads run artifacts | NO | NO runtime mutation for execution truth | NO | NO | NO | YES | REPORTER |

## Section 2 - UEA Dominance Audit

| Action | UEA active? | Legacy override possible? | Legacy mutation possible? | Current dominant authority | Required next step |
|---|---:|---:|---:|---|---|
| `ENTRY_SUBMIT` | YES when `FeatureFlags.UnifiedExecutionAuthorityEnabled`; submit gate returns after UEA allow/deny at `SubmitGate.cs:418-432` | Only if UEA flag false; old chain at `:436-527` | No broker submit before gate | UEA, conditional on flag | Make UEA active outside isolated playback/config-dependent starts, then remove old submit decision mode. |
| `REENTRY_SUBMIT` | YES for market reentry at `StreamStateMachine.Reentry.cs:330-353`; adapter submit also maps `SUBMIT_MARKET_REENTRY` through UEA at `SubmitGate.cs:1066`/`:418` | Some failure/recovery commits still local through `Commit()` calls at `Reentry.cs:189`, `:212`, `:672`, etc. | Journal `ReentryIntentId` and submit-pending mutate after UEA allow at `Reentry.cs:244-281` | UEA for actual enqueue; SSM for lifecycle state | Keep SSM as executor only; audit all non-market reentry/carryover branches. |
| `PROTECTIVE_SUBMIT` | YES through submit gate and coordinator authority at `ProtectiveCoverageCoordinator.cs:296`, `:452`, `:626` | Coordinator still owns block/recovery classification | Yes, protective block state | UEA for corrective submit, coordinator for block lifecycle | Move protective block/unblock decisions to UEA frame verdict after runtime proof. |
| `FLATTEN_SUBMIT` | YES at adapter flatten guard `SubmitGate.cs:807-928`, request-side `NinjaTraderSimAdapter.FlattenCommands.cs:365`, session request `HostIea.cs:759-763` | Structural/overlay can still block after UEA allow at `SubmitGate.cs:877`, `:910` | Flatten state/claims can mutate in session/SSM | UEA plus validators | Keep validators; next audit should prove every request path reaches guard before enqueue/execute. |
| `STREAM_COMMIT` | YES at `StreamStateMachine.Commit.cs:489-690` | Direct startup/hydration `State = DONE` mirrors remain in `StreamStateMachine.cs`; prior carry-forward commit UEA-gated at `Commit.cs:846-876` | Yes, stream journal commit writes | UEA for commit allow, SSM for persistence | Promote as canonical after one more SIM/runtime proof with active reentry and shutdown clean. |
| `JOURNAL_COMPLETE_BROKER_FLAT` | YES in runner at `ReconciliationRunner.cs:750-842` and journal API at `ExecutionJournal.Reconciliation.cs:113-182` | Repair/manual paths can still construct authority locally; manual override is independent | Yes, `TradeCompleted` | UEA for broker-flat completion; journal for normal fill/reject/manual | Split manual override/recovery completion policies into explicit authority actions. |
| `MISMATCH_RELEASE` | YES through `RobotEngine.Reconciliation.Release.cs:104`, `:197-286` | Coordinator still mutates release state after readiness is returned | Yes, block release state | Release evaluator + coordinator, UEA as gate | Make coordinator unable to clear block unless release decision carries UEA allow token. |
| `LATCH_CLEAR` | YES at `RobotEngine.Reconciliation.cs:537-584`, `:889-926`, `:1061-1109` | Public `RiskLatchManager.Clear` still callable, but current callsites are routed through engine authority paths | Yes, latch file delete | UEA + engine executor | Encapsulate `Clear` behind authority-only wrapper or audit all calls in CI. |
| `SHUTDOWN_SAFE_VERDICT` | YES in summary builder at `RunSummaryBuilder.cs:49`, `:232-308` | Summary still computes aggregation first; watchdog consumes summary, not frame | Report mutation only | UEA shutdown verdict plus summary reporter | Watchdog should read/display `AUTHORITY_SHUTDOWN_FRAME.json` next to summary. |

## Section 3 - ExecutionAuthorityFrame Completeness

| Frame field | Current source | Freshness | Reliable? | Missing? | Needed before authority promotion? |
|---|---|---|---:|---:|---:|
| Broker qty | Input field and builder mapping at `AuthorityDecisionModels.cs:127`, `ExecutionAuthorityFrameBuilder.cs:180`; submit frames sample account snapshot | Snapshot-time; latest frame includes captured time | Mostly | No | Already required |
| Broker working orders | `AuthorityDecisionModels.cs:128-133`, builder `ExecutionAuthorityFrameBuilder.cs:181-186` | Snapshot-time | Mostly | No | Already required |
| Stop/target qty | Frame fields at `AuthorityDecisionModels.cs:130-131`; protective builder inputs at `ProtectiveCoverageCoordinator.cs:604-626` | Audit-time | Partial | No | Needed for protective promotion |
| Journal open qty | `AuthorityDecisionModels.cs:134`, builder `ExecutionAuthorityFrameBuilder.cs:187`; journal queries source | Current journal root dependent | Mostly | No | Already required |
| Ownership open/slots | `AuthorityDecisionModels.cs:159-163`, builder `ExecutionAuthorityFrameBuilder.cs:210-215` | Snapshot/event-time | Partial; snapshots can be stale | No | Needed for shutdown/mismatch |
| Stream lifecycle/committed | `AuthorityDecisionModels.cs:165-170`, commit/reentry builders | Current stream-local, durable journal can carry | Partial | No | Needed before deleting SSM guards |
| Active intents | `AuthorityDecisionModels.cs:168-169`, builder `ExecutionAuthorityFrameBuilder.cs:218-219` | In-memory plus input | Partial | No | Needed before entry/reentry promotion |
| Reentry state | `AuthorityDecisionModels.cs:170`, builder `ExecutionAuthorityFrameBuilder.cs:220` | Stream-local/durable hints | Partial | No | Needed before reentry/carryover promotion |
| Registry working count | `AuthorityDecisionModels.cs:141-143`, builder `ExecutionAuthorityFrameBuilder.cs:193-196` | In-memory only | Partial | No | Needed; registry drift remains a known risk |
| Mismatch state | `AuthorityDecisionModels.cs:182-183`, builder `ExecutionAuthorityFrameBuilder.cs:230-232` | Coordinator state | Partial | No | Needed before mismatch release promotion |
| Protective state | `AuthorityDecisionModels.cs:173-175`, builder `ExecutionAuthorityFrameBuilder.cs:222-224` | Audit-time | Partial | No | Needed before protective block migration |
| QEC state | `AuthorityDecisionModels.cs:176-178`, builder `ExecutionAuthorityFrameBuilder.cs:225-227` | Static process state | Partial/stale after restart | No | Needed before protective/entry promotion |
| Latches | `AuthorityDecisionModels.cs:180-181`, builder `ExecutionAuthorityFrameBuilder.cs:228-229` | Durable file state | Mostly | No | Needed before latch clear promotion |
| Session/timetable | Frame has `TimetableAllowed`, `SessionCloseState` at `AuthorityDecisionModels.cs:188-189`; many frames leave `timetable_allowed=false` by default | Engine-local | Partial | Needs fuller population | YES for live scheduling authority |
| Kill switch | `AuthorityDecisionModels.cs:186`, builder `ExecutionAuthorityFrameBuilder.cs:234` | cached per `KillSwitch` policy | Mostly | No | Already required |
| Mode/playback context | `AuthorityDecisionModels.cs:190-192`, builder `ExecutionAuthorityFrameBuilder.cs:237-239` | Run config | Partial | Multi-day semantics still not live proof | YES for playback/live divergence |
| Runtime hash/proof | `AuthorityDecisionModels.cs:194`, builder `ExecutionAuthorityFrameBuilder.cs:241`; latest `ROBOT_BUILD_SIGNATURE` proves DLL hash in logs | Only when runtime emits signature | Missing in most action frames | YES, field often empty | YES for deployed proof/reporting |
| Derived clean/contradiction predicates | `ExecutionAuthorityFrameBuilder.cs:122-155`, `:242-247`, predicate method at `:349` | Derived from mixed inputs | Useful but can be semantically noisy | No | Improve before deleting validators |

Frame gap observed in latest run: protective submit frames in `robot_CL.jsonl` show `broker_qty=0`, `journal_open_qty=0`, `is_clean_flat=True`, and also `has_tracked_exposure=True` because IEA/QEC pending evidence was present. That is not necessarily unsafe, but it proves the derived field names still need sharper semantics before the frame alone replaces all validators.

## Section 4 - Legacy Bypass Audit

| Legacy path | File/function | What it mutates | Why dangerous | Current guard | Required migration step |
|---|---|---|---|---|---|
| Mismatch main release | `MismatchEscalationCoordinator.cs:706-725` | Clears `state.Blocked`, block reason, lifecycle, counters | Can unblock entries if readiness source is wrong | Readiness callback is UEA-gated through `RobotEngine.Reconciliation.Release.cs:197-286` | Require explicit UEA decision object at mutation site. |
| Fail-closed release | `MismatchEscalationCoordinator.FailClosed.cs:169-185` | Clears fail-closed state | Same unblock risk | Calls `_evaluateReleaseReadiness`; strict mode can require multi-snapshot readiness | Pass UEA release token, not just readiness result. |
| Inactive-scope quiesce release | `MismatchEscalationCoordinator.AuditLoop.cs:304-309` | Clears stale block state | Could clear hidden mismatch if scope detector is wrong | Calls `_evaluateReleaseReadiness` and requires release ready | Route mutation through UEA release token. |
| Protective block/unblock | `ProtectiveCoverageCoordinator.cs:258-268`, `:437-440` | Sets/clears protective instrument block | Can block entries or clear protection block outside canonical decision | Corrective submit/flatten actions are UEA-gated | Move block clear/create to UEA action types. |
| Manual unsafe lock clear | `NinjaTraderSimAdapter.SubmitGate.cs:707-759` | Clears overlay unsafe lock | Operator action can change safety state outside UEA | Structural baseline + overlay fresh check | Add UEA `OVERLAY_LOCK_CLEAR` or `SAFETY_LOCK_CLEAR`. |
| Direct manual journal override | `ExecutionJournal.Reconciliation.cs:70-90` | Sets `TradeCompleted=true` for manual override | Can hide exposure if misused | Manual override function-local checks only | Add explicit `JOURNAL_COMPLETE_MANUAL_OVERRIDE` authority action or fence operator-only. |
| Recovery/journal repair completions | `ExecutionJournal.Recovery.cs:251`, `:267`, `:436`, `:893` | Marks journal complete in recovery paths | Can repair truth incorrectly if evidence stale | Some broker-flat recovery now uses `EvaluateBrokerFlatJournalCompletionAuthority` at `:486-498` | Classify each completion reason and route safety-affecting ones through UEA. |
| Normal submit/reject completion | `ExecutionJournal.Submission.cs:1048`, `:1414` | Completes rejected/canceled/non-fill rows | Lower risk but still terminal truth | Normal lifecycle rules | Keep as executor, but include in frame snapshots for audit. |
| Session sweep claims | `RobotEngine.Session.cs:408-523` | Marks/clears sweep requested claims | Can leave stale flatten state | UEA checks before request at `:294-364` | Keep but add post-mutation frame snapshot on claim clear. |
| Watchdog summary read-through | `system/modules/watchdog/backend/routers/watchdog.py:87-107`, `run_artifacts.py:210` | Operator truth display | Can omit authority-frame evidence | Summary is UEA-gated now | Read and expose `AUTHORITY_SHUTDOWN_FRAME.json`. |

## Section 5 - Contradiction Audit

| Contradiction | Legacy decision | Canonical frame decision | Evidence mismatch | Severity | Required fix |
|---|---|---|---|---|---|
| None emitted in latest run | No `AUTHORITY_FRAME_DISAGREEMENT` or `AUTHORITY_FRAME_UNSAFE_LEGACY_ALLOW` in latest SIM run | 810 frame snapshots, zero disagreements | Latest run clean from authority disagreement perspective | None for that run | Continue collecting frames in next SIM run. |
| Event registry warnings | Runtime logger warned on two unregistered events | Not an authority decision | `EXECUTION_JOURNAL_READ_SKIPPED`, `FLATTEN_VERIFY_RETIRED_LATE_SESSION_CLOSE_CONFIRMED` unregistered in deployed DLL | Noisy/evidence-quality | Fixed in current source; redeploy and confirm zero warnings. |
| Protective frame semantics | Legacy/protective action allowed; frame says `is_clean_flat=True` and `has_tracked_exposure=True` in some protective submit frames | UEA allowed | QEC/IEA pending evidence can mark tracked exposure while broker/journal qty are zero | Noisy, could become safety-critical if used for deletion | Rename/split derived predicates or tighten `IsCleanFlat` semantics before deleting validators. |
| Mismatch release mutation | Coordinator clears state after readiness | UEA only gates readiness source indirectly | Mutation site does not require UEA decision object | Safety-critical potential | Make UEA release token mandatory at coordinator mutation. |

## Section 6 - Flatten/Reentry Authority Audit

| Lifecycle step | Current authority | Possible bypass | Safe? | Required fix |
|---|---|---|---:|---|
| Forced/session flatten request | Session sweep builds frame and calls UEA at `RobotEngine.Session.cs:267-364`; adapter request calls flatten guard at `NinjaTraderSimAdapter.HostIea.cs:759-763` | Engine can still mark sweep claims/interrupted streams | Mostly YES | Add post-claim frame/evidence and keep sweep mutation executor-only. |
| Flatten enqueue/execute | Guarded by `TryExecutionSafetyFlattenGuard` at `SubmitGate.cs:807-928`; runtime queue blocks flatten actions at `RuntimeQueues.cs:335`, `:544` | Structural/overlay still decide after UEA allow | YES as validators | Keep validators until SIM proof shows no false blocks. |
| Market reentry enqueue | UEA-gated before journal `ReentryIntentId` and enqueue at `StreamStateMachine.Reentry.cs:244-281`, authority call at `:330-353` | Failure/carryover paths still call `Commit()` locally | Mostly YES | Audit non-market/carryover branches and prove with runtime. |
| Stream terminalization | UEA-gated `Commit()` at `StreamStateMachine.Commit.cs:489-690`; prior carry-forward UEA-gated at `:846-876` | Startup rehydration sets runtime `State=DONE` from existing committed journal | Mostly YES | Add rehydration frame audit, not behavior change. |
| Broker-flat journal completion | UEA-gated through `RecordReconciliationComplete` at `ExecutionJournal.Reconciliation.cs:113-182` | Manual/recovery direct completion paths remain | Partial | Split completion reasons and authority actions. |
| Shutdown convergence | `RunSummaryBuilder` writes `AUTHORITY_SHUTDOWN_FRAME.json` at `RunSummaryBuilder.cs:308` | Watchdog still reads summary only | Mostly YES for summary, NO for operator frame visibility | Watchdog should surface shutdown frame. |

## Section 7 - Protective Authority Audit

| Action | Current authority | Wrongly blocked possible? | Correct future behavior |
|---|---|---:|---|
| Protective stop/target submit | UEA action `ProtectiveSubmit` allows through entry/mismatch blocks unless snapshot/recovery execution disallows at `UnifiedExecutionAuthority.cs:222-254`; submit gate uses UEA active path | Low; structural/overlay can still block | Protective coverage should bypass ordinary entry latches, but still fail closed on invalid structure/stale snapshot. |
| Corrective protective submit | Coordinator checks UEA before `_submitCorrective` at `ProtectiveCoverageCoordinator.cs:296-328`, `:452-469` | Medium if frame lacks broker/order evidence | Keep UEA gate and add aggregate broker/protective frame fields to every corrective action. |
| Emergency protective flatten | Coordinator checks UEA before `_emergencyFlatten` at `ProtectiveCoverageCoordinator.cs:351-377`, `:504-534` | Low; flatten guard also validates | Safety flatten should bypass entry blocks; only stale snapshot/structural unsafe state should deny. |
| Protective block create/clear | Coordinator mutates `state.Blocked` directly at `:258-268`, `:437-440` | YES | UEA should decide block create/clear; coordinator executes. |
| Protective under-coverage canonicality | Frame includes protective status/missing qty at `AuthorityDecisionModels.cs:173-175` and coordinator frame at `ProtectiveCoverageCoordinator.cs:604-626` | Partially | Make aggregate per-instrument stop/target coverage a first-class frame invariant before removing old audit. |

## Section 8 - Shutdown Safe Truth Audit

| Subsystem | Can claim safe? | Uses canonical frame? | Risk of disagreement | Required consolidation |
|---|---:|---:|---|---|
| RunSummaryBuilder | YES | YES at `RunSummaryBuilder.cs:232-308` | Low-medium; aggregation happens before frame | Keep frame as final verdict source and include authority denial in summary fields. |
| RobotEngine.Stop | Writes run artifacts and invokes summary | Indirect | Medium if shutdown snapshot stale | Emit final live broker/account snapshot freshness into shutdown frame. |
| Watchdog/backend | Displays summary via `watchdog.py:87-107` and `run_artifacts.py:210` | NO direct frame read | Medium operator visibility gap | Read/display `AUTHORITY_SHUTDOWN_FRAME.json`. |
| ReconciliationRunner | Can close broker-flat journals | YES for broker-flat completion | Medium from manual/repair paths | Route all non-fill safety completions through explicit UEA actions. |
| Authority frame | Can allow/deny shutdown safe verdict | YES | Frame fields may be stale/empty for runtime hash/mode | Populate runtime signature/proof fields before final operator-ready claim. |

Latest SIM shutdown proof: `summary.json` reports `WARN`, `FLATTEN_OCCURRED`, 12 trades, zero errors/criticals, broker qty 0, broker working orders 0, ownership/journal open qty 0, incomplete streams 0. `AUTHORITY_SHUTDOWN_FRAME.json` allowed `SHUTDOWN_SAFE_VERDICT` with `is_clean_flat=true`. This is SIM/runtime-proven for that deployed DLL only.

## Section 9 - Promotion Readiness

| Action | Readiness | Why | Missing proof | Runtime risk |
|---|---|---|---|---|
| `STREAM_COMMIT` | READY_AFTER_RUNTIME_PROOF | Commit path is UEA-gated and harnessed | More SIM proof with reentry/carryover | Medium |
| `JOURNAL_COMPLETE_BROKER_FLAT` | READY_AFTER_RUNTIME_PROOF | Runner and API require UEA broker-flat authority | Manual/recovery completion classification audit | High |
| `LATCH_CLEAR` | READY_AFTER_RUNTIME_PROOF | Current engine callsites are UEA-gated | Encapsulation of `RiskLatchManager.Clear` | Medium |
| `SHUTDOWN_SAFE_VERDICT` | READY_NOW for summary reporting, READY_AFTER_RUNTIME_PROOF for operator UI | Summary emits authority frame | Watchdog frame read-through | Medium |
| `ENTRY_SUBMIT` | READY_AFTER_RUNTIME_PROOF | Active UEA can dominate submit gate | Prove feature flag active in all intended modes | High |
| `REENTRY_SUBMIT` | READY_AFTER_RUNTIME_PROOF | Market path UEA-gated | Non-market/carryover branch audit | High |
| `PROTECTIVE_SUBMIT` | NOT_READY for deleting old validators | UEA allows safety actions; old audit still essential | Aggregate coverage invariant in frame | High |
| `FLATTEN_SUBMIT` | NOT_READY for deleting structural/overlay validators | UEA gates flatten but validators still catch stale/unsafe structure | Request-side proof across all callers | High |
| `MISMATCH_RELEASE` | NOT_READY | UEA gates readiness but coordinator still mutates state | UEA release token at mutation site | High |

## Section 10 - Remaining Gaps Before One Authority

| Gap | Subsystem | Severity | Runtime risk | Required fix |
|---|---|---|---|---|
| Mismatch coordinator mutates block/release state internally | Mismatch | HIGH | Unsafe release or stale block | Require UEA decision object for every release/unblock mutation. |
| Protective block state remains coordinator-owned | Protective | HIGH | Entry block can drift from canonical frame | Add UEA block create/clear actions. |
| Runtime hash/proof fields are mostly empty in action frames | Frame/proof | MEDIUM | Operator may assume wrong DLL proof | Populate from runtime signature cache in frame builder. |
| Timetable/session fields are sparsely populated | Frame/session | MEDIUM-HIGH | Live rollover authority remains incomplete | Populate timetable/session authority on stream and submit frames. |
| Manual unsafe lock clear outside UEA | Overlay/SafetyGate | MEDIUM | Operator clear may bypass canonical frame | Add safety-lock clear authority action. |
| Direct manual/recovery `TradeCompleted=true` paths | Journal/recovery | HIGH | Journal truth can be repaired without enough evidence | Explicit authority actions per completion reason. |
| Watchdog does not consume authority shutdown frame | Watchdog/operator | MEDIUM | UI may omit canonical proof context | Add read-through and display proof level/frame verdict. |
| Playback/live mode proof divergence | Playback/runtime | HIGH | Playback pass can be overvalued | Keep proof discipline; require SIM runtime hash proof after deploy. |
| Derived frame semantics are still noisy | Frame builder | MEDIUM | Validators may be deleted too early | Split `IsCleanFlat` from pending-alignment/tracked-exposure states. |

## Section 11 - Exact Next Migration Steps

| Phase | Task | Files | Behavior change? | Proof needed | Rollback plan |
|---|---|---|---:|---|---|
| P0 | Add watchdog read-through for `AUTHORITY_SHUTDOWN_FRAME.json` | `system/modules/watchdog/run_artifacts.py`, router/frontend | NO execution behavior | Python tests | Revert UI/backend read-through only. |
| P0 | Add frame snapshot for stream rehydration/carryover state decisions | `StreamStateMachine.cs`, `StreamStateMachine.Commit.cs` | NO | harness + next SIM logs | Remove log emission only. |
| P0 | Populate runtime signature/proof fields in frame builder inputs where available | engine/runtime signature cache, frame builder callsites | NO behavior | build + frame snapshot inspection | Revert field plumbing. |
| P1 | Make mismatch release mutation require UEA allow token | `MismatchEscalationCoordinator*.cs`, `RobotEngine.Reconciliation.Release.cs` | YES | `AUTHORITY_CONTRADICTIONS`, state consistency tests, SIM | Revert token enforcement. |
| P1 | Add UEA actions for protective block create/clear | `UnifiedExecutionAuthority.cs`, `ProtectiveCoverageCoordinator.cs` | YES | protective audit tests, SIM | Revert to coordinator-owned block state. |
| P1 | Fence manual unsafe-lock clear through UEA | `NinjaTraderSimAdapter.SubmitGate.cs`, `ExecutionSafetyGate.cs` | YES | execution safety gate tests | Revert operator-clear wrapper. |
| P2 | Classify all journal `TradeCompleted=true` paths by authority policy | `ExecutionJournal.*.cs` | Mixed | journal/reconciliation/recovery tests | Revert per completion category. |
| P2 | Promote watchdog/operator safe verdict to consume authority frame | watchdog backend/frontend | NO execution behavior | watchdog operator tests | Revert display path. |
| P3 | Delete/demote legacy submit path only after multiple clean SIM/runtime proofs | EPA/structural/overlay callsites | YES | focused harness + SIM + deployed hash proof | Restore old callsites. |

## Section 12 - Final Verdict

1. Is UEA truly the dominant authority yet?  
   Partially. It is dominant for active submit gating, flatten guard, market reentry, session sweep, stream commit, broker-flat journal completion, latch clear/create, mismatch release readiness, protective action requests, and shutdown safe verdict. It is not yet the only owner of block state or all truth mutations.

2. What decisions are still controlled by legacy paths?  
   Mismatch block/release state mutation, protective block lifecycle, overlay unsafe-lock clear, some recovery/manual journal completion paths, watchdog/operator safety display, and some stream rehydration/carryover state mirroring.

3. Can legacy logic still independently mutate truth?  
   Yes. Mismatch, protective, journal, overlay lock, and stream journal executors can still mutate state. Some are correctly UEA-gated; some still rely on local validators/readiness.

4. Is the frame complete enough for real authority promotion?  
   It is complete enough for continued shadowing and selected low-risk promotions. It is not complete enough to delete protective, structural, overlay, mismatch, or journal validators.

5. What is the most dangerous remaining bypass?  
   Mismatch release/block mutation outside a mandatory UEA decision token, because it can unblock risk-increasing submits after a stale or incomplete readiness result.

6. Which authority promotion should happen next?  
   Mismatch release tokenization first, then protective block create/clear authority, then watchdog shutdown-frame visibility.

7. What should absolutely not be promoted yet?  
   Do not delete structural/overlay/protective validators or make the frame the sole basis for protective/flatten safety until aggregate protective coverage and snapshot freshness are stronger in runtime proof.

8. How close is the system to true single-authority architecture?  
   Approximately two-thirds of the critical surfaces are routed through UEA, but the final third is the risky part: state mutation ownership. The next work should not add more gates; it should make existing mutators require explicit UEA decision objects.
