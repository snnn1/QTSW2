# QTSW2 Canonical Execution Authority Consolidation Plan - 2026-05-14

Scope: planning/audit map for consolidating QTSW2 execution decisions into one canonical authority frame.

Evidence level for this document: source-only plus cited existing SIM/runtime/playback audit evidence. No new build, harness, playback, SIM/runtime, or deployed-live proof was created by this document.

Rules for this plan:

- Do not remove safety rails yet.
- Do not weaken fail-closed, protective, overfill, journal, ownership, or mismatch protection.
- Separate evidence providers from final decision-makers.
- First implementation patch must be read-only/shadow unless explicitly promoted.
- Playback passes do not prove multi-day live scheduling; runtime/deployed hash proof remains required.

## Executive Summary

The one final authority should be `UnifiedExecutionAuthority`, consuming one expanded `ExecutionAuthorityFrame` built from broker, journal, stream, ownership, registry, QEC, protective, latch, mismatch, timetable, kill-switch, mode, and deployment evidence.

The code already has the start of this model:

- `ExecutionAuthorityFrame` exists at `system/modules/robot/core/Execution/AuthorityDecisionModels.cs:85`.
- Submit decisions already flow through `UnifiedExecutionAuthority.Evaluate` at `system/modules/robot/core/Execution/UnifiedExecutionAuthority.cs:127`.
- Non-submit action decisions have started through `UnifiedExecutionAuthority.EvaluateAction` at `system/modules/robot/core/Execution/UnifiedExecutionAuthority.cs:30`.
- Terminal stream commit is now routed through UEA at `system/modules/robot/core/StreamStateMachine.Commit.cs:386`.
- Session-close global sweep is now routed through UEA at `system/modules/robot/core/RobotEngine.Session.cs:281`.

The consolidation is incomplete. Several legacy paths still decide or mutate state independently: EPA, structural, overlay, RiskGate, reentry admission, forced flatten classification, reconciliation broker-flat journal completion, mismatch release, latch clear, protective recovery escalation, and shutdown safe verdict.

The safest first implementation patch after this plan is not a behavior change: build and emit a read-only canonical frame snapshot for every major action, run the existing legacy decision and the canonical shadow decision side by side, and log `AUTHORITY_FRAME_DISAGREEMENT` when the two disagree.

## Section 1 - Current Decision Inventory

| Decision | Current file/function | Inputs used | State mutated | Final authority? | Future role | Risk |
|---|---|---|---|---|---|---|
| Entry allowed/blocked | `NinjaTraderSimAdapter.SubmitGate.TryExecutionSafetyGateForOrderSubmit` at `system/modules/robot/core/Execution/NinjaTraderSimAdapter.SubmitGate.cs:433`; calls UEA at `:442`; legacy EPA/structural/overlay at `:465`, `:507`, `:525` | intent id, instrument, submit path, kill switch, mismatch block, structural frame, overlay snapshot | submit denied event/logs; no broker mutation on deny | Partial YES | UEA final; EPA/structural/overlay validators | UEA and legacy chain can drift because legacy checks still execute around UEA. |
| Entry submit execution | `SubmitEntryOrderCore` at `NinjaTraderSimAdapter.SubmitGate.cs:904`, gate at `:991` | gate result, order fields, duplicate/idempotency state | NT submit, execution journal, registry/QEC through downstream paths | NO | Executor after authority allow | Duplicate/idempotent paths can log CRITICAL while returning success. |
| Reentry allowed/blocked | `StreamStateMachine.Reentry` reentry time gate at `system/modules/robot/core/StreamStateMachine.Reentry.cs:53`, engine reentry block callback at `RobotEngine.Reconciliation.cs:520`, adapter `SubmitMarketReentryOrder` at `NinjaTraderSimAdapter.SubmitGate.cs:1035` | session reentry time, journal lifecycle, instrument block, IEA health, submit gate | stream journal reentry pending/submitted/failure state | NO | UEA final for admission; SSM evidence/requester; IEA executor | Reentry currently spans SSM, engine blocks, submit gate, and strategy-thread latch. This caused stale close/reentry conflicts. |
| Protective submit allowed/blocked | `SubmitProtectiveStop` interface and implementation; submit gate maps `SUBMIT_PROTECTIVE_STOP` to risk coverage at `NinjaTraderSimAdapter.SubmitGate.cs:546`; structural protective check at `:694` | filled exposure, stop/target details, QEC pending, EPA/structural/overlay | broker stop/target orders, QEC pending, registry | NO | UEA final must allow safety coverage unless stronger safety denial exists; protective submit remains executor | Ordinary entry latches must not block protective coverage. |
| Flatten allowed/blocked | `TryExecutionSafetyGateFlattenEnqueue` at `NinjaTraderSimAdapter.SubmitGate.cs:897`; structural flatten at `:846`; overlay flatten at `:879`; session sweep request at `RobotEngine.Session.cs:439`; emergency fallback at `:443` | broker exposure, structural frame, overlay lock, flatten command | queued flatten/cancel actions, flatten latches | NO | UEA final for flatten admission/classification; adapter/IEA executor | Flatten must bypass entry blocks but still require broker/account safety. |
| Stream terminal commit | `StreamStateMachine.Commit.Commit` at `system/modules/robot/core/StreamStateMachine.Commit.cs:369`; UEA action eval at `:386`; state mutation at `:438` | stream journal, execution journal lifecycle, reentry flags, commit reason | stream journal `Committed`, `CommitReason`, terminal state, `State=DONE` | Partial YES | UEA final for commit allow; SSM executor/persistence owner | Terminal commit can hide exposure if not tied to broker/journal/ownership proof. |
| Session-close global sweep flatten | `RunSessionCloseGlobalExposureSweep` at `system/modules/robot/core/RobotEngine.Session.cs:151`; per-instrument path at `:207`; UEA at `:281`; flatten request at `:439` | account snapshot, journal open rows, robot tags, reentry blockers, engine scope | durable sweep claim, cancel/flatten enqueue, interrupted stream journals | Partial YES | UEA final for sweep allow/deny; session path executor | Stale close claims can flatten valid post-reopen reentry if evidence is incomplete. |
| Journal broker-flat completion | Reconciliation broker-flat closure guard in `ReconciliationRunner.RunInternal` at `system/modules/robot/core/Execution/ReconciliationRunner.cs:360`; broker non-flat skip `:741`; working-order skip `:756`; ownership gross skip `:773`; close reason at `:796` | broker positions, working orders, journal open rows, ownership open slots | execution journal `TradeCompleted`, ownership close events | NO | UEA final for `JOURNAL_COMPLETE_BROKER_FLAT`; ReconciliationRunner evidence provider/executor | Broker net flat is not clean flat if gross journal/ownership remains. |
| Release mismatch/block | `RobotEngine.Reconciliation.Release.EvaluateStateConsistencyReleaseReadiness` at `system/modules/robot/core/RobotEngine.Reconciliation.Release.cs:104`; input builder `:193`; pure evaluator at `StateConsistencyGateModels.cs:197`; mismatch release at `MismatchEscalationCoordinator.cs:603`, `:706`, `:757` | broker/journal/ownership/registry/adoption blockers, stability window, pending IEA | mismatch block state, release state, logs, sometimes durable latch policy | NO | UEA final for `MISMATCH_RELEASE`; state consistency evaluator validator/evidence | Release can clear too early or stay stuck if one input is stale. |
| Durable latch create | `RiskLatchManager.Persist` at `system/modules/robot/core/RiskLatchManager.cs:79`; structural multi-intent policy persists at `RobotEngine.Reconciliation.cs:591`; forced convergence policy at `RobotEngine.Reconciliation.Recovery.cs:109` | mismatch/reconciliation/protective critical reason | `data/risk_latches` file | NO | UEA final for creating durable block; latch manager persistence executor | Durable latches survive restart and can hide as stale blocks. |
| Durable latch clear | `RiskLatchManager.Clear` at `RiskLatchManager.cs:100`; auto-clear checks at `RobotEngine.Reconciliation.cs:698`, `:744`, clear at `:764`; explicit unfreeze at `:818`, `:851` | broker qty, working orders, journal qty, real/recovery open qty, supervisory/protective/mismatch blocks | deletes latch file, clears engine freeze | NO | UEA final for `LATCH_CLEAR`; engine/latch manager executor | Unsafe clear can reopen trading while hidden exposure remains. |
| Broker exposure explained/unexplained | `InstrumentOwnershipLedger.GetOwnershipSnapshot` at `system/modules/robot/core/Execution/InstrumentOwnershipLedger.cs:320`; `InstrumentOwnershipSnapshot.ComputeUnexplainedQty` at `InstrumentOwnershipModels.cs:97`; `AuthoritativeStateEmitter` computes snapshot at `AuthoritativeStateEmitter.cs:156`, `:160`, `:181` | broker qty, ownership slots, journal slots, working orders | snapshot/event files only | NO | Evidence provider | Summary/watchdog can consume stale snapshots if not tied to frame freshness. |
| IEA registry working truth | `InstrumentExecutionAuthority.OrderRegistry.GetMismatchTrustedWorkingCount` at `system/modules/robot/core/Execution/InstrumentExecutionAuthority.OrderRegistry.cs:121`; order registration at `:19`; adoption at `:468` | order updates, adoption scan, broker ids/tags, ownership status | in-memory registry, shared adopted registry, QEC notifications | NO | Evidence provider/validator | Registry drift caused known working orders to appear missing. |
| Protective coverage block/escalate | `ProtectiveCoverageAudit` at `system/modules/robot/core/Execution/ProtectiveCoverageAudit.cs:16`; missing stop at `:114`; qty mismatch at `:132`; critical classifier at `:225`; coordinator state transitions at `ProtectiveCoverageCoordinator.cs:228`, `:320`, `:370`, `:433` | broker position, broker stop/target orders, QEC pending, recovery state | protective recovery state, corrective submit/flatten request, block reason | NO | Validator/evidence provider; executor for recovery after UEA allows safety action | Can block entries correctly but must not block protectives/emergency flatten. |
| QEC pending/convergence | `QuantExecutionControlStore` at `system/modules/robot/core/Execution/QuantExecutionControlStore.cs:11`; protective pending at `:36`; mapped fill at `:254`; unmapped fill at `:350`; escalation apply at `:872` | mapped/unmapped fills, protective pending, working submit pending, recovery windows | static per-instrument QEC state | NO | Evidence provider/validator | Pending windows can hide under-protection if not short/fresh. |
| Kill switch/operator disable | `KillSwitch` fail-closed file read at `system/modules/robot/core/Execution/KillSwitch.cs:13`; config path `:23`; `IsEnabled` at `:99` | `configs/robot/kill_switch.json`, read errors | cached kill-switch state/logs | NO | Evidence provider to UEA | Read errors intentionally block execution; must be surfaced clearly. |
| Shutdown safe verdict | `RobotEngine.Stop` at `system/modules/robot/core/RobotEngine.Shutdown.cs:259`; summary write at `:374`; `RunSummaryBuilder.Build` at `RunSummaryBuilder.cs:17`; open exposure aggregation at `:42`, `:200`, reason `:258`, verdict `:288` | KEY_EVENTS, execution journals, ownership snapshots, run shutdown signal, robot logs | `summary.json`, `RUN_SHUTDOWN.json` | NO | Reporter consuming UEA shutdown-safe decision | Summary can only report safe if all clean-flat predicates pass and evidence is fresh. |

## Section 2 - Evidence Store Inventory

| Evidence source | File/function | Freshness guarantee | Can be stale? | Current consumer | Required frame field |
|---|---|---|---|---|---|
| Broker position qty | `ExecutionJournal.SumAbsBrokerPositionForInstrument` at `system/modules/robot/core/Execution/ExecutionJournal.cs:52`; account snapshots consumed by `RobotEngine.Session.cs:151` and `ReconciliationRunner.cs:360` | Fresh only at snapshot capture time; frame must include capture time and age | YES | reconciliation, sweep, summary ownership snapshots | `broker_position_qty`, `broker_snapshot_captured_utc`, `account_snapshot_age_ms` |
| Broker working orders | `AuthoritativeStateEmitter.EmitSnapshot` counts at `system/modules/robot/core/Execution/AuthoritativeStateEmitter.cs:129`, `:146`; session sweep uses `CountRobotTaggedBrokerWorkingForInstrument` from `RobotEngine.Session.cs:221` path | Snapshot-time only | YES | ownership snapshots, sweep, reconciliation, protective audit | `broker_working_orders_count`, `broker_order_ids`, `broker_order_roles` |
| Broker order ids/tags | `RobotOrderIds` tags; registry resolves broker working rows via `InstrumentExecutionAuthority.OrderRegistry.cs:319`; session sweep collects robot tags | Snapshot-time only; tags can be missing/malformed | YES | registry, adoption, release blockers | `broker_order_ids`, `broker_order_tags`, `tagged_intent_ids` |
| Execution journal open qty | `ExecutionJournal.Query.GetOpenJournalEntriesByInstrument` at `system/modules/robot/core/Execution/ExecutionJournal.Query.cs:246`; remaining qty at `ExecutionJournal.AdoptionRelease.cs:636` | Disk/in-memory current for persistence root; depends on correct root | YES | reconciliation, sweep, summary, adoption | `journal_open_qty`, `open_journal_intent_ids`, `journal_row_count` |
| Stream journal lifecycle | `JournalStore.StreamJournal` fields around `system/modules/robot/core/JournalStore.cs:417`; commit writes in `StreamStateMachine.Commit.cs:438` | Durable per stream/day; may carry prior-day state | YES | SSM, playback, summary, sweep reentry blocker | `stream_lifecycle_state`, `stream_committed`, `stream_journal_key`, `prior_journal_key` |
| Ownership ledger open qty | `InstrumentOwnershipLedger.GetOwnershipSnapshot` at `system/modules/robot/core/Execution/InstrumentOwnershipLedger.cs:320`; gross/signed fields built at `:501`, active slots at `:502` | Fresh when emitted; snapshots can be stale later | YES | reconciliation, summary, watchdog | `ownership_open_qty`, `ownership_signed_qty`, `ownership_active_slots`, `ownership_orphan_slots` |
| Ownership snapshots | `AuthoritativeStateEmitter` writes to `events/ownership_snapshots` at `system/modules/robot/core/Execution/AuthoritativeStateEmitter.cs:58`, `:208` | Event/periodic; shutdown freshness must be checked | YES | run summary/watchdog | `ownership_snapshot_utc`, `ownership_snapshot_age_ms` |
| IEA order registry working count | `GetOwnedPlusAdoptedWorkingCount` at `InstrumentExecutionAuthority.OrderRegistry.cs:118`; mismatch-trusted count at `:122`; intent ids at `:126` | In-memory only; must be sampled with broker snapshot | YES | reconciliation, release, invariant audit | `iea_registry_working_count`, `iea_mismatch_trusted_working_count`, `iea_working_intent_ids` |
| Active intents | IEA lifecycle in `InstrumentExecutionAuthority.Commands.cs:290`, `:319`; execution journal submitted/open rows | In-memory plus journal evidence | YES | submit/reentry/summary | `active_intents_count`, `active_intent_ids`, `intent_lifecycle_states` |
| Protective coverage state | `ProtectiveCoverageAudit` at `ProtectiveCoverageAudit.cs:16`; critical statuses at `:225`; coordinator at `ProtectiveCoverageCoordinator.cs:228` onward | Snapshot audit; pending windows bounded by QEC | YES | protective coordinator, entry blocks | `protective_coverage_state`, `broker_stop_qty`, `broker_target_qty`, `protective_missing_qty`, `protective_pending` |
| QEC pending/convergence | `QuantExecutionControlStore.NotifyProtectiveSubmitPending` at `QuantExecutionControlStore.cs:36`; mapped fill at `:254`; unmapped at `:350`; escalation at `:872` | Static process state; not durable unless mirrored elsewhere | YES | protective audit, structural layer | `qec_phase`, `qec_pending_alignment`, `qec_recovery_open_qty`, `qec_unmapped_reason` |
| Mismatch state | `MismatchEscalationCoordinator` publishes block authority at `system/modules/robot/core/Execution/MismatchEscalationCoordinator.cs:133`; release at `:706` | In-memory coordinator state plus logs | YES | RiskGate, UEA, watchdog | `mismatch_block_active`, `mismatch_block_reason`, `mismatch_release_ready` |
| Durable risk latches | `RiskLatchManager.Persist` at `RiskLatchManager.cs:79`; hydrate at `:137`; clear at `:100` | Durable until cleared | YES | engine/RiskGate/watchdog | `durable_latch_active`, `durable_latch_reason`, `durable_latch_age_ms` |
| Timetable/session state | session close sweep `RobotEngine.Session.cs:151`; reentry allowed time `:897`; timetable reload not mapped here | Current engine session state; live/replay files can be stale | YES | SSM, reentry, sweep, watchdog | `timetable_allowed`, `session_class`, `session_close_state`, `scheduled_exit_time` |
| Kill switch/operator disable | `KillSwitch.IsEnabled` at `system/modules/robot/core/Execution/KillSwitch.cs:99` | Cached for 5 seconds per `KillSwitch.cs:19` | YES | RiskGate/UEA | `kill_switch_active`, `kill_switch_reason` |
| Account snapshot freshness | `ExecutionSafetyEvaluationRequest` at `ExecutionSafetyGate.cs:266`; overlay evaluates freshness at `ExecutionSafetyGate.cs:124` | Depends on producer timestamp | YES | overlay, structural, UEA | `account_snapshot_age_ms`, `snapshot_error` |
| Playback/live mode | execution mode in RobotEngine/adapter, playback bypass flags in SSM | Stable per run but scenario pointer can be stale | YES | reentry suppression, summary | `execution_mode`, `is_playback`, `playback_scenario_id`, `is_multi_day_scenario` |
| Deployed hash/proof context | runtime signature emitted into robot logs; deploy scripts cited in `robot_subsystem_audit_map_2026-05-13.md:33` | Only fresh when runtime emits `ROBOT_BUILD_SIGNATURE` | YES | audits/operator readiness | `source_commit`, `build_hash`, `deployed_hash`, `runtime_signature_hash`, `proof_level` |

## Section 3 - Proposed ExecutionAuthorityFrame

Use the existing `ExecutionAuthorityFrame` name and expand it. Avoid adding a parallel frame type unless migration requires a temporary wrapper.

Proposed producer: `ExecutionAuthorityFrameBuilder`, owned by `UnifiedExecutionAuthority` or the adapter/engine boundary, not by individual validators.

Refresh timing:

- Build one frame immediately before each action decision.
- Do not reuse a frame for a later decision after order/execution callbacks, broker snapshot changes, journal writes, or stream lifecycle writes.
- Include snapshot captured time and computed age.
- Emit read-only frame snapshots in P0 before behavior routing.

Proposed C# shape:

```csharp
public sealed record ExecutionAuthorityFrame
{
    // Identity
    public string FrameId { get; init; } = "";
    public string Account { get; init; } = "";
    public string Instrument { get; init; } = "";
    public string CanonicalInstrument { get; init; } = "";
    public string ExecutionInstrumentKey { get; init; } = "";
    public string TradingDate { get; init; } = "";
    public string StreamId { get; init; } = "";
    public string IntentId { get; init; } = "";
    public string OrderRole { get; init; } = "";
    public string SubmitPath { get; init; } = "";
    public string ExecutionMode { get; init; } = "";
    public DateTimeOffset DecisionUtc { get; init; }
    public DateTimeOffset FrameCreatedUtc { get; init; }

    // Broker truth
    public int BrokerPositionQty { get; init; }
    public int BrokerWorkingOrdersCount { get; init; }
    public int BrokerStopQty { get; init; }
    public int BrokerTargetQty { get; init; }
    public IReadOnlyList<string> BrokerOrderIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BrokerOrderTags { get; init; } = Array.Empty<string>();
    public DateTimeOffset? BrokerSnapshotCapturedUtc { get; init; }
    public long? AccountSnapshotAgeMs { get; init; }
    public string? SnapshotError { get; init; }

    // Robot truth
    public int JournalOpenQty { get; init; }
    public int OwnershipOpenQty { get; init; }
    public int OwnershipSignedQty { get; init; }
    public int OwnershipActiveSlots { get; init; }
    public int OwnershipOrphanSlots { get; init; }
    public string StreamLifecycleState { get; init; } = "";
    public bool StreamCommitted { get; init; }
    public int ActiveIntentsCount { get; init; }
    public IReadOnlyList<string> ActiveIntentIds { get; init; } = Array.Empty<string>();
    public string ActiveReentryState { get; init; } = "";
    public DateTimeOffset? ScheduledExitTimeUtc { get; init; }

    // Registry/protection truth
    public int IeaRegistryWorkingCount { get; init; }
    public int IeaMismatchTrustedWorkingCount { get; init; }
    public string ProtectiveCoverageState { get; init; } = "";
    public int ProtectiveMissingQty { get; init; }
    public bool ProtectivePending { get; init; }
    public string QecPhase { get; init; } = "";
    public bool QecPendingAlignment { get; init; }
    public int QecRecoveryOpenQty { get; init; }

    // Authority/block truth
    public bool DurableLatchActive { get; init; }
    public string DurableLatchReason { get; init; } = "";
    public bool MismatchBlockActive { get; init; }
    public string MismatchBlockReason { get; init; } = "";
    public bool IeaSupervisoryBlock { get; init; }
    public string StructuralDeny { get; init; } = "";
    public bool OverlayLock { get; init; }
    public bool KillSwitchActive { get; init; }
    public bool TimetableAllowed { get; init; }
    public string SessionCloseState { get; init; } = "";

    // Proof/mode context
    public bool IsPlayback { get; init; }
    public bool IsMultiDayScenario { get; init; }
    public string PlaybackScenarioId { get; init; } = "";
    public string ProofLevel { get; init; } = "";
    public string RuntimeSignatureHash { get; init; } = "";

    // Derived truth
    public bool IsCleanFlat { get; init; }
    public bool HasTrackedExposure { get; init; }
    public bool HasUntrackedExposure { get; init; }
    public bool HasProtectedExposure { get; init; }
    public bool HasContradiction { get; init; }
    public IReadOnlyList<string> FailedPredicates { get; init; } = Array.Empty<string>();
}
```

Frame invariants:

1. `IsCleanFlat` requires broker qty 0, broker working orders 0, journal open qty 0, ownership open qty 0, active intents 0, nonterminal streams 0, and recovery open qty 0.
2. `HasTrackedExposure` is true when broker/journal/ownership/registry can explain exposure by robot intent or stream.
3. `HasUntrackedExposure` is true when broker qty or robot-tagged working orders exist without journal/ownership/registry explanation.
4. `HasProtectedExposure` requires broker exposure covered by aggregate stop quantity, or an explicit short-lived QEC pending alignment.
5. `HasContradiction` is true when broker, journal, ownership, registry, or stream lifecycle facts disagree after bounded pending windows.
6. A frame is invalid for decision if `AccountSnapshotAgeMs` exceeds action-specific freshness limits or `SnapshotError` is non-empty.

## Section 4 - Proposed ExecutionAuthorityDecision

Use one canonical decision shape for submit and non-submit actions. The current `AuthorityDecision` and `ExecutionAuthorityActionDecision` should converge into this shape.

```csharp
public enum ExecutionAuthorityActionType
{
    ENTRY_SUBMIT,
    REENTRY_SUBMIT,
    PROTECTIVE_SUBMIT,
    FLATTEN_SUBMIT,
    CANCEL_SUBMIT,
    STREAM_COMMIT,
    JOURNAL_COMPLETE_BROKER_FLAT,
    MISMATCH_RELEASE,
    LATCH_CLEAR,
    SHUTDOWN_SAFE_VERDICT
}

public enum ExecutionAuthoritySeverity
{
    INFO,
    WARN,
    BLOCK,
    FAIL_CLOSED,
    CRITICAL
}

public sealed record ExecutionAuthorityDecision
{
    public bool Allowed { get; init; }
    public ExecutionAuthorityActionType ActionType { get; init; }
    public string ReasonCode { get; init; } = "";
    public ExecutionAuthoritySeverity Severity { get; init; }
    public string Layer { get; init; } = "UEA";
    public ExecutionAuthorityFrame SourceEvidence { get; init; } = new();
    public IReadOnlyList<string> FailedPredicates { get; init; } = Array.Empty<string>();
    public bool BlocksEntries { get; init; }
    public bool BlocksReentry { get; init; }
    public bool BlocksProtectives { get; init; }
    public bool BlocksFlatten { get; init; }
    public bool AllowsSafetyActions { get; init; }
    public string OperatorAction { get; init; } = "";
    public string LogEventName { get; init; } = "";
    public bool DurableStateChangeAllowed { get; init; }
    public string ClearCondition { get; init; } = "";
    public string ProofContext { get; init; } = "";
}
```

Required action policies:

| Action | Default stance | Non-negotiable rule |
|---|---|---|
| `ENTRY_SUBMIT` | deny if any hard block/contradiction | Never risk-increase under latch, mismatch, kill switch, stale timetable, stale account snapshot, or unexplained broker exposure. |
| `REENTRY_SUBMIT` | deny unless broker-flat and lifecycle evidence is coherent | Reentry is risk-increasing and must not bypass blocks except a narrowly proven late-session-close stale latch bypass. |
| `PROTECTIVE_SUBMIT` | allow through entry blocks | Deny only for stronger safety reasons such as invalid order structure, stale account context, or overfill/quantity policy failure. |
| `FLATTEN_SUBMIT` | allow through entry blocks | Deny only when flatten would be structurally unsafe or account snapshot/NT context is invalid. |
| `CANCEL_SUBMIT` | allow safety cancels | Deny only when cancellation would remove sole protection without replacement/flatten. |
| `STREAM_COMMIT` | deny on open lifecycle exposure | Terminal state cannot hide broker/journal/ownership exposure. |
| `JOURNAL_COMPLETE_BROKER_FLAT` | deny unless clean-flat predicates pass for that instrument scope | Broker net flat alone is insufficient. |
| `MISMATCH_RELEASE` | deny until release readiness is stable and fresh | Stable release must include broker/journal/ownership/registry convergence. |
| `LATCH_CLEAR` | deny if any clean-flat predicate fails | Clear must prove broker flat, no working orders, no open journal, no ownership open, no active intents, no recovery open. |
| `SHUTDOWN_SAFE_VERDICT` | deny unless all clean-flat predicates pass globally | Summary/reporters consume decision, they do not override evidence. |

## Section 5 - Subsystem Ownership

| Subsystem | Current role | Future role | Decisions it loses | Evidence it provides | Migration risk |
|---|---|---|---|---|---|
| StreamStateMachine | Lifecycle owner and partial authority for commit/reentry/forced flatten | EVIDENCE_PROVIDER + EXECUTOR | final commit safety, final reentry permission | stream state, journal lifecycle, range state, scheduled times | High: currently mutates durable terminal state. |
| ReconciliationRunner | Observer plus journal broker-flat closer | EVIDENCE_PROVIDER + EXECUTOR | final broker-flat journal completion | broker/journal/ownership comparison, closure candidates | High: false broker-flat completion is capital-risk relevant. |
| MismatchEscalationCoordinator | Block/release authority | VALIDATOR + REPORTER initially, then executor for UEA release | final mismatch release | mismatch block state, release readiness, stability window | High: release too early or stuck block. |
| IEA | Command serializer, lifecycle/registry owner | EXECUTOR + EVIDENCE_PROVIDER | final allow/deny for risk actions | command queue health, lifecycle state, registry counts, supervisory block | Medium-high: command serialization remains critical. |
| OrderRegistry | Working-order truth classifier | EVIDENCE_PROVIDER + VALIDATOR | final broker exposure classification | owned/adopted/recoverable working counts, broker id aliases | High: registry drift has recent failures. |
| ExecutionJournal | Durable trade truth and closure writer | EVIDENCE_PROVIDER + EXECUTOR | final broker-flat completion permission | open qty, intent state, fills, rejects, adoption candidates | High: false closed journal hides exposure. |
| OwnershipLedger | Broker exposure explanation | EVIDENCE_PROVIDER | final clean-flat claim | ownership slots, signed/gross open, orphan slots | Medium: stale snapshots can mislead summary. |
| QEC | Pending/convergence state | EVIDENCE_PROVIDER + VALIDATOR | final protective/entry block | pending alignment, unmapped/recovery state | Medium: pending windows must be bounded. |
| EPA | Preflight blocker | VALIDATOR | final submit allow/deny | kill/recovery/path applicability result | Medium: duplicate gate with UEA. |
| UEA | Submit facade; emerging action authority | FINAL_AUTHORITY | none | canonical decision and logs | High: must not become another parallel layer. |
| StructuralLayer | Structural submit/flatten checks | VALIDATOR | final submit/flatten allow/deny | structure contradictions, repair active, parity block | Medium: strong safety validator. |
| Overlay/SafetyGate | Last overlay lock/freshness check | VALIDATOR | final allow/deny | unsafe lock, snapshot freshness, manual clear validation | Medium: must remain fail-closed as validator. |
| RiskGate | Legacy stream gate | LEGACY_TO_DEMOTE then VALIDATOR | final entry/reentry block | operator/kill/recovery failed predicates | Medium: currently still blocks paths that call it. |
| Session-close sweep | Broker-level flatten fallback | EXECUTOR + EVIDENCE_PROVIDER | final sweep allow/deny | candidate exposure, stale sweep claim, reentry blocker evidence | Medium-high: stale close sweep caused post-reopen risk. |
| ProtectiveAudit/Coordinator | Coverage validator and recovery executor | VALIDATOR + EXECUTOR | final entry block/flatten escalation decision | missing stop, qty mismatch, pending convergence, recovery state | High: must not weaken protective safety. |
| RunSummaryBuilder | Verdict builder | REPORTER | final shutdown safe decision | shutdown aggregate evidence | Medium: must report, not decide contrary to UEA. |
| Watchdog | Operator reporter/control plane | REPORTER + EVIDENCE_PROVIDER for operator actions | runtime truth decisions | stale/unsafe/operator view, latch visibility | Medium: false safe is unacceptable. |

## Section 6 - Migration Strategy

### P0 - Read-Only Frame

Behavior change: NO.

Tasks:

1. Implement `ExecutionAuthorityFrameBuilder`.
2. Build frames for entry, reentry, protective submit, flatten, stream commit, broker-flat journal completion, mismatch release, latch clear, and shutdown verdict.
3. Emit `AUTHORITY_FRAME_SNAPSHOT` JSONL to the run root and robot log.
4. Keep existing behavior active.
5. Add `AUTHORITY_FRAME_DISAGREEMENT` when canonical derived predicates disagree with legacy allow/block.

P0 fail-closed rule: if canonical says unsafe while legacy would allow, log CRITICAL first. Do not change behavior in the first patch unless explicitly promoted after harness proof.

### P1 - Shadow Decision

Behavior change: limited fail-closed only after tests.

Tasks:

1. Canonical authority computes decision in parallel.
2. Existing behavior remains active.
3. If canonical denies a risk-increasing action and legacy allows, block only after focused harness coverage for that action.
4. If legacy blocks while canonical allows, log only. Do not auto-allow.

### P2 - Route Narrow Decisions

Route lowest-risk decisions first:

1. `STREAM_COMMIT`
2. `JOURNAL_COMPLETE_BROKER_FLAT`
3. `LATCH_CLEAR`
4. `SHUTDOWN_SAFE_VERDICT`

Then route risk/execution decisions:

5. `ENTRY_SUBMIT`
6. `REENTRY_SUBMIT`
7. `PROTECTIVE_SUBMIT`
8. `FLATTEN_SUBMIT`
9. `MISMATCH_RELEASE`

### P3 - Demote Legacy

Behavior change: YES, after P2 proofs.

- EPA/Structural/Overlay/RiskGate become validators.
- SSM requests lifecycle actions but does not independently claim safe/terminal.
- Reconciliation observes and recommends; UEA decides release/repair permission.
- Session-close sweep requests flatten through UEA only.
- Protective coordinator requests recovery/flatten through UEA only.

### P4 - Remove/Delete

Only after:

- focused harnesses pass,
- exact failing playback/SIM shapes are fixed,
- representative single-day playback passes,
- representative SIM/runtime pass confirms deployed `ROBOT_BUILD_SIGNATURE`,
- no clean-flat contradiction appears at shutdown,
- no CRITICAL registry/protective/reconciliation invariant events remain unexplained.

## Section 7 - Hard Invariants

1. Clean flat requires broker qty 0, broker working orders 0, journal open qty 0, ownership open qty 0, active intents 0, nonterminal streams 0, and recovery open qty 0.
2. A stream cannot commit terminal if journal open qty > 0, ownership open qty > 0, active reentry is pending/filled but not terminal, or broker exposure is tracked to that stream.
3. Journal cannot complete `RECONCILIATION_BROKER_FLAT` unless broker qty 0, broker working orders 0, ownership qty 0, stream lifecycle is terminal or safe-flat, and no active protective/reentry state exists.
4. Entry/reentry can be blocked by latch, mismatch, kill switch, or session/timetable. Protective and emergency flatten must remain allowed unless a stronger safety reason exists.
5. Broker net flat is not clean flat if gross journal/ownership exposure remains.
6. Shutdown safe verdict cannot be true if broker qty, working orders, journal open qty, ownership open qty, active intents, or nonterminal streams remain.
7. A read error in kill switch or core evidence must fail closed for risk-increasing actions.
8. Protective under-coverage must be CRITICAL unless QEC proves a bounded pending alignment window.
9. Any frame contradiction must block risk-increasing actions until a later fresh frame clears it.
10. Runtime readiness cannot be upgraded beyond the strongest deployed hash/signature proof available.

## Section 8 - Tests Required

| Test | Purpose | Suggested location |
|---|---|---|
| `AuthorityFrame_CleanFlat_AllPredicatesPass` | Frame detects clean flat | `system/modules/robot/core/Tests/AuthorityFrameTests.cs` |
| `AuthorityFrame_TrackedExposure_ExplainedByJournalOwnershipRegistry` | Frame detects tracked exposure | same |
| `AuthorityFrame_UntrackedBrokerExposure_FailsClosed` | Frame detects untracked exposure | same |
| `AuthorityFrame_BrokerNetFlatGrossOpen_IsContradiction` | Broker net flat but gross open is not clean flat | same |
| `Authority_StreamCommit_DeniedWithOpenJournal` | terminal commit denied with open journal | extend `AuthorityContradictionTests.cs` |
| `Authority_JournalBrokerFlatCompletion_DeniedWithWorkingOrders` | broker-flat completion denied when working orders remain | new release/authority test |
| `Authority_LatchClear_DeniedWithAnyFailedPredicate` | latch clear requires clean-flat | new latch authority test |
| `Authority_ProtectiveSubmit_AllowedDespiteEntryLatch` | protective coverage bypasses ordinary entry latch | submit/protective integration test |
| `Authority_EmergencyFlatten_AllowedDespiteEntryLatch` | emergency flatten bypasses ordinary entry latch | flatten authority test |
| `Playback_SingleDay_SuppressesReentryWithoutScenario` | no single-day playback reentry exposure | `ReentryMarketCloseCommitTests.cs` plus playback validation |
| `Playback_ExplicitMultiDay_AllowsCarryoverReentry` | explicit scenario carryover allowed | `PlaybackScenarioTests.cs` |
| `Authority_FrameDisagreement_Logged` | shadow disagreement event emitted | harness/log test |
| `Authority_LegacyAllowCanonicalDeny_FailsClosedAfterPromotion` | P1 promoted gate blocks unsafe legacy allow | action-specific tests |
| `Authority_ShutdownSafeVerdict_RequiresCleanFlat` | shutdown safe requires all clean-flat predicates | `RunSummaryBuilderTests.cs` or new authority verdict test |

## Section 9 - Current Failure Mapping

| Failure | Missing/wrong evidence | Frame field that would catch it | Legacy path conflict | Required migration step |
|---|---|---|---|---|
| MNG/MYM open exposure at shutdown | Runtime had broker open qty 8, working orders 8, journal open qty 8 per `docs/audits/robot_subsystem_audit_map_2026-05-13.md:10`; later audit shows MNG/MYM counts agreeing at `forced_flatten_reentry_time_exit_audit_2026-05-13.md:28` | `broker_position_qty`, `broker_working_orders_count`, `journal_open_qty`, `ownership_open_qty`, `has_tracked_exposure`, `is_clean_flat=false` | Run summary reported unsafe, but lifecycle/reentry/flatten allowed an unsafe terminal run shape | P0 frame snapshots, P2 shutdown verdict, P2 stream commit/journal completion. |
| YM adoption `expected_qty=0` | Adoption quantity policy can be missing/restored in `InstrumentExecutionAuthority.OrderRegistry.cs:364`, failure reason at `:432` | `iea_registry_working_count`, `qec_recovery_open_qty`, `failed_predicates=adoption_qty_policy_missing` | Adoption/registry can classify without one canonical quantity-policy field | P0 frame includes policy expected/max qty; P2 journal/adoption release authority. |
| NG false `RECONCILIATION_BROKER_FLAT` completion | Broker-flat closure is guarded in `ReconciliationRunner.cs:741`, `:756`, `:773`, but closure still mutates journal at `:796` | `broker_position_qty`, `broker_working_orders_count`, `ownership_open_qty`, `stream_lifecycle_state`, `protective_pending` | Reconciliation can complete journal independently of UEA | P2 route `JOURNAL_COMPLETE_BROKER_FLAT`. |
| Stale MNG durable latch | Latches persist at `RiskLatchManager.cs:79`, hydrate at `:137`, clear at `:100`; auto-clear path at `RobotEngine.Reconciliation.cs:698`, `:744`, `:764` | `durable_latch_active`, `durable_latch_reason`, `failed_predicates`, `clear_condition` | Latch clear currently engine/reconciliation local | P2 route `LATCH_CLEAR`; watchdog reports clear predicates. |
| Broker protective orders present but IEA registry count 0 | Recent audit says M2K had broker working count 2, IEA/mismatch-trusted 0 at `forced_flatten_reentry_time_exit_audit_2026-05-13.md:136-137`; registry trusted count is `InstrumentExecutionAuthority.OrderRegistry.cs:121` | `broker_working_orders_count`, `iea_registry_working_count`, `iea_mismatch_trusted_working_count`, `has_contradiction` | Reconciliation invariant fired during successful forced flatten | P0 frame disagreement plus P2 flatten/protective convergence fields. |
| Stream `DONE` while reentry/journal exposure open | Active audit root cause states stream terminalized while reentry open at `forced_flatten_reentry_time_exit_audit_2026-05-13.md:208`; current commit route now uses UEA at `StreamStateMachine.Commit.cs:386` | `stream_committed`, `active_reentry_state`, `journal_open_qty`, `has_tracked_exposure` | SSM terminal commit used local lifecycle truth | P2 `STREAM_COMMIT`; already partially routed, still needs complete frame. |
| Single-day playback reentry exposure at stop | Audit says single-day isolated playback opened market-reentry positions after session-close flatten at `forced_flatten_reentry_time_exit_audit_2026-05-13.md:35`; suppression code cited at `:39-41` | `is_playback`, `is_multi_day_scenario`, `active_reentry_state`, `session_close_state` | Reentry path treated single-day playback like explicit multi-day scenario | P2 `REENTRY_SUBMIT`; frame must include playback scenario context. |
| Missed May 12 trades / stale carryover occupying slots | Deep audit says ES2/NG2/YM1 occupied by carried prior-day reentry journals at `playback_scenario_20260511_20260512_20260513T224843Z_deep_audit_2026-05-13.md:69` | `stream_journal_key`, `prior_journal_key`, `stream_lifecycle_state`, `trading_date`, `active_reentry_state` | Stream/timetable lifecycle allowed prior-day state to occupy fresh slot | P0 frame for stream activation; P2 stream commit/carryover authority. |
| NG persisted in `RANGE_LOCKED` with real exposure | Deep audit says NG1 had real open exposure while stream journal still said `RANGE_LOCKED` at `playback...deep_audit_2026-05-13.md:111` | `stream_lifecycle_state`, `journal_open_qty`, `broker_position_qty`, `has_contradiction` | Stream state and execution journal truth diverged | P0 frame contradiction logs; P2 stream lifecycle authority. |
| Shutdown summary inflated by stale ownership snapshots | Deep audit says stale snapshots inflated ownership open qty at `playback...deep_audit_2026-05-13.md:147` | `ownership_snapshot_age_ms`, `ownership_active_slots`, `ownership_orphan_slots`, `shutdown_reference_utc` | Summary consumed stale reporter evidence | P2 `SHUTDOWN_SAFE_VERDICT`; reporter consumes frame freshness. |

## Section 10 - Concrete Implementation Plan

| Task | Files | Behavior change? | Risk | Tests | Proof needed |
|---|---|---:|---|---|---|
| Add `ExecutionAuthorityFrameBuilder` read-only | `AuthorityDecisionModels.cs`, new `ExecutionAuthorityFrameBuilder.cs`, possible `Robot.Core.csproj` link if new file | NO | Low | new `AuthorityFrameTests` | build-proven + harness-proven |
| Emit `AUTHORITY_FRAME_SNAPSHOT` for submit actions | `NinjaTraderSimAdapter.SubmitGate.cs`, `RobotEventTypes.*` | NO | Low-medium log volume | frame snapshot test, submit gate tests | harness-proven |
| Emit frames for stream commit and session sweep | `StreamStateMachine.Commit.cs`, `RobotEngine.Session.cs` | NO | Low | existing `AUTHORITY_CONTRADICTIONS`, `REENTRY_MARKET_CLOSE_COMMIT` | harness-proven |
| Emit frames for broker-flat journal completion | `ReconciliationRunner.cs`, `ExecutionJournal.AdoptionRelease.cs` | NO | Medium | journal broker-flat denial tests | harness-proven |
| Emit frames for mismatch release | `RobotEngine.Reconciliation.Release.cs`, `MismatchEscalationCoordinator.cs` | NO | Medium | state consistency release tests | harness-proven |
| Emit frames for latch clear | `RiskLatchManager.cs`, `RobotEngine.Reconciliation.cs` | NO | Medium | latch clear predicate tests | harness-proven |
| Emit frames for shutdown verdict | `RobotEngine.Shutdown.cs`, `RunSummaryBuilder.cs` | NO | Low-medium | shutdown safe verdict test | harness-proven |
| Add shadow canonical decisions and disagreement logs | `UnifiedExecutionAuthority.cs`, frame builder, `RobotEventTypes.*` | NO initially | Medium | `AUTHORITY_FRAME_DISAGREEMENT` test | harness-proven |
| Promote `STREAM_COMMIT` authority | `StreamStateMachine.Commit.cs` | YES, already partial | Medium | reentry/commit tests | harness + exact failing playback/SIM |
| Promote `JOURNAL_COMPLETE_BROKER_FLAT` authority | `ReconciliationRunner.cs`, `ExecutionJournal.AdoptionRelease.cs` | YES | High | broker-flat completion denied tests | playback + SIM/runtime |
| Promote `LATCH_CLEAR` authority | `RobotEngine.Reconciliation.cs`, `RiskLatchManager.cs` | YES | Medium | latch clear tests | harness + SIM/runtime |
| Promote `SHUTDOWN_SAFE_VERDICT` authority | `RunSummaryBuilder.cs`, `RobotEngine.Shutdown.cs` | YES reporter behavior | Medium | summary builder tests | harness + runtime run summary |
| Promote `ENTRY_SUBMIT` and `REENTRY_SUBMIT` | `NinjaTraderSimAdapter.SubmitGate.cs`, `StreamStateMachine.Reentry.cs`, `RobotEngine.Reconciliation.cs` | YES | High | submit/reentry authority tests | exact failing playback + SIM/runtime |
| Promote `PROTECTIVE_SUBMIT` and `FLATTEN_SUBMIT` | submit gate, protective coordinator, flatten commands | YES | High | protective/flatten tests | SIM/runtime with broker flat/shutdown clean |
| Demote/remove legacy gates | EPA/structural/overlay/RiskGate callsites | YES | Highest | full focused suite | multiple clean runtime proofs |

## Section 11 - Final Recommendation

1. The one final authority should be `UnifiedExecutionAuthority`, backed by one expanded `ExecutionAuthorityFrame`.
2. Evidence providers should be broker/account snapshot, ExecutionJournal, StreamJournal/SSM, OwnershipLedger, IEA registry, QEC, ProtectiveCoverageAudit, MismatchEscalationCoordinator, RiskLatchManager, timetable/session authority, kill switch, and deployment/hash proof.
3. Validators should be EPA, StructuralLayer, Overlay/SafetyGate, QEC, ProtectiveCoverageAudit, StateConsistencyReleaseEvaluator, and OrderRegistry consistency checks.
4. Dangerous legacy decision paths are stream terminal commit, reconciliation broker-flat journal completion, mismatch release, latch auto-clear, session-close sweep flatten, RiskGate entry/reentry block, protective recovery escalation, and run summary shutdown safe classification.
5. The safest first implementation patch is P0 read-only frame emission plus `AUTHORITY_FRAME_DISAGREEMENT`; do not change submit behavior in that patch.
6. Before deleting legacy guards, prove the canonical frame and UEA decisions through focused harnesses, exact failing playback/SIM replay, one representative SIM/runtime day with deployed `ROBOT_BUILD_SIGNATURE`, broker flat/no working orders/no open journals/no stale ownership at shutdown, and no unexplained CRITICAL registry/protective/reconciliation events.
