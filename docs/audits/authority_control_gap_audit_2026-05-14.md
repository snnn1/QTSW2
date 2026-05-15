# QTSW2 Authority Control Gap Audit - 2026-05-14

Scope: focused audit of what the canonical execution authority does not fully control yet.

Evidence level: source-only plus latest run artifact evidence. No new build, harness, playback, SIM/runtime, or deployed-live proof was created in this audit pass.

Latest run checked: `runs/playback_scenario_20260512_20260513_20260514T092937Z`, from `runs/LATEST_RUN.txt`.

Latest run status: `FAIL`, `OPEN_EXPOSURE_AT_SHUTDOWN`, `UNSAFE_EXPOSURE`, `STOP`, 18 trades, 10 errors, broker position qty 0, broker working orders 4, ownership active slots 4, ownership journal open qty 8, incomplete streams 3, crash/freeze signal true. No `AUTHORITY_FRAME`, `UEA_ACTIVE_DENY`, `AUTHORITY_FRAME_DISAGREEMENT`, `SESSION_CLOSE_FLATTEN_AUTHORITY_DENIED`, or `ROBOT_BUILD_SIGNATURE` evidence was found in the latest run logs during this audit. Therefore the current authority source is not runtime-proven by that run.

## Executive Summary

UEA now has broad source-level control hooks, but it is not yet absolute authority over all execution truth.

UEA currently covers these action classes in source:

- entry submit
- market reentry submit
- protective submit
- flatten submit
- cancel submit
- terminal stream commit
- latch create and clear
- session-close global sweep
- journal broker-flat completion through the main reconciliation runner
- mismatch release through engine release readiness
- shutdown safe verdict

The remaining gaps are mostly lower-level mutation surfaces that can still change durable truth if called by a path that has not been routed through UEA. The highest-risk gaps are journal completion/repair APIs, already-committed stream journal rehydration, and direct latch/protective/mismatch internal state mutation.

Bottom line: the architecture is moving toward one authority, but the authority does not fully own all truth mutations yet. Current state is best described as build/harness-level source architecture in progress, not SIM/runtime-proven single authority.

## What UEA Controls Now

| Area | Source evidence | Current control level | Proof level |
|---|---|---:|---|
| Action enum exists for major lifecycle decisions | `system/modules/robot/core/Execution/AuthorityDecisionModels.cs:23-37` | Broad action vocabulary exists | source-only |
| UEA action policy switch exists | `system/modules/robot/core/Execution/UnifiedExecutionAuthority.cs:30` | Central policy entrypoint exists | source-only |
| Entry submit | `UnifiedExecutionAuthority.cs:36`; submit gate activates UEA when enabled at `NinjaTraderSimAdapter.SubmitGate.cs:418` | Conditional final authority | source-only |
| Market reentry | `UnifiedExecutionAuthority.cs:128`; SSM authorizes before journal mutation at `StreamStateMachine.Reentry.cs:242-287`; adapter submit path uses `SUBMIT_MARKET_REENTRY` at `NinjaTraderSimAdapter.SubmitGate.cs:1066-1073` | Main path controlled | source-only |
| Protective submit | `UnifiedExecutionAuthority.cs:222`; protective coordinator checks before corrective submit at `ProtectiveCoverageCoordinator.cs:294-330`, `:450-485` | Corrective request controlled | source-only |
| Flatten submit | `UnifiedExecutionAuthority.cs:259`; adapter flatten guard at `NinjaTraderSimAdapter.SubmitGate.cs:807-929`; session-close flatten request guard at `NinjaTraderSimAdapter.HostIea.cs:759-780`; `FlattenIntentReal` guard at `NinjaTraderSimAdapter.NT.TerminalCancel.cs:289-310` | Main request and real submit paths controlled | source-only |
| Cancel submit | `UnifiedExecutionAuthority.cs:286`; cancel guard at `NinjaTraderSimAdapter.FlattenCommands.cs:332-377`, used by cancel paths at `:425`, `:451`, `:853` | Main cancel paths controlled | source-only |
| Terminal stream commit | `UnifiedExecutionAuthority.cs:293`; SSM checks authority before first commit mutation at `StreamStateMachine.Commit.cs:417-477` | First commit controlled | source-only |
| Session-close global sweep | `UnifiedExecutionAuthority.cs:337`; session sweep evaluates at `RobotEngine.Session.cs:296`, flatten request at `:458` | Sweep decision controlled | source-only |
| Journal broker-flat completion, main runner | `UnifiedExecutionAuthority.cs:364`; `ReconciliationRunner.cs:750-857` | Main runner controlled | source-only |
| Latch create/clear through engine paths | `UnifiedExecutionAuthority.cs:311`, `:412`; create at `RobotEngine.Streams.cs:502-512`, `RobotEngine.Reconciliation.cs:674-684`; clear at `RobotEngine.Reconciliation.cs:537-584`, `:889-926`, `:1061-1109` | Known engine paths controlled | source-only |
| Mismatch release through engine readiness | `UnifiedExecutionAuthority.cs:516`; release wrapper at `RobotEngine.Reconciliation.Release.cs:196-289`; inactive quiesce calls release readiness at `MismatchEscalationCoordinator.AuditLoop.cs:283-305` | Engine-level release controlled | source-only |
| Shutdown safe verdict | `UnifiedExecutionAuthority.cs:573`; summary invokes authority at `RunSummaryBuilder.cs:232-274` | Summary verdict check controlled | source-only |

## Control Gaps

| Gap | File/function | What authority does not fully control | Risk | Next migration |
|---|---|---|---|---|
| Submit authority is feature-flag conditional | `NinjaTraderSimAdapter.SubmitGate.cs:418`; fallback chain at `:460-513`; flags default false at `Execution/FeatureFlags.cs:106-112` | If `UnifiedExecutionAuthorityEnabled` is false or `_unifiedAuthority` is null, EPA/structural/overlay decide final submit behavior and UEA is only shadow or absent. | High until runtime config/deployed hash proves flag state. | After runtime proof, fail closed if UEA is missing for risk-increasing submit paths. |
| Already-committed stream journal can set in-memory `DONE` without fresh UEA check | `StreamStateMachine.Commit.cs:371-379` | If `_journal.Committed` is already true, code mirrors/sets `State = DONE` and returns before current broker/journal/ownership authority reevaluation. | High for stale committed journals/carryover. | Add committed-journal rehydration authority check before setting `DONE`; block or mark contradiction if current frame says exposure open. |
| Carry-forward mirror can commit prior journal rows after authority approves current commit only | `StreamStateMachine.Commit.cs:548`, `:579-646` | Prior journal mirror is mutated as side effect of current commit, not with separate authority frame for the prior journal key. | Medium-high in multi-day/carryover scenarios. | Build a prior-journal authority frame or make mirror update evidence-only unless current and prior lifecycle both prove safe. |
| Terminal commit authority relies partly on SSM-supplied booleans | `StreamStateMachine.Commit.cs:384-430`; UEA policy at `UnifiedExecutionAuthority.cs:293` | UEA uses `HasOpenLifecycleExposureOrPendingReentry`, `HasCompletedTradeForCurrentStream`, and intentional-commit flags from SSM. It does not independently query all broker/journal/ownership truth in that path. | Medium-high. | Promote full frame fields as required for terminal commit: journal open qty, ownership open qty, broker qty, working orders, active reentry. |
| Raw `ExecutionJournal.RecordReconciliationComplete` is not self-authorized | `ExecutionJournal.Reconciliation.cs:112-190` | The method directly sets `TradeCompleted = true` and `CompletionReason = RECONCILIATION_BROKER_FLAT`. The main `ReconciliationRunner` gates it, but the method itself accepts any caller. | Critical if any caller bypasses UEA. | Require an authority decision/token for broker-flat journal completion. |
| Reconciliation repair executor can close journals through raw journal API | `ReconciliationRepairExecutor.cs:89-100` | Repair executor calls `RecordReconciliationComplete` directly. This path was not shown to call `JournalCompleteBrokerFlat` UEA in this audit. | Critical. | Route repair executor through UEA `JOURNAL_COMPLETE_BROKER_FLAT` or remove/disable repair executor until it has authority token. |
| Adoption/release helpers can close or reopen journal rows directly | `ExecutionJournal.AdoptionRelease.cs:1187-1194`, `:1248-1255`, `:1314-1323`, `:1631-1654`; reopen at `:292-391`, `:461-519` | These methods can complete, stale-release, position-align, or reopen journal rows outside UEA. Some are repair/carryover helpers, but they still mutate durable truth. | High. | Classify each helper as validator/executor; require authority token for close/reopen/align mutations. |
| Recovery helpers can mark recovered rows completed directly | `ExecutionJournal.Recovery.cs:251-268`, `:433-437`, `:481-485` | Recovery code can set `TradeCompleted = true` and broker-flat/superseded completion reasons without direct UEA evidence in the mutation method. | High. | Gate recovered-row completion through authority, or restrict methods to controlled callers with explicit decision payload. |
| RiskLatchManager is a raw persistence API | `RiskLatchManager.cs:79`, `:100`, `:137` | `Persist` and `Clear` perform durable latch mutation. Known engine callsites are gated, but the manager itself does not require authority proof. | Medium-high. | Replace raw calls with `PersistAuthorized` / `ClearAuthorized` or require a decision token. |
| Protective coordinator internal block state is independent | `ProtectiveCoverageCoordinator.cs:267`, `:437-441` | Corrective submit and emergency flatten requests go through UEA, but `state.Blocked` can be set/cleared internally by audit status and clean-pass counts. | Medium. | Treat protective block state as validator evidence; emit frame snapshots for block set/clear and route durable/unblock consequences through UEA. |
| Protective emergency flatten state changes happen before/around executor result | `ProtectiveCoverageCoordinator.cs:371-376`, `:524-529` | UEA gates the request, but coordinator sets `EmergencyFlattenTriggered` and `FLATTEN_IN_PROGRESS` after callback result, not after broker flatten confirmation. | Medium. | Split "request accepted" from "flatten confirmed"; authority owns confirmation/clean-flat claim. |
| Mismatch coordinator still mutates internal block state | `MismatchEscalationCoordinator.AuditLoop.cs:298-309` | Inactive quiesce uses release readiness first, but the coordinator still directly sets `Blocked = false`, `EscalationState = NONE`, `GateLifecyclePhase = NONE`. | Medium. | Pass an explicit `MismatchRelease` authority decision/token into coordinator mutation. |
| Adapter real/destructive methods are still private mutation surfaces | `NinjaTraderSimAdapter.NT.TerminalCancel.cs:204-249`, `:289-310`; `NinjaTraderSimAdapter.FlattenCommands.cs:906` | Current public/request paths are guarded, and `FlattenIntentReal` has its own guard. But private real methods can still mutate NT/account state if future code calls them incorrectly. | Medium. | Convert destructive real methods to require a policy-applied token, like the existing flatten token concept in `RuntimeQueues.cs:268-280`. |
| Run summary is a reporter, not runtime authority | `RunSummaryBuilder.cs:52-83`, `:232-274`, `:363-374` | Summary checks shutdown-safe authority and reports unsafe, but it cannot prevent runtime mutation. It can still compute status from aggregate artifacts that may be stale/incomplete. | Medium. | Keep summary as reporter; feed watchdog/operator from authority-frame artifact and freshness fields. |
| Latest run has no authority event proof | Latest run `summary.json`; authority grep returned no matches | Source changes are not proven active in runtime. | Critical for readiness claims. | Rebuild/redeploy, confirm `ROBOT_BUILD_SIGNATURE`, then run controlled SIM and inspect `AUTHORITY_FRAME_SNAPSHOT`/denial events. |

## Action-by-Action Dominance

| Action | Current authority dominance | Main remaining weakness |
|---|---|---|
| `ENTRY_SUBMIT` | Conditional. Dominant only when `UnifiedExecutionAuthorityEnabled` and `_unifiedAuthority` are active. | Legacy EPA/structural/overlay fallback still exists. |
| `REENTRY_SUBMIT` | Main market reentry path is controlled before journal mutation and IEA enqueue. | Any future/recovery reentry-like journal mutation must be audited; submit dominance still feature-flag conditional at adapter level. |
| `PROTECTIVE_SUBMIT` | Corrective protective requests are UEA-gated. | Protective coordinator block/unblock state is still internal validator state. |
| `FLATTEN_SUBMIT` | Stronger now: session-close, IEA enqueue, emergency, and real flatten paths have guards. | Request-side state can still mark flatten progress before broker confirmation; private destructive methods should require tokens. |
| `CANCEL_SUBMIT` | Main cancel paths are UEA-gated. | Real private cancel method is protected by public callers, but not tokenized. |
| `STREAM_COMMIT` | First terminal commit is UEA-gated. | Already-committed journal rehydrate and carry-forward mirror still bypass fresh authority. |
| `JOURNAL_COMPLETE_BROKER_FLAT` | Main `ReconciliationRunner` path is UEA-gated. | Raw journal completion/repair/adoption/recovery helpers can still mutate `TradeCompleted`. |
| `MISMATCH_RELEASE` | Engine release readiness is UEA-wrapped; inactive quiesce uses it. | Coordinator still directly applies internal state changes after readiness. |
| `LATCH_CLEAR` | Known engine clear paths are UEA-gated. | Raw `RiskLatchManager.Clear` remains unauthenticated. |
| `SHUTDOWN_SAFE_VERDICT` | Summary checks UEA shutdown-safe verdict. | Runtime prevention is not affected; stale artifact inputs can still influence reporting. |

## Runtime Evidence Gap

The latest run is failure evidence, not proof of current authority behavior:

- Run root: `runs/playback_scenario_20260512_20260513_20260514T092937Z`
- `summary.json`: `FAIL`, `OPEN_EXPOSURE_AT_SHUTDOWN`, `UNSAFE_EXPOSURE`, `STOP`.
- Open broker position qty was 0, but broker working orders were 4, ownership active slots were 4, ownership journal open qty was 8, and incomplete streams were 3.
- No authority-frame or UEA denial events were found in the latest run logs by this audit pass.
- No runtime `ROBOT_BUILD_SIGNATURE` was found in that run's robot log path during this audit pass.

Therefore: do not use this run to claim UEA dominance. It only proves the system still produced an unsafe/incomplete shutdown in the run artifact family.

## Ranked Next Steps

| Priority | Task | Files | Behavior change? | Proof needed |
|---|---|---|---:|---|
| P0 | Add authority-token requirement for `RecordReconciliationComplete` and repair/executor callers. | `ExecutionJournal.Reconciliation.cs`, `ReconciliationRunner.cs`, `ReconciliationRepairExecutor.cs`, `ExecutionJournal.AdoptionRelease.cs` | YES | build, harness, exact failing SIM/playback shape |
| P0 | Gate already-committed stream journal rehydration before setting `State = DONE`. | `StreamStateMachine.Commit.cs` | YES | reentry/commit/carryover harness, exact failing stream shape |
| P0 | Convert destructive real flatten/cancel methods to require policy-applied token or keep self-guard on every method. | `NinjaTraderSimAdapter.NT.TerminalCancel.cs`, `NinjaTraderSimAdapter.FlattenCommands.cs`, `RuntimeQueues.cs` | YES | flatten/cancel harness + SIM proof |
| P1 | Replace raw latch manager mutation calls with authorized wrappers. | `RiskLatchManager.cs`, `RobotEngine.Reconciliation.cs`, `RobotEngine.Streams.cs` | YES | latch clear/create harness |
| P1 | Make mismatch coordinator internal release consume explicit authority decision, not just release-readiness callback. | `MismatchEscalationCoordinator*.cs`, `RobotEngine.Reconciliation.Release.cs` | YES | mismatch convergence harness |
| P1 | Route protective block clear/set through authority-frame snapshots and separate request accepted vs broker-confirmed flatten. | `ProtectiveCoverageCoordinator.cs` | YES | protective coverage harness |
| P1 | Promote submit UEA from feature-flag conditional to fail-closed required for risk-increasing submit paths. | `NinjaTraderSimAdapter.SubmitGate.cs`, `FeatureFlags.cs`, config/deploy evidence | YES | focused submit harness + deployed SIM |
| P2 | Feed watchdog/dashboard from authority-frame artifacts with proof-level labels. | watchdog/dashboard modules | Possibly | operator snapshot proof |

## Final Verdict

1. UEA is not yet the absolute dominant authority.
2. UEA controls most high-level action requests in source, but raw mutation surfaces still exist below it.
3. The most dangerous remaining bypass is durable journal mutation: `RecordReconciliationComplete` and related recovery/adoption helpers can still mark exposure complete outside a required authority token.
4. The second most dangerous bypass is already-committed stream journal rehydration setting `DONE` without fresh authority.
5. The authority frame is useful and broader than before, but not enough to claim runtime dominance until the raw mutation APIs are tokenized and a deployed SIM run proves authority events and hash.
6. Do not promote or delete legacy guards yet. First make the underlying truth mutations impossible without authority approval.
