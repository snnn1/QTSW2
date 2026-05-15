# QTSW2 Canonical Authority Implementation Completeness Audit - 2026-05-14

Scope: implementation-completeness audit for whether QTSW2 has one canonical execution authority, one canonical authority frame, validators feeding authority, and executors obeying authority.

Evidence level:

- Source-only for current dirty source inspection.
- Harness-proven for `AUTHORITY_FRAME` and `AUTHORITY_CONTRADICTIONS`, both run on 2026-05-14 in this audit pass.
- Playback/SIM runtime-proven for latest run evidence only: `runs/playback_scenario_20260512_20260513_20260514T092937Z`.
- No deployed-live-proven evidence.
- Latest run proved loaded DLL hash `f77f6e81e0a05724084dbfc76c0990b4de30356c26836ef6a1ed0d8910c55135` at `C:\Users\jakej\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`, last write `2026-05-14T03:27:58.6236197Z`.
- Current deployed DLL hash at audit time was `06FDDED72119AB992A071166B25D6599B5543127C905FA3321F49873C4B6090E`, last write `2026-05-14T12:11:46.6972181Z`.
- Current net48 build output hash at audit time was `61B71F5397180BD652161A4559ECF2FE18C292BBB430E82E647F19992471D150`.
- Therefore latest run evidence does not prove the current dirty source/build/deployed state.

Latest run summary:

- Run: `runs/playback_scenario_20260512_20260513_20260514T092937Z`.
- `summary.json`: `FAIL`, `UNSAFE_EXPOSURE`, `STOP`, `OPEN_EXPOSURE_AT_SHUTDOWN`.
- Counts: 18 trades, 10 errors, 0 robot criticals, broker position qty at shutdown 0, broker working orders 4, journal open qty 8, ownership active slots 4, incomplete streams 3.
- Authority logs: 1,223 `AUTHORITY_FRAME_SNAPSHOT`; 1,141 `MISMATCH_RELEASE`, 14 `STREAM_COMMIT`, 68 blank/submit-path snapshots; 1,106 allow, 49 deny, 68 blank decisions.
- No `AUTHORITY_FRAME_DISAGREEMENT`, `AUTHORITY_FRAME_UNSAFE_LEGACY_ALLOW`, `UEA_ACTIVE_DENY`, or `REENTRY_BLOCKED_AUTHORITY_DENIED` in latest run logs.

## Executive Summary

UEA is not yet the truly dominant authority over the robot.

It is dominant for several promoted paths when feature flags are enabled: entry submit, market reentry submit, flatten guard, stream terminal commit, session-close global sweep, reconciliation-runner broker-flat journal completion, mismatch release readiness output, latch create/auto-clear, and run-summary shutdown-safe verdict.

But legacy/parallel paths still mutate execution truth or safety state outside a fully canonical frame:

- Release readiness can reconcile/close broker-flat journal rows before UEA evaluates `MISMATCH_RELEASE`.
- ExecutionJournal has multiple reconciliation/recovery/manual completion paths that mutate `TradeCompleted` without a direct UEA decision.
- ProtectiveCoverageCoordinator can set protective block/recovery state and trigger emergency flatten through callbacks without its own canonical protective authority frame.
- Explicit unfreeze and interrupted-session reentry bypass can clear latches/frozen state without `LatchClear` authority.
- IEA queue poison, registry lifecycle, and adoption mutate execution state as executor/evidence paths, not UEA-governed final authority.
- RunSummaryBuilder now uses UEA for `SHUTDOWN_SAFE_VERDICT`, but it still derives facts from run artifacts and stale ownership snapshots can affect counts.
- Watchdog reports/read-through status but does not yet consume canonical authority frames as the final safety truth.

The frame is directionally good but not complete enough to delete legacy guards. It is complete enough to promote more narrow safety decisions only after current deployed runtime proof.

## Section 1 - Current Authority Architecture

| Subsystem | Current role | Final authority? | Can mutate execution truth? | Can independently block? | Can independently flatten? | Can independently terminalize? | Should remain? | Target future role |
|---|---|---:|---:|---:|---:|---:|---|---|
| UEA | Submit facade plus action authority in `UnifiedExecutionAuthority.EvaluateAction` and `Evaluate` at `system/modules/robot/core/Execution/UnifiedExecutionAuthority.cs:30`, `:779` | Partial YES | No direct mutation | YES, through callers | YES, through flatten guard caller | YES, through commit caller | YES | FINAL_AUTHORITY |
| ExecutionAuthorityFrameBuilder | Builds derived frame in `ExecutionAuthorityFrameBuilder.Build` at `ExecutionAuthorityFrameBuilder.cs:96`; derives predicates at `:340` | NO | No | No | No | No | YES | FRAME_BUILDER |
| EPA | Entry-path preflight validator in `ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight` at `ExecutionPermissionAuthority.cs:48` | NO | No | YES | No | No | YES | VALIDATOR |
| StructuralLayer | Structural validator in `ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure` at `ExecutionStructuralLayer.cs:430`; flatten structure at `:875` onward | NO | Can clear QEC recovery in narrow paths | YES | Can block flatten | No | YES | VALIDATOR |
| Overlay/SafetyGate | Overlay lock/freshness validator in `ExecutionSafetyGate.TryEvaluateExecutionOverlay` at `ExecutionSafetyGate.cs:124`; flatten idempotency at `:112` | NO | Can mutate overlay lock/idempotency | YES | Can block repeated flatten | No | YES | VALIDATOR |
| RiskGate | Legacy stream-attributed risk gate in `RiskGate.CheckGates` at `Execution/RiskGate.cs:47` | NO | No | YES | No | No | Temporarily | LEGACY_TO_DEMOTE |
| StreamStateMachine | Lifecycle requester/executor; terminal commit goes through UEA at `StreamStateMachine.Commit.cs:417`; reentry goes through UEA at `StreamStateMachine.Reentry.cs:330` | Partial | YES, stream journal and state | YES, local lifecycle gates | Can request flatten on slot expiry | YES | YES | EXECUTOR + EVIDENCE_PROVIDER |
| ReconciliationRunner | Reconciliation observer/executor; journal broker-flat completion goes through UEA at `ReconciliationRunner.cs:750` | Partial | YES, journals and ownership close | YES indirectly via mismatch observations | No direct flatten | Can complete journals | YES | EVIDENCE_PROVIDER + EXECUTOR |
| MismatchEscalationCoordinator | Mismatch block/release owner; release readiness is wrapped by UEA in engine release path at `RobotEngine.Reconciliation.Release.cs:242` | Partial | YES, mismatch state | YES | No direct flatten | No | YES | VALIDATOR + EXECUTOR after UEA |
| ProtectiveCoverageCoordinator | Protective validator/recovery state machine at `ProtectiveCoverageCoordinator.cs:221`; block at `:390`; emergency flatten callback at `:325`, `:432` | NO | YES, protective state | YES | YES, through callback | No | YES | VALIDATOR + EXECUTOR after UEA |
| QEC | Pending/alignment/recovery evidence and block state | NO | YES, static QEC state | YES through structural/protective | No | No | YES | EVIDENCE_PROVIDER + VALIDATOR |
| IEA | Per-instrument command serializer at `InstrumentExecutionAuthority.cs:33`; queue block at `:558`; worker poison at `:415` | NO | YES, registry/lifecycle/commands | YES | YES, as executor | No | YES | EXECUTOR + EVIDENCE_PROVIDER |
| OrderRegistry | Working/adopted order truth at `InstrumentExecutionAuthority.OrderRegistry.cs:122`, adoption at `:468` | NO | YES, registry rows | Indirect | No | No | YES | EVIDENCE_PROVIDER |
| ExecutionJournal | Durable trade truth; reconciliation completion at `ExecutionJournal.Reconciliation.cs:112`; release reconciliation at `ExecutionJournal.AdoptionRelease.cs:1140` | NO | YES | Indirect | No | YES, journal complete | YES | EVIDENCE_PROVIDER + UEA-GATED EXECUTOR |
| OwnershipLedger | Broker exposure explanation snapshot at `InstrumentOwnershipLedger.cs:320` | NO | YES, ownership slots/events | Indirect | No | Indirect close | YES | EVIDENCE_PROVIDER |
| Session-close sweep | Broker-level sweep path at `RobotEngine.Session.cs:151`; UEA at `:294`; request at `:480` | Partial YES | YES, sweep claims/stream interruption | YES | YES | No | YES | EXECUTOR after UEA |
| RunSummaryBuilder | Reporter with shutdown UEA check at `RunSummaryBuilder.cs:227`, `:257` | NO | Writes summary only | No runtime block | No | No | YES | REPORTER |
| Watchdog | Operator reporter/status aggregator, read-through summary at `watchdog.py:87`; status safety fields in `aggregator_main.py:3685` | NO | No robot truth mutation | Operator/reporting only | No | No | YES | REPORTER |

## Section 2 - UEA Dominance Audit

| Action | UEA active? | Legacy override possible? | Legacy mutation possible? | Current dominant authority | Required next step |
|---|---:|---:|---:|---|---|
| `ENTRY_SUBMIT` | YES when `UnifiedExecutionAuthorityEnabled`; active adapter call at `NinjaTraderSimAdapter.SubmitGate.cs:418` | Legacy can still deny after UEA via structural/overlay at `:486` and `:525` family | Submit mutation blocked after UEA deny | UEA deny dominates; legacy validators also deny | Keep; collect deployed runtime proof after current hash deploy |
| `REENTRY_SUBMIT` | YES in SSM at `StreamStateMachine.Reentry.cs:330`; adapter submit path also resolves market reentry at `UnifiedExecutionAuthority.cs:704` | Legacy/local SSM gates can still deny | No enqueue after SSM UEA deny in current source | UEA plus SSM lifecycle validators | READY_AFTER_RUNTIME_PROOF |
| `PROTECTIVE_SUBMIT` | YES in submit path via `ResolveSubmitAuthorityAction` at `UnifiedExecutionAuthority.cs:707` | Structural can still deny; protective coordinator can trigger corrective submit independently | Corrective state mutates before/around submit callback | UEA for adapter submit, not coordinator recovery | Add protective authority frame at coordinator recovery decision |
| `FLATTEN_SUBMIT` | YES in adapter flatten guard at `NinjaTraderSimAdapter.SubmitGate.cs:837` | Structural/overlay/idempotency can still deny at `:877`, `:913` | Protective coordinator and session sweep can request flatten, but adapter guard should govern execution | UEA in adapter guard | Promote request-side frames in protective/forced-flatten paths |
| `STREAM_COMMIT` | YES at `StreamStateMachine.Commit.cs:417` | Commit caller still computes local facts; prior journal mirroring at `:628` is local | YES, commit persists stream journal after allow | UEA for main terminal commit | Keep and add runtime denial proof |
| `JOURNAL_COMPLETE_BROKER_FLAT` | YES in ReconciliationRunner path at `ReconciliationRunner.cs:750` | YES: release readiness and recovery helpers can still close/reopen rows outside this action | YES | Split: UEA in runner, legacy in journal/release recovery paths | Migrate release readiness journal closures behind same action |
| `MISMATCH_RELEASE` | YES wrapper at `RobotEngine.Reconciliation.Release.cs:242` | Validator cannot override allow; UEA denial forces release not ready at `:284` | YES: `BuildStateConsistencyReleaseEvaluationInput` can mutate journals before UEA at `:348`, `:368` | UEA for release result, legacy for pre-release repairs | Stop write-side repairs before UEA; make repair requests evidence/commands |
| `LATCH_CLEAR` | YES for auto-clear at `RobotEngine.Reconciliation.cs:806` | YES: explicit unfreeze clears latch at `RobotEngine.Reconciliation.cs:930`; stale reentry bypass clears at `:501` | YES | Partial UEA for auto-clear only | Route all clear paths through `LatchClear` or explicit operator-clear action |
| `SHUTDOWN_SAFE_VERDICT` | YES in RunSummaryBuilder at `RunSummaryBuilder.cs:257` | Summary still applies legacy reason order at `:322`, `:331`; watchdog can report independently | Summary writes only | UEA contributes; reporter still aggregates | Emit shutdown authority frame to run artifacts and watchdog |

## Section 3 - ExecutionAuthorityFrame Completeness

| Frame field | Current source | Freshness | Reliable? | Missing? | Needed before authority promotion? |
|---|---|---|---:|---:|---:|
| Broker qty | Builder input at `ExecutionAuthorityFrameBuilder.cs:21`; sampled by caller | Snapshot-time only | Medium | No | YES |
| Broker working orders | Builder input at `:22`; submit path samples account snapshot | Snapshot-time only | Medium | No | YES |
| Stop/target qty | Builder input at `:23-24` | Often placeholder zero in some callers | Low/Medium | Partially | YES for protective promotion |
| Journal open qty | Builder input at `:27`; release/summary/SSM supply from journal | Current to journal root | Medium | No | YES |
| Ownership open/slots | Builder input at `:58-61`; fallback from snapshot at `:109-118` | Snapshot/event freshness varies | Medium | No | YES |
| Stream lifecycle | Builder input at `:63-68` | Caller-local | Medium | No | YES for commit/reentry |
| Active intents | Builder input at `:69-70`; derived count at `:119` | Often placeholder in submit frames | Low/Medium | Partially | YES |
| Reentry state | Builder input at `:71`; open-state derivation at `:438` | Caller-local | Medium | No for SSM path | YES |
| Registry working count | Builder input at `:36-38` | In-memory IEA only | Medium | No | YES |
| Mismatch state | Builder input at `:82-83` | Coordinator state | Medium | No | YES |
| Protective state | Builder input at `:73-75` | Often not populated outside protective audit | Low | YES | YES for protective promotion |
| QEC state | Builder input at `:76-78` | Static/process state | Medium | Partial | YES |
| Latches | Builder input at `:80-81` | Durable but caller scoped | Medium | No | YES |
| Session/timetable | Builder input at `:88-89` | Often placeholder false/empty in submit frames | Low | Partial | YES for entry/reentry |
| Kill switch | Builder input at `:87`; UEA entry checks preflight kill switch at `UnifiedExecutionAuthority.cs:55` | Preflight sampled | Medium | No | YES |
| Mode/playback | Builder input at `:91-93` | Caller supplied | Low/Medium | Partial | YES for playback/live divergence |
| Runtime hash/proof | Builder input at `:95`; not seen populated in latest frame samples | Low | YES | YES before readiness claims |
| Derived clean/contradiction predicates | Built at `ExecutionAuthorityFrameBuilder.cs:121`, `:241-246`, `:340` | Deterministic from supplied fields | Medium | Field-dependent | YES |

Frame issue found in latest runtime logs: some list fields rendered as type names such as `System.String[]` and `System.Collections.Generic.List\`1[System.String]` in `AUTHORITY_FRAME_SNAPSHOT`, reducing log usability for `active_intent_ids` and `failed_predicates`. Treat this as observability debt, not authority behavior proof.

## Section 4 - Legacy Bypass Audit

| Legacy path | File/function | What it mutates | Why dangerous | Current guard | Required migration step |
|---|---|---|---|---|---|
| Release readiness broker-flat reconciliation | `RobotEngine.Reconciliation.Release.cs:348`, `:368` | Closes broker-flat journal rows before mismatch-release UEA | Can alter release inputs before authority sees original truth | Suppression predicates, ownership checks | Route these repairs through `JournalCompleteBrokerFlat` authority first |
| ExecutionJournal reconciliation complete | `ExecutionJournal.Reconciliation.cs:112` | Sets `TradeCompleted`, completion reason | Caller-proven broker flat can be wrong/stale | Caller contract only | Add UEA-required wrapper for all non-fill completions |
| ExecutionJournal release cleanup | `ExecutionJournal.AdoptionRelease.cs:1140`, `:1187` | Closes release rows | Can hide gross-open truth if invoked from wrong context | Release filters | Gate by canonical `JournalCompleteBrokerFlat` or separate repair action |
| ExecutionJournal recovery close | `ExecutionJournal.Recovery.cs:459`, `:481`, `:872`, `:945` | Completes recovered/untracked rows | Recovery cleanup can conflict with frame truth | Recovery-specific predicates | Add frame snapshot + UEA repair action before mutation |
| Protective recovery state and emergency flatten | `ProtectiveCoverageCoordinator.cs:284-326`, `:398-436` | Blocks instrument, submits corrective, triggers flatten | Protective is safety-critical and can bypass final authority frame at request point | Adapter flatten/protective submit gates later | Build protective authority frame before corrective/flatten state transitions |
| Explicit unfreeze | `RobotEngine.Reconciliation.cs:930` | Clears frozen instrument and risk latch | Clears durable safety state outside `LatchClear` | Adapter explicit unfreeze validation | Add `LATCH_CLEAR_EXPLICIT_OPERATOR` authority action |
| Interrupted close reentry bypass | `RobotEngine.Reconciliation.cs:501` | Clears freeze/latch for reentry bypass | Can clear durable block without `LatchClear` frame | Broker-flat and interrupted stream checks | Route through UEA with explicit stale-close-reentry-bypass action |
| IEA queue poison/blocked state | `InstrumentExecutionAuthority.cs:415`, `:558` | Blocks instrument, rejects queue work | Necessary fail-closed path but outside frame | IEA internal fail-closed | Keep as executor health evidence; feed into frame |
| RiskGate | `Execution/RiskGate.cs:47` | Blocks stream execution | Parallel allow/deny vocabulary | Called by stream paths | Demote to validator or remove call sites after runtime proof |
| Run summary stale ownership suppression | `RunSummaryBuilder.cs:992-1006` | Alters reported working-order count | Reporter can disagree with canonical frame freshness | Best-effort stale suppression | Have summary consume shutdown frame artifact |

## Section 5 - Contradiction Audit

Latest run authority contradiction events:

| Contradiction | Legacy decision | Canonical frame decision | Evidence mismatch | Severity | Required fix |
|---|---|---|---|---|---|
| No `AUTHORITY_FRAME_DISAGREEMENT` | None logged | None logged | None in event stream | Informational | Continue; absence is not proof of complete dominance |
| No `AUTHORITY_FRAME_UNSAFE_LEGACY_ALLOW` | None logged | None logged | None in event stream | Informational | Continue; add more action coverage |
| 49 `MISMATCH_RELEASE` denials | State-consistency validator not ready | UEA denied `STATE_CONSISTENCY_NOT_RELEASE_READY` | No disagreement, but release repair still mutates before UEA | Safety-relevant | Move write-side release repairs behind authority |
| Runtime final unsafe despite zero broker qty | Summary STOP/unsafe | Shutdown frame should deny; latest summary denied unsafe | Broker working orders 4, journal open qty 8, ownership active slots 4 | Safety-critical but detected | Promote shutdown frame artifact to watchdog/operator truth |
| Frame log list serialization | No decision conflict | Frame log loses failed predicate detail in runtime log | Type-name strings for lists | Noisy/observability risk | Fix log payload serialization before relying on runtime frame diffs |

## Section 6 - Flatten/Reentry Authority Audit

| Lifecycle step | Current authority | Possible bypass | Safe? | Required fix |
|---|---|---|---:|---|
| Forced flatten request | Session sweep UEA at `RobotEngine.Session.cs:294`; adapter flatten UEA at `SubmitGate.cs:837` | Protective coordinator can request emergency flatten without request-side authority frame | Partial | Add frame at protective escalation and forced-flatten requester |
| Flatten verification | Structural/overlay/IEA plus flatten verifier rules; not one frame | Legacy verifier lifecycle rules can retire/continue locally | Partial | Add canonical flatten-verification action after flatten command |
| Broker-flat confirmation | ReconciliationRunner UEA for one path at `ReconciliationRunner.cs:750` | Release/recovery journal close paths still bypass | NO | Gate all broker-flat journal completion through UEA |
| Market reentry | SSM UEA at `StreamStateMachine.Reentry.cs:330`; adapter submit UEA | Direct adapter `SubmitMarketReentryOrder` still relies on submit gate if called elsewhere | Mostly | Runtime-proof current source; assert no direct caller bypass |
| Stream terminalization | Main commit UEA at `StreamStateMachine.Commit.cs:417` | Prior carry-forward journal mirroring at `:628` is local | Mostly | Add authority/audit for prior journal mirror |
| Shutdown convergence | Summary UEA at `RunSummaryBuilder.cs:257` | Watchdog and summary artifact inputs can differ/stale | Partial | Emit/consume final shutdown authority frame |

## Section 7 - Protective Authority Audit

| Action | Current authority | Wrongly blocked possible? | Correct future behavior |
|---|---|---:|---|
| Protective submit | UEA submit action allows through entry latch; EPA classifies protective as risk coverage at `ExecutionPermissionAuthority.cs:21`, tests at `AuthorityContradictionTests.cs:113-146` | YES via structural snapshot/freshness; intended only for stronger safety reasons | Protective allowed through entry/mismatch latches unless invalid structure, stale account, or quantity failure |
| Protective corrective submit | Coordinator calls `_submitCorrective` at `ProtectiveCoverageCoordinator.cs:305`, `:414` | YES/unknown; no local UEA frame before state transition | Build protective frame before corrective state mutation |
| Emergency protective flatten | Coordinator calls `_emergencyFlatten` at `:325`, `:432`; adapter flatten guard later evaluates UEA | Low for execution, higher for state transition | Requester frame plus adapter flatten frame |
| Under-coverage detection | Protective audit/coordinator status, not canonical frame-dominant | YES, pending windows can hide real gap | Aggregate coverage state must be a required frame field before promotion |

## Section 8 - Shutdown Safe Truth Audit

| Subsystem | Can claim safe? | Uses canonical frame? | Risk of disagreement | Required consolidation |
|---|---:|---:|---|---|
| RunSummaryBuilder | YES, via `summary.json` verdict | YES at `RunSummaryBuilder.cs:257` | Medium: artifact freshness/list suppression | Persist shutdown authority frame and failed predicates |
| RobotEngine.Stop | Writes summary artifacts at `RobotEngine.Shutdown.cs:259`, `:374` | Indirect through summary builder | Medium | Log shutdown authority snapshot in engine stop |
| Watchdog | Reports `execution_safe` and run summaries | Not authority-frame dominant | Medium | Use latest shutdown frame/summary proof level as display truth |
| Reconciliation | Can imply flat/closed through journal closure | Partially | High | Stop journal closure before UEA decision |
| Authority frame | Can deny safe verdict | YES | Low if inputs fresh; high if placeholders | Improve field population/freshness |

## Section 9 - Promotion Readiness

| Action | Readiness | Why | Missing proof | Runtime risk |
|---|---|---|---|---|
| `STREAM_COMMIT` | READY_AFTER_RUNTIME_PROOF | UEA is active before main commit mutation and harnessed | Current deployed SIM proof for current hash | Medium |
| `JOURNAL_COMPLETE_BROKER_FLAT` | NOT_READY | Runner path is UEA-gated, but release/recovery journal close bypasses remain | All journal completion callsites behind UEA | High |
| `LATCH_CLEAR` | NOT_READY | Auto-clear is gated, explicit/bypass clears are not | Route explicit/bypass clear paths | High |
| `SHUTDOWN_SAFE_VERDICT` | READY_AFTER_RUNTIME_PROOF | Summary UEA check exists and latest run correctly unsafe | Runtime frame artifact and watchdog consumption | Medium |
| `ENTRY_SUBMIT` | READY_AFTER_RUNTIME_PROOF | Active UEA deny dominates submit path when flag enabled | Current deployed hash proof and disagreement-free SIM | Medium |
| `REENTRY_SUBMIT` | READY_AFTER_RUNTIME_PROOF | SSM pre-mutation UEA exists in current source | Deployed SIM proof for current hash | Medium-high |
| `PROTECTIVE_SUBMIT` | NOT_READY | Adapter submit UEA exists, coordinator decisions are not framed | Protective aggregate frame and coordinator authority | High |
| `FLATTEN_SUBMIT` | READY_AFTER_RUNTIME_PROOF for adapter execution; NOT_READY for request-side dominance | Adapter guard exists; requesters still local | Request-side authority frames | Medium-high |
| `MISMATCH_RELEASE` | NOT_READY | Release output is UEA-gated, but input builder mutates journals first | Move repair writes behind authority | High |

## Section 10 - Remaining Gaps Before One Authority

| Gap | Subsystem | Severity | Runtime risk | Required fix |
|---|---|---|---|---|
| Release readiness mutates journals before UEA | Reconciliation release / ExecutionJournal | Critical | False release/hidden exposure | Make release builder read-only; route repair writes through UEA |
| Protective coordinator not frame-dominant | ProtectiveCoverageCoordinator | Critical | Under-protection/late flatten ambiguity | Add protective frame and authority action at coordinator decision points |
| Latch clear bypasses | Reconciliation explicit/stale reentry clear | High | Unsafe unfreeze or hidden block clear | Route all clears through authority |
| Frame fields sometimes placeholders | Submit frames, mode/timetable/proof/protective fields | High | False clean/unsafe predicates | Populate all required fields by action |
| Runtime frame logs lose list details | Frame logging | Medium | Harder audit/debug | Fix serialization of list fields |
| Deployed/source/hash mismatch | Deployment proof | Critical | Cannot claim current runtime behavior | Rebuild/redeploy and confirm `ROBOT_BUILD_SIGNATURE` |
| Watchdog not frame-dominant | Watchdog | Medium | Operator false-safe risk | Consume authority summary/frame |
| Legacy RiskGate still blocks | Stream/RiskGate | Medium | Parallel deny vocabulary | Demote after runtime proof |

## Section 11 - Exact Next Migration Steps

### P0 - Immediate Observability / Safety

| Task | Files | Behavior change? | Proof needed | Rollback plan |
|---|---|---:|---|---|
| Fix frame log list serialization | `ExecutionAuthorityFrameBuilder.cs`, `RobotEvents` path if needed | NO | `AUTHORITY_FRAME` harness, sample runtime log | Revert serializer-only patch |
| Emit final shutdown authority frame into run root | `RunSummaryBuilder.cs`, `RobotEngine.Shutdown.cs` | NO | summary builder tests | Revert artifact emission |
| Add callsite inventory test for journal completion outside UEA | test/tool only | NO | harness/tool output | Remove test/tool |

### P1 - Low-Risk Authority Promotion

| Task | Files | Behavior change? | Proof needed | Rollback plan |
|---|---|---:|---|---|
| Make release readiness broker-flat journal reconciliation read-only by default | `RobotEngine.Reconciliation.Release.cs` | YES | `AUTHORITY_CONTRADICTIONS`, state consistency tests | Revert release-readiness patch |
| Route explicit/stale latch clear through UEA action | `RobotEngine.Reconciliation.cs` | YES | latch authority tests | Revert clear routing only |
| Add request-side flatten frame for protective/session/slot requesters | `ProtectiveCoverageCoordinator.cs`, `StreamStateMachine.ForcedFlatten.cs`, `RobotEngine.Session.cs` | NO first | harness + log sample | Revert logging/frame build |

### P2 - Execution Authority Promotion

| Task | Files | Behavior change? | Proof needed | Rollback plan |
|---|---|---:|---|---|
| Protective coordinator must ask UEA before corrective submit or emergency flatten state transition | `ProtectiveCoverageCoordinator.cs` | YES | protective coverage tests + SIM | Revert coordinator authority patch |
| All non-fill journal completions require `JournalCompleteBrokerFlat` or explicit repair authority | `ExecutionJournal.*`, callers | YES | journal/reconciliation tests + failing-day replay | Revert journal wrapper patch |
| Flatten verification completion uses canonical frame | `FlattenCommands`, verifier rules | YES | flatten/reentry tests + SIM | Revert verifier authority patch |

### P3 - Legacy Demotion / Removal

| Task | Files | Behavior change? | Proof needed | Rollback plan |
|---|---|---:|---|---|
| Demote RiskGate to validator-only or remove duplicate callsites | `RiskGate.cs`, SSM entry paths | YES | multiple clean SIM/runtime proofs | Restore old gate calls |
| Demote structural/overlay/EPA from final deniers to validators under UEA result | submit gate / UEA | YES | full harness + SIM + deployed hash | Restore old chain |
| Watchdog consumes authority frame as operator truth | watchdog backend/frontend | YES reporting | watchdog tests + runtime snapshot | Revert UI/backend consumption |

## Section 12 - Final Verdict

1. UEA is not truly dominant yet. It is dominant on several promoted callsites, but not over every mutation of execution truth.
2. Legacy paths still control or mutate journal repair/completion, protective recovery state, explicit latch clear, release readiness pre-repairs, RiskGate blocking, IEA queue block state, and watchdog/reporting truth.
3. Yes, legacy logic can still independently mutate truth: especially execution journal closure/reopen paths, protective coordinator state, latch clears, IEA registry/order lifecycle, and summary-derived reporting.
4. The frame is close enough for narrow promotion with tests, but not complete enough for deleting legacy guards or promoting protective/mismatch/journal authority globally.
5. The most dangerous remaining bypass is release readiness/journal repair mutating journal rows before UEA evaluates mismatch release.
6. The next promotion should be: make release readiness read-only and route all broker-flat journal completion through `JournalCompleteBrokerFlat`; in parallel, route all latch clears through UEA.
7. Do not promote protective coordinator or mismatch release deletion/removal yet. They still need complete frame population and runtime proof.
8. Overall distance to true single-authority architecture: about 55-65 percent by source structure, but lower by runtime proof because the latest run failed unsafe and current source/build/deployed hashes do not match latest runtime proof.

Recommendation: no broad refactor and no legacy deletion. Fix the remaining write-side bypasses first, then deploy a single known DLL hash, run one representative SIM day, and require authority frame snapshots, no unsafe legacy allow, no disagreement, clean shutdown predicates, broker flat, no working orders, no journal open qty, and no stale ownership counts before claiming authority dominance.
