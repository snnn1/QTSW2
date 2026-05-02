# QTSW2 Full System Architecture, Belief, Complexity, and Run-Evidence Audit

Audit date: 2026-04-28

Code scope:
- `system/modules/robot/core`
- `system/RobotCore_For_NinjaTrader`
- `system/NT_ADDONS`

Runtime scope:
- Latest/relevant runs under `runs/`
- Primary latest run: `runs/98ecaee070664f79bc60c873d70c2d77`
- Supporting recent runs: `0474e633f2c644dd9b0aad8b8be7a6d9`, `0d4aa7940d834f7580896bd39db9afcf`, `325436872f5f4cf0af3331e944fc54ce`, `1e6a57d25ce54a84b3fbd30b7a0807a2`

Important timing note:
- The latest runtime evidence was produced before commit `3d013e4` (`Stabilize authority recovery and session handling`).
- Several runtime-proven issues below are fixed in code but still require a fresh playback run to validate.

## SECTION 1 - System Architecture Map

| Component | Purpose | Key files/functions | Depends on | Feeds into |
|---|---|---|---|---|
| Strategy / NinjaTrader adapter | Bridges NT callbacks into robot engine and adapter. Drains deferred NT-thread actions on the strategy thread. | `system/modules/robot/ninjatrader/RobotSimStrategy.cs` `OnBarUpdate`, `OnOrderUpdate`, `OnExecutionUpdate` | NinjaTrader account/order/execution APIs, `RobotEngine`, `NinjaTraderSimAdapter` | Engine ticks, order updates, execution updates |
| Stream state machine | Per-stream lifecycle: range lock, bracket submit, fills, exits, forced flatten, market-open reentry. | `system/modules/robot/core/StreamStateMachine.cs` `SubmitStopEntryBracketsAtLock`, `HandleForcedFlatten`, `CheckMarketOpenReentry`, `HandleReentrySubmitCompleted` | Timetable/session truth, execution adapter, journals | Entry orders, flatten requests, reentry intents, terminal commits |
| Execution adapter | Main execution bridge. Performs submit/cancel/flatten, safety gate calls, journal updates, protective handling, deferred NT actions. | `system/modules/robot/core/Execution/NinjaTraderSimAdapter.cs` `TryExecutionSafetyGateForOrderSubmit`, `SubmitStopEntryOrder`, `CancelOrders`, `CancelIntentOrders`, `HandleExecutionUpdate`, `HandleEntryFill` | UEA/EPA/structural gates, IEA, strategy-thread executor, journal | NT order calls, IEA queue, execution journal, protective commands |
| Strategy-thread executor | Serializes NT-touching account/order actions onto the strategy thread. | `system/modules/robot/core/Execution/StrategyThreadExecutor.cs` `EnqueueNtAction`, `DrainNtActions` | NT callback thread identity, queue state | Submit/cancel/flatten execution |
| Instrument Execution Authority (IEA) | Per-instrument serialized ownership/order authority. Owns execution updates, protective commands, adoption scans. | `system/RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` `EnqueueExecutionUpdate`, `HandleEntryFill`, `ScanAndAdoptExistingOrdersCore` | Broker orders, execution updates, ownership snapshots, protective coordinator | Ownership registry, protective submit/resize, adoption state |
| Unified Execution Authority (UEA) | Single intended order-submit permission gate stack. | `system/modules/robot/core/Execution/UnifiedExecutionAuthority.cs` `Evaluate` | EPA, structural layer, overlay, ownership ledger snapshot | Allow/deny decisions, `UEA_ACTIVE_DENY` |
| Execution Permission Authority (EPA) | Fast preflight for kill switch, mismatch execution block, frozen instruments. | `system/modules/robot/core/Execution/ExecutionPermissionAuthority.cs` `TryAdapterOrderSubmitPreflight` | Global execution state, mismatch block state | Gate1 result |
| Structural gate | Checks broker/journal/ledger/IEA structural safety, recovery state, parity. | `system/modules/robot/core/Execution/ExecutionStructuralLayer.cs` `TryEvaluateOrderSubmitStructure`, `TryClearRecoveryRequiredForAuthoritativeFlatSubmit` | Account snapshot, journal, ledger, QuantExecutionControlStore | Gate3 allow/deny |
| Quant execution control store | Tracks per-instrument normal/recovery phases and mismatch escalation/release. | `system/modules/robot/core/Execution/QuantExecutionControlStore.cs` `EvaluateEscalationAndApplyIfRequired`, `TryClearRecoveryRequiredOnAuthoritativeFlat` | Reconciliation snapshots, ledger/journal/broker quantities | `RecoveryRequired`, execution blocking |
| Reconciliation | Compares broker quantity to journal quantity and emits mismatch/drift/repair signals. | `system/modules/robot/core/Execution/ReconciliationRunner.cs` `RunOnce`, `ForceRunGateRecoveryForInstrument` | Broker account, execution journal, ledger, pending alignment | Mismatch events, recovery, gate callbacks |
| State consistency gate | Decides if mismatch/recovery can release based on one structural snapshot. | `system/modules/robot/core/Execution/StateConsistencyGateModels.cs` `StateConsistencyGate.Evaluate` | Broker qty/orders, journal qty, IEA working, adoption candidates, blockers | Release/deny decision |
| Ownership ledger/snapshots | Canonical attribution of owned slots, active/orphan slots, broker-order ownership. | `system/modules/robot/core/Execution/CanonicalOwnershipLedger*`, `events/ownership_*` | IEA, journals, broker order ids | UEA/structural gate/reconciliation evidence |
| Execution journal | Durable intent/order/fill lifecycle facts. | `system/modules/robot/core/Execution/ExecutionJournal.cs` `RecordSubmission`, `RecordEntryFill` | Submit/fill events | Reconciliation, stream lifecycle, audit |
| Session/timetable | Resolves active session, close, forced flatten trigger, reopen timing. | `system/modules/robot/core/Models.SessionTruthFrame.cs`, `system/modules/robot/core/RobotEngine.cs` `ResolveSessionTruth`, `SessionTimingPolicy.cs` | Internal timetable, NT session cache, spec fallback | Forced flatten, reentry timing |
| Watchdog/health | Emits stalls, alive ticks, execution queue health, mismatch health. | `robot_ENGINE*.jsonl`, `StrategyThreadExecutor`, execution queues | Engine/IEA queues | Stop/crash diagnostics |
| Run artifacts/summaries | Post-run status, key event counts, journals, derived artifacts. | `runs/<run_id>/summary.json`, `KEY_EVENTS.jsonl`, `logs/robot/*.jsonl`, `state/*`, `events/*`, `derived/*` | Runtime logs/state | Audit verdicts |

## SECTION 2 - Runtime Evidence Summary

| run_id | status | reason | trades | errors | main blocker | evidence |
|---|---:|---|---:|---:|---|---|
| `98ecaee070664f79bc60c873d70c2d77` | FAIL | `ROBOT_LOG_ERROR` | 9 | 10 | MES mismatch, MNG stale adoption escalation, NT-thread cancel violations, GC/YM stale recovery denial | `summary.json`; `logs/robot/robot_ENGINE_20260428_020617.jsonl:40967-40969`, `:41328-41329`, `:67305-67309`; `logs/robot/robot_GC.jsonl:115`; `logs/robot/robot_YM.jsonl:113` |
| `0474e633f2c644dd9b0aad8b8be7a6d9` | FAIL | `STREAM_INCOMPLETE_AT_SHUTDOWN` | 9 | 5 | Open exposure/working orders at shutdown, IEA-owned invariant breach, NT-thread cancel violations, ExecutionUpdate stall | `summary.json`; `logs/robot/robot_ENGINE.jsonl:42532`, `:52277`, `:69926-69930`, `:71649` |
| `0d4aa7940d834f7580896bd39db9afcf` | FAIL | `ROBOT_LOG_CRITICAL` | 12 | 3 | IEA-owned working invariant breaches | `summary.json`; `logs/robot/robot_ENGINE.jsonl` critical events for MES/MYM/M2K |
| `325436872f5f4cf0af3331e944fc54ce` | FAIL | `ROBOT_LOG_ERROR` | 11 | 63 | NG reentry repeatedly denied by stale `quant_recovery_required` | `summary.json`; `logs/robot/robot_NG.jsonl:212+` repeated `REENTRY_SUBMIT_FAILED` |
| `1e6a57d25ce54a84b3fbd30b7a0807a2` | FAIL | recent historical |  |  | MGC mismatch, MNG adoption/protective issue, CL range transition failure | `logs/robot/robot_ENGINE*.jsonl` critical/error events |

Still active in runtime, based on latest run before `3d013e4`:
- MES broker/journal drift: `RECONCILIATION_QTY_MISMATCH`, `POSITION_DRIFT_DETECTED`, `EXPOSURE_INTEGRITY_VIOLATION` at `runs/98ecaee070664f79bc60c873d70c2d77/logs/robot/robot_ENGINE_20260428_020617.jsonl:40967-40969`.
- MNG adoption non-convergence on already registered/candidate orders at `:41328-41329`.
- NT-thread cancel violations at `:67305-67309`.
- GC/YM bracket submit blocked by stale `quant_recovery_required` at `runs/98ecaee070664f79bc60c873d70c2d77/logs/robot/robot_GC.jsonl:115` and `robot_YM.jsonl:113`.

Fixed in code but not yet runtime-validated:
- Public cancel paths now enqueue before touching NT-thread-only real cancel methods: `NinjaTraderSimAdapter.cs` `CancelOrders`, `CancelIntentOrders`.
- RecoveryRequired now has an authoritative-flat release valve: `ExecutionStructuralLayer.cs` `TryClearRecoveryRequiredForAuthoritativeFlatSubmit`; `QuantExecutionControlStore.cs` `TryClearRecoveryRequiredOnAuthoritativeFlat`.
- Adoption convergence now clears already-owned candidates: `InstrumentExecutionAuthority.NT.cs` `ScanAndAdoptExistingOrdersCore`.
- Session truth now prefers internal timetable over NT holiday conflict for forced flatten/reentry decisions: `Models.SessionTruthFrame.cs`, `RobotEngine.cs` `ResolveSessionTruth`.

Code-design risks not currently reproduced in the latest run:
- Duplicate protective handling between adapter legacy path and IEA path.
- Duplicate mismatch/block/release decisions across UEA, EPA, structural layer, reconciliation, state consistency gate, and QuantExecutionControlStore.
- Summary metadata inconsistency: latest `summary.json` says date `2026-04-12` while runtime logs include `2026-04-14` session evidence.

## SECTION 3 - End-to-End Data Flow

| Step | Component | Input | Output | Authority used |
|---|---|---|---|---|
| Market data | `RobotSimStrategy.OnBarUpdate` | NT bars/ticks | `RobotEngine.OnBar`/`Tick` | NT feed plus robot timetable |
| Range/entry decision | `StreamStateMachine.CanSubmitStopBrackets`, `SubmitStopEntryBracketsAtLock` | Range state, slot state, current price | Submit/no-trade decision | Stream journal, timetable, price classifier |
| Submit decision | `NinjaTraderSimAdapter.TryExecutionSafetyGateForOrderSubmit` | intent, side, qty, order type, context | allow/deny reason | UEA/EPA/structural/overlay |
| Order submit | `SubmitStopEntryOrder`, strategy-thread executor | allowed intent | NT order | Strategy-thread executor and NT adapter |
| Fill | `RobotSimStrategy.OnExecutionUpdate`, `NinjaTraderSimAdapter.HandleExecutionUpdate`, `IEA.EnqueueExecutionUpdate` | NT execution | queued IEA work item | IEA per-instrument serialization |
| Journal | `ExecutionJournal.RecordEntryFill` | fill facts | durable fill state | Execution journal |
| Ownership | IEA and canonical ledger | broker order ids, intent ids, fills | ownership slots/snapshots | IEA registry and canonical ledger |
| Protection | IEA `HandleEntryFill` | fill qty/price | stop/target submit or resize | IEA protective authority |
| Reconciliation | `ReconciliationRunner.RunOnce` | broker qty, journal qty, ledger | mismatch/drift/recovery signal | Broker snapshot plus journal/ledger |
| Exit | stream/adapter/IEA | exit condition or protective fill | cancel/complete journal | Stream state plus execution journal |
| Flatten/reentry | `HandleForcedFlatten`, `CheckMarketOpenReentry` | session close/open, broker flat, original journal | flatten, reentry market order | SessionTruthFrame, broker snapshot, journal |
| Cleanup | stream commit, summary, watchdog | terminal state | run artifacts | Journals, ownership snapshots, summary builder |

## SECTION 4 - Decision Point Inventory

| Decision | File/function | Inputs used | Output | Downstream effect |
|---|---|---|---|---|
| Can we trade this slot? | `StreamStateMachine.CanSubmitStopBrackets` | stream state, range state, current time/price, dependencies | true/false | bracket submit or wait/no-trade |
| Is stop order valid or already crossed? | `StreamStateMachine.SubmitStopEntryBracketsAtLock` | breakout prices/current price/classifier | STOP, MARKET, reject/no-trade | entry order type or missed-opportunity rejection |
| Can we submit? | `NinjaTraderSimAdapter.TryExecutionSafetyGateForOrderSubmit` | intent, order purpose, instrument, qty, UEA flag | allow/deny | submits or logs `UEA_ACTIVE_DENY` |
| Gate1 mismatch/global block | `ExecutionPermissionAuthority.TryAdapterOrderSubmitPreflight` | mismatch block, kill switch, frozen instrument | allow/deny | immediate submit veto |
| Gate3 structural safety | `ExecutionStructuralLayer.TryEvaluateOrderSubmitStructure` | account snapshot, journal, ledger, IEA, recovery phase | allow/deny | structural veto |
| Is recovery required? | `QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired` | sustained mismatch state | normal/recovery phase | future submit block |
| Can recovery release? | `StateConsistencyGate.Evaluate`, `TryClearRecoveryRequiredOnAuthoritativeFlatSubmit` | broker/journal/ledger/IEA/adoption/blockers | release/deny | clears or keeps block |
| Is state safe? | `ReconciliationRunner.RunOnce` | broker qty vs journal qty | mismatch/drift events | recovery/escalation |
| Is session active/closing? | `RobotEngine.ResolveSessionTruth`, `SessionTimingPolicy` | internal timetable, NT cache, spec fallback | session truth frame | forced flatten/reentry |
| Should we flatten? | `RobotEngine` forced flatten loop, `StreamStateMachine.HandleForcedFlatten` | session close trigger, stream active state, original intent | flatten request or skip | flatten NT order, journal interrupted |
| Should we reenter? | `StreamStateMachine.CheckMarketOpenReentry` | interrupted state, session reopen, broker flat, original journal | reentry command or wait | market reentry |
| Can NT be touched now? | `StrategyThreadExecutor.EnqueueNtAction`, `NinjaTraderSimAdapter.EnsureStrategyThreadOrEnqueue` | thread identity, playback quiescence | enqueue/execute/violation | prevents NT-thread crash/freezes |
| Is protective coverage complete? | IEA `HandleEntryFill`, protective audits | fill qty, existing stop/target, broker orders | submit/resize/fail-close | risk coverage |

## SECTION 5 - Decision Overlap / Duplication

| Decision | Duplicate locations | Difference in inputs | Risk | Runtime evidence |
|---|---|---|---|---|
| Execution permission | UEA `Evaluate`; adapter fallback gate chain; EPA; structural layer; overlay | UEA consumes a ledger snapshot, fallback re-derives via journal/account | One lane can allow while another denies; stale reasons can survive | GC/YM denied by Gate3 stale recovery: latest `robot_GC.jsonl:115`, `robot_YM.jsonl:113`; historical NG reentry denials `325436.../robot_NG.jsonl:212+` |
| Mismatch block/release | EPA mismatch block; UEA Gate2; reconciliation; state consistency gate; QuantExecutionControlStore | Some use broker/journal, some use blockers/phases, some use ledger | Multiple ways to block, fewer ways to release | MES latest drift `robot_ENGINE_20260428_020617.jsonl:40967-40969` |
| Structural gate | `ExecutionStructuralLayer`, `StateConsistencyGate`, IEA invariant checks | Structural gate is submit-time; state consistency is recovery-time; IEA checks live working orders | Same concept expressed as separate decisions | IEA invariant breaches in `0474...:42532`, `:52277`; `0d4aa...` criticals |
| Recovery required | Quant store, structural gate, reentry transient retry classifier | Quant phase persists; structural layer may deny; reentry loops retry | Stale recovery blocks clean new entries | Latest GC/YM and historical NG |
| Session truth | `RobotEngine.ResolveSessionTruth`, NT session cache, spec fallback, stream reentry gate | Internal calendar can conflict with NT holiday cache | False holiday can suppress session behavior or create noisy conflict | `SESSION_CLOSE_HOLIDAY_CONFLICT` latest; forced flatten used `INTERNAL_OVERRIDE` at `robot_ENGINE_20260428_020617.jsonl:67270-67271` |
| Journal/ledger parity | Execution journal, runner journal parity, canonical ledger, IEA registry | Journals can lag broker/IEA; ledger owns attribution | Lagging durable state can veto live-safe state | MES broker 2/journal 0 latest |
| Protective coverage | IEA `HandleEntryFill` and adapter legacy `HandleEntryFill` | IEA is serialized authority; adapter path contains legacy recovery behavior | Two protective brains can diverge | Historical MNG `PROTECTIVE_MISSING_STOP` in `1e6a57...` |
| Forced flatten/reentry | stream lifecycle, session policy, broker flat wait, late-confirm handler | flatten fill can arrive after next-session lifecycle | Reentry can be too early/late or per-lane inconsistent | Prior user-observed forced-flatten/reentry failures; latest flatten did trigger with internal override |

## SECTION 6 - Core Beliefs Extraction

| Belief | Actual statement from code behavior | File/function evidence | Domain |
|---|---|---|---|
| UEA is intended to be the submit authority | Order submit should pass through one gate stack. | `UnifiedExecutionAuthority.cs` `Evaluate`; `NinjaTraderSimAdapter.cs` `TryExecutionSafetyGateForOrderSubmit` | execution permission |
| EPA can veto before structural reasoning | Global/mismatch/frozen states can block immediately. | `ExecutionPermissionAuthority.cs` `TryAdapterOrderSubmitPreflight` | execution permission |
| Structural safety may veto submit even after EPA allows | Broker/journal/ledger/recovery state must be structurally safe. | `ExecutionStructuralLayer.cs` `TryEvaluateOrderSubmitStructure` | state validity |
| RecoveryRequired is fail-closed | Sustained mismatch should block fresh execution until cleared. | `QuantExecutionControlStore.cs` `EvaluateEscalationAndApplyIfRequired` | recovery behavior |
| Authoritative flat can clear stale recovery | If broker, journal, ledger, IEA, and working orders are all clean, recovery may release. | `ExecutionStructuralLayer.cs` `TryClearRecoveryRequiredForAuthoritativeFlatSubmit`; `QuantExecutionControlStore.cs` `TryClearRecoveryRequiredOnAuthoritativeFlat` | recovery behavior |
| Broker snapshot is immediate risk truth | Reconciliation compares broker qty against journal and escalates broker-ahead exposure. | `ReconciliationRunner.cs` `RunOnce` | position truth |
| Journal is durable lifecycle truth | Submission/fill state is persisted before lifecycle decisions continue. | `ExecutionJournal.cs` `RecordSubmission`, `RecordEntryFill` | journal truth |
| IEA owns live per-instrument order state | Execution updates and protectives are serialized by instrument. | `InstrumentExecutionAuthority.NT.cs` `EnqueueExecutionUpdate`, `HandleEntryFill` | ownership/protection |
| Internal session truth can override NT holiday | If internal timetable says active but NT cache says holiday, internal override can drive flatten. | `RobotEngine.cs` `ResolveSessionTruth`; `Models.SessionTruthFrame.cs` | session timing |
| NT calls must run on strategy thread | Account/order calls are unsafe off strategy thread. | `StrategyThreadExecutor.cs`; `NinjaTraderSimAdapter.cs` `EnsureStrategyThreadOrEnqueue` | platform safety |

## SECTION 7 - Belief Conflicts

| Conflict | Belief A | Belief B | Code evidence | Runtime evidence | Severity |
|---|---|---|---|---|---|
| Stale recovery blocks fresh clean submit | RecoveryRequired must fail closed | Authoritative flat should release stale recovery | `ExecutionStructuralLayer.cs` recovery branch and release helper; `QuantExecutionControlStore.cs` release helper | Latest GC/YM `UEA_DENIED:Gate3_Structural:quant_recovery_required`; historical NG reentry failures | High, fixed in code pending run validation |
| Broker/ledger live truth vs journal lag | Broker snapshot exposes real risk immediately | Journal parity can veto or escalate before alignment converges | `ReconciliationRunner.cs` broker/journal comparison; structural layer parity checks | Latest MES broker 2/journal 0 drift | High |
| Already-owned order treated as adoption failure | IEA registry/candidate means tracked | Adoption convergence still escalated unchanged order | `InstrumentExecutionAuthority.NT.cs` `ScanAndAdoptExistingOrdersCore`; `AdoptionReconciliationConvergence.cs` | Latest MNG classification `Accepted|True|True|S` and `Working|True|True|T` escalated | High, fixed in code pending run validation |
| Public cancel API vs NT-thread safety | Public cancel can be called by engine paths | Real cancel requires strategy thread | `NinjaTraderSimAdapter.cs` `CancelOrders`, `CancelIntentOrders`, `EnsureStrategyThreadOrEnqueue` | Latest NT_THREAD_VIOLATION `:67305-67309` | High, fixed in code pending run validation |
| NT holiday vs internal timetable | NT session cache may say no session/holiday | Internal timetable says trading slot exists | `RobotEngine.cs` `ResolveSessionTruth`; `SessionTruthFrame` | Latest `SESSION_CLOSE_HOLIDAY_CONFLICT`; forced flatten used internal override `:67270-67271` | Medium |
| Protective authority duplicated | IEA should own live per-instrument protection | Adapter legacy path still contains protective submit/recovery logic | `InstrumentExecutionAuthority.NT.cs` `HandleEntryFill`; `NinjaTraderSimAdapter.cs` `HandleEntryFill` | Historical MNG missing protective stop | Medium |
| Summary status vs real trading outcome | Summary status says FAIL on log critical | Latest had no open exposure at shutdown and completed filled journals | `summary.json` status builder artifacts | Latest summary: trades 9, no open exposure, but FAIL due robot logs | Medium |

## SECTION 8 - Truth Source Hierarchy

| Truth source | Used by | Strength | Known lag | Should be authoritative? |
|---|---|---|---|---|
| Broker account snapshot | reconciliation, structural gate, flatten/reentry preflight | Best immediate risk truth | Can be transient around fills | YES for live exposure/risk |
| Canonical ownership ledger | UEA/structural/reconciliation attribution | Best attribution truth when enabled | Can lag fill until updated | YES for ownership attribution |
| Execution journal | stream lifecycle, reconciliation, audit | Best durable fill/order facts | Can lag broker fill callback | YES for durable facts, NO as sole live risk veto |
| Stream journal | stream lifecycle | Best slot lifecycle truth | Can be stale if callbacks delayed | YES for lifecycle, NO for broker risk |
| Ownership snapshots/events | audit, UEA, IEA | Strong evidence of live order attribution | Snapshot cadence lag | YES with broker/IEA |
| Runner journal parity | reconciliation/diagnostics | Useful diagnostic | Can lag live broker/ledger state | NO as sole execution veto |
| IEA registry | IEA/protective/adoption | Best live working-order ownership | Can miss externally created/adopted orders until scan | YES for live order ownership |
| UEA structural snapshot | submit gate | Strong if built from immutable frame | Only as fresh as inputs | YES as submit authority input |
| Timetable/spec | engine/session | Best robot-intended trading schedule | Spec/config errors possible | YES for robot schedule |
| NT session resolver/cache | session close/open | Platform-native reference | Can mark configured slots as `HOLIDAY` incorrectly | Advisory unless internal truth absent |

Correct hierarchy:
1. Live exposure risk: broker snapshot first, then IEA/ledger/journal for attribution.
2. Live working order ownership: IEA registry plus broker orders first, then ownership ledger/snapshots.
3. Durable fill history: execution journal first.
4. Stream lifecycle: stream journal first, but broker risk can override safety actions.
5. Submit permission: one immutable authority frame consumed by UEA; gates must not independently re-derive conflicting truth.
6. Session timing: `SessionTruthFrame` from internal timetable/spec first, NT cache advisory, emergency fallback last.

## SECTION 9 - Authority Model Audit

| Domain | Current authority | Conflicting authorities | Correct authority | Change needed |
|---|---|---|---|---|
| execution permission | UEA, with adapter fallback if disabled | EPA, structural layer, overlay, mismatch block, Quant store | UEA consuming immutable authority frame | Keep UEA only; make fallback diagnostic/off in production |
| state validity | reconciliation, structural layer, state consistency gate | broker/journal/ledger each used separately | pure reconciliation classifier plus state consistency release gate | Centralize snapshot construction |
| recovery release | Quant store release helper, structural helper, state consistency gate | stale phase can outlive clean state | authoritative-flat release from broker+journal+ledger+IEA | Validate post-`3d013e4`; then remove older release exceptions |
| session timing | `SessionTruthFrame` plus older direct NT/spec readers | NT holiday cache, fallback spec | `SessionTruthFrame` only | Audit/remove direct session cache use |
| protective coverage | IEA and adapter legacy path | duplicate protective submit/recovery | IEA owns live protective coverage | Make adapter path enqueue to IEA only |
| forced flatten | stream + engine session loop + adapter | instrument-level flatten vs per-stream lifecycle | session truth plus per-intent ledger attribution | Keep current, add tests for multi-stream same instrument flatten |
| reentry | stream state machine | recovery gate, broker flat, session cache | session truth + broker flat + original journal | Validate stale recovery release and late-flatten handling |

## SECTION 10 - Legacy / Defensive Code Inventory

| Location | Purpose | Bug it likely came from | Still needed? | Risk if removed | Runtime evidence |
|---|---|---|---|---|---|
| `NinjaTraderSimAdapter.TryExecutionSafetyGateForOrderSubmit` fallback path | Allow old EPA/structural/overlay chain when UEA disabled | migration to UEA | UNCERTAIN | Losing fallback if UEA flag disabled | No latest need if UEA enabled |
| `ExecutionStructuralLayer` journal-authority fallback | Operate when ledger unavailable | pre-ledger ownership model | UNCERTAIN | Submits could fail if ledger missing | Design risk, not latest-proven |
| `QuantExecutionControlStore` fail-closed recovery | Stop trading under unresolved mismatch | broker/journal drift incidents | YES | Could trade through real drift | Latest MES drift proves still protective |
| `TryClearRecoveryRequiredOnAuthoritativeFlatSubmit` | Release stale recovery when all authorities are clean | NG/GC/YM stale recovery blocks | YES | Reentry/brackets blocked indefinitely | Latest GC/YM, historical NG |
| `AdoptionReconciliationConvergence` | Escalate persistent adoption non-convergence | orphan working-order bugs | YES, but narrower | Could miss truly orphaned orders | Latest MNG showed it was too broad before fix |
| `EnsureStrategyThreadOrEnqueue` | Prevent NT calls off strategy thread | freezes/crashes | YES | NT freeze/crash risk | Latest NT_THREAD_VIOLATION |
| Adapter `HandleEntryFill` protective branch | Legacy protective recovery | pre-IEA protective handling | UNCERTAIN/likely reducible | Removing too early could leave gaps if IEA disabled | Historical protective issues, latest clean |
| Session emergency fallback close | Ensure flatten still possible if session resolver fails | NT session/holiday failures | YES | No flatten on bad session cache | Latest NT holiday conflict |
| Extensive critical logging for mismatch/invariants | Fail visibly on risk contradiction | silent drift | YES for now | Green-looking unsafe run | Latest MES drift |

## SECTION 11 - Complexity Drivers

| Cause | % contribution | Evidence | Runtime impact |
|---|---:|---|---|
| Fragmented authority/truth sources | 35% | UEA, EPA, structural, reconciliation, state gate, Quant store all decide safety | MES drift; stale recovery denial |
| Durable recovery/adoption residue | 25% | `RecoveryRequired` phase and adoption convergence can outlive clean live state | GC/YM/NG/MNG blocked/escalated |
| NT platform adapter leakage | 15% | Engine paths can reach NT-thread-only calls without queueing | NT_THREAD_VIOLATION latest |
| Protective/flatten lifecycle duplication | 15% | IEA and adapter both contain protective logic; flatten is instrument-level but lifecycle is per-stream | historical missing stop and multi-stream flatten/reentry confusion |
| Observability/status noise | 10% | Summary FAIL on critical logs even with no open exposure; date mismatch | Hard to know when a day is truly green |

## SECTION 12 - Root Cause Ranking

1. Fragmented authority/truth sources - 35%.
   Code evidence: `UnifiedExecutionAuthority.cs` `Evaluate`; `ExecutionPermissionAuthority.cs`; `ExecutionStructuralLayer.cs`; `ReconciliationRunner.cs`; `StateConsistencyGateModels.cs`; `QuantExecutionControlStore.cs`.
   Run evidence: latest MES drift `robot_ENGINE_20260428_020617.jsonl:40967-40969`; GC/YM recovery denials; historical NG reentry denials.
   Alternatives are less likely because failures occur across different instruments and phases but share the same pattern: one truth says block while another state is already clean or unaligned.

2. Stale recovery/adoption residue - 25%.
   Code evidence: `QuantExecutionControlStore.cs` persisted `RecoveryRequired`; `AdoptionReconciliationConvergence.cs`; IEA adoption scan.
   Run evidence: latest MNG `ADOPTION_NON_CONVERGENCE_ESCALATED` while `inRegistry=True` and `hasCandidate=True`; latest GC/YM stale `quant_recovery_required`; historical NG reentry.
   Alternatives are less likely because classifications explicitly show tracked orders being escalated and recovery phases vetoing new submits.

3. NT thread/scheduler leakage - 15%.
   Code evidence: `StrategyThreadExecutor.cs`; `NinjaTraderSimAdapter.cs` public cancel paths and strategy-thread guard.
   Run evidence: latest and prior `NT_THREAD_VIOLATION` events.
   Alternatives are less likely because logs name exact real cancel methods reached off thread.

4. Duplicated protective/flatten lifecycle - 15%.
   Code evidence: IEA `HandleEntryFill` and adapter `HandleEntryFill`; stream flatten/reentry late-confirm logic.
   Run evidence: historical missing stop, multi-stream forced flatten/reentry problems; latest forced flatten did trigger.
   Alternatives are less likely for historical cases because failures happen after fills/flatten, not at range-lock calculation.

5. Run classification/summary noise - 10%.
   Code/runtime evidence: latest `summary.json` FAIL due logs despite no open exposure at shutdown; summary date mismatch vs log session.
   Alternatives are less likely because the summary itself contains internally mixed signals.

## SECTION 13 - Correct Belief Model

| Old belief | Correct belief | Enforcing component |
|---|---|---|
| Runner/journal parity may veto reentry indefinitely | Broker flat plus clean ledger/IEA/journal releases stale recovery; journal lag is diagnostic unless structural risk exists | UEA authority frame plus Quant release helper |
| Adoption residue can keep escalating any unchanged order | Already-owned, candidate-backed broker orders are converged, not adoption failures | IEA adoption scan |
| Any submit path can perform its own safety derivation | Submit permission must come from one immutable frame consumed by UEA | UEA and authority-frame builder |
| NT session cache can suppress robot session behavior | Internal timetable/session truth wins; NT holiday is advisory unless internal truth absent | `SessionTruthFrame` |
| ExecutionUpdate may process all downstream consequences inline | ExecutionUpdate should capture fill facts, persist essentials, enqueue downstream work, and return bounded | IEA worker/post-fill pipeline |
| Protective logic can live in adapter and IEA | IEA is the live protective authority; adapter only routes commands | IEA |
| Summary FAIL means the trading logic failed equally | Summary should separate safety failure, execution contradiction, and diagnostic criticals | summary builder/run classifier |

## SECTION 14 - System Contract

Position truth rules:
- Broker snapshot is the immediate exposure truth.
- Canonical ledger and IEA explain ownership, not whether broker exposure exists.
- Execution journal is durable fill history, not sole live exposure truth.

Execution permission rules:
- All submits must pass through UEA.
- UEA must consume one immutable authority frame.
- Gates may classify but must not independently rebuild conflicting truth.

Recovery/adoption rules:
- RecoveryRequired blocks real structural risk.
- RecoveryRequired clears when broker, working orders, journal, IEA, and ledger are all authoritative flat/clean.
- Already-owned adoption candidates must clear convergence residue.

Protective coverage rules:
- IEA owns protective submit/resize/cancel decisions.
- Adapter may route or enqueue, but must not act as an independent protective brain.
- Partial protection is a fail-close condition only when broker exposure is real and unprotected.

Session timing rules:
- `SessionTruthFrame` is the single source for close, forced flatten trigger, and reopen.
- Internal timetable/spec wins over NT holiday conflict for configured robot sessions.
- NT resolver is advisory unless internal authority is absent.

Flatten/reentry rules:
- Forced flatten is a session lifecycle interrupt, not a terminal completed trade.
- Instrument-level flatten must be attributed back to all affected active streams.
- Reentry waits for next allowed market-open truth and broker-flat confirmation.

Audit/logging rules:
- Critical logs must distinguish active safety violations from stale residue that was auto-cleared.
- Summary status must distinguish unsafe exposure from diagnostic failures.

## SECTION 15 - Simplification Plan

| Simplification | What to remove | What to merge | What to centralize | Where | Why safe | Confidence | Risk |
|---|---|---|---|---|---|---:|---|
| Build an immutable execution authority frame | ad hoc per-gate snapshot derivation | broker/journal/ledger/IEA inputs | authority-frame builder used by UEA | `Execution` gate layer | Reduces contradictory submit decisions | High | Medium |
| Make UEA the only production submit gate | old adapter fallback when UEA enabled | EPA/structural/overlay as UEA sub-gates only | `TryExecutionSafetyGateForOrderSubmit` | adapter/UEA | Keeps same logic but one entry point | High | Low |
| Narrow recovery release to explicit authoritative-flat contract | scattered recovery bypasses | Quant release helper plus structural release | Quant store/structural layer | Already implemented partly | Matches recent stale recovery failures | High | Low |
| Narrow adoption convergence | stale unchanged-order escalation for owned orders | candidate/registry check into convergence state | IEA adoption | Already implemented partly | Runtime evidence directly matched | High | Low |
| Route all NT calls through executor | direct public cancel/flatten/account calls | public API to deferred NT action | adapter/executor | Already implemented for cancel | Prevents NT freezes | High | Medium |
| Collapse protective authority into IEA | adapter independent protective branch when IEA enabled | adapter route-only behavior | IEA | Latest protective clean, IEA is right layer | Medium | Medium |
| Centralize session use | direct NT session/fallback readers | all consumers use `SessionTruthFrame` | engine/streams | Latest shows NT holiday conflict | High | Low |
| Improve run classifier | treating all critical logs as same failure | safety failure vs diagnostic contradiction | summary builder | Makes green judgment clearer | Medium | Low |

## SECTION 16 - Fix Priority

| Change | Priority | Impact | Risk | Expected runtime improvement |
|---|---:|---|---|---|
| Validate `3d013e4` with a fresh playback run | P0 | Confirms stale recovery/adoption/thread fixes | Low | Should remove GC/YM/NG stale recovery, MNG false adoption, cancel NT-thread violations |
| Add/finish immutable authority frame for UEA | P1 | Removes most contradiction | Medium | Fewer split decisions and stale vetoes |
| Audit all direct NT-touching calls after quiescence/stall | P1 | Crash/freeze reduction | Medium | No off-thread account/order calls |
| Add post-fill sub-step timings | P1 | Finds remaining ExecutionUpdate stalls | Low | Bounded fill callback/worker behavior |
| Multi-stream forced-flatten attribution test | P1 | Prevents one-stream-real/one-stream-synthetic flatten split | Medium | Cleaner reentry after flatten |
| Collapse protective decisions into IEA when enabled | P2 | Simplifies protection logic | Medium | Less duplicate resize/fail-close behavior |
| Summary classifier cleanup | P2 | Better green/red answer | Low | Clearer run verdicts |

## SECTION 17 - Validation Plan

| Signal | Expected | Failure condition | Runs/tests needed |
|---|---|---|---|
| `UEA_DENIED:*quant_recovery_required` on fresh bracket/reentry | none when broker/journal/ledger/IEA are clean | any stale denial with authoritative-flat evidence | Harness plus next playback |
| `ADOPTION_NON_CONVERGENCE_ESCALATED` with `inRegistry=True` and `hasCandidate=True` | zero | any repeat of latest MNG classification | Harness adoption test plus playback |
| `NT_THREAD_VIOLATION` | zero | any public cancel/flatten/account call reaches real NT path off-thread | Playback around cancels/forced flatten |
| `EXECUTION_COMMAND_STALLED` | zero over 8000 ms; preferably no sub-step over 2000 ms | any fill/post-fill worker stall | Add timings; replay around prior MES/MNG stalls |
| Broker/journal mismatch | allowed only inside bounded pending-alignment window | unbounded broker-ahead drift | Reconciliation tests and playback |
| Forced flatten | trigger at configured close offset using `SessionTruthFrame` | no flatten at 20:55 or immediate wrong reentry | Multi-stream same-instrument playback |
| Reentry | fires at next market open after broker flat and clean authority | early reentry or no reentry due stale recovery | Harness `MARKET_REENTRY_SUBMIT_PATH` plus playback |
| Protective coverage | stop and target exist for every filled active intent | missing stop/target or partial protection | IEA protective tests plus playback |
| Run summary | separates unsafe exposure from diagnostic criticals | FAIL with no actionable safety issue and no open exposure | Summary test fixtures |

## SECTION 18 - Final Verdict

Classification:
- C) fragmented and self-contradicting, but with several high-impact contradictions already fixed in code by `3d013e4` and awaiting runtime validation.

Was the latest run actually unsafe?
- It contained a runtime-proven unsafe/contradictory moment: MES broker quantity 2 while journal quantity 0, logged as `RECONCILIATION_QTY_MISMATCH`, `POSITION_DRIFT_DETECTED`, and `EXPOSURE_INTEGRITY_VIOLATION` at `runs/98ecaee070664f79bc60c873d70c2d77/logs/robot/robot_ENGINE_20260428_020617.jsonl:40967-40969`.
- It did not end with open exposure according to `summary.json`: `had_open_exposure_at_shutdown=false`, `broker_position_qty_at_shutdown=0`, `broker_working_orders_at_shutdown=0`.

Mandatory answers:

1. Where does the system argue with itself?
   - Submit authority: UEA/EPA/structural/recovery each can veto from overlapping but not identical truth.
   - Position truth: broker says live exposure while journal/ledger can lag.
   - Adoption truth: IEA registry/candidate can say tracked while convergence escalates.
   - Session truth: NT holiday cache conflicts with internal timetable.
   - Protective authority: IEA and adapter both contain protective decision logic.

2. What beliefs are wrong or outdated?
   - Outdated: durable journal parity should be able to veto fresh reentry forever.
   - Outdated: unchanged adoption fingerprint means non-convergence even when the order is already owned.
   - Wrong: NT session holiday result should be equal to robot timetable truth.
   - Outdated: adapter and IEA can both independently decide protective handling.

3. Which complexity is still protecting us vs legacy baggage?
   - Still protecting: fail-closed recovery, reconciliation criticals, NT-thread executor/guard, emergency session fallback, protective fail-close.
   - Likely baggage/reducible: adapter fallback gate chain when UEA is enabled, adapter protective branch when IEA is enabled, direct session cache reads outside `SessionTruthFrame`, scattered recovery bypasses after authoritative-flat release exists.

4. What is the smallest set of changes to simplify the system?
   - Validate and keep the `3d013e4` stale recovery/adoption/thread fixes.
   - Introduce one immutable authority frame for UEA submit decisions.
   - Make UEA the only production submit authority.
   - Route all NT-touching calls through the strategy-thread executor.
   - Centralize close/open timing on `SessionTruthFrame`.
   - Add post-fill timings before changing more ExecutionUpdate behavior.

5. Which current runtime issues prove the architecture problem is real?
   - Latest MES broker/journal drift proves fragmented position truth.
   - Latest GC/YM stale `quant_recovery_required` proves recovery residue can veto clean submits.
   - Latest MNG adoption escalation with `inRegistry=True` and `hasCandidate=True` proves ownership/adoption truth conflict.
   - Latest NT-thread cancel violations prove platform adapter leakage.
   - Latest NT holiday conflicts prove session truth fragmentation.

6. What should NOT be changed yet?
   - Do not remove fail-closed recovery or mismatch criticals until fresh runs prove the release contract works.
   - Do not remove NT-thread guards.
   - Do not remove internal session override; latest run needed it.
   - Do not collapse IEA/adoption/protective logic until the next run validates the recent fixes.
   - Do not treat summary FAIL as noise until MES-style drift and NT-thread violations are gone.

