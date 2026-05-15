using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Diagnostics;
using QTSW2.Robot.Core.Execution;
using QTSW2.Robot.Core.Notifications;

public sealed partial class RobotEngine
{

    /// <summary>
    /// Phase 3: canonical unexplained exposure for mismatch convergence escape hatch. Uses full release readiness for
    /// working / coherence / IEA, but position explainability uses the same PendingFillBridge + journal aggregation as
    /// <see cref="AssembleMismatchObservations"/> (narrow path — release evaluator inputs unchanged for other callers).
    /// </summary>
    private MismatchConvergenceCanonicalProbeResult ProbeMismatchConvergenceCanonicalExposure(string instrument,
        DateTimeOffset utcNow)
    {
        if (_executionAdapter == null)
            return new MismatchConvergenceCanonicalProbeResult { HasUnexplainedBrokerExposure = true };
        AccountSnapshot? snap;
        try
        {
            snap = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            return new MismatchConvergenceCanonicalProbeResult { HasUnexplainedBrokerExposure = true };
        }

        if (snap == null)
            return new MismatchConvergenceCanonicalProbeResult { HasUnexplainedBrokerExposure = true };
        if (string.IsNullOrWhiteSpace(instrument))
            return new MismatchConvergenceCanonicalProbeResult { HasUnexplainedBrokerExposure = true };

        var inst = instrument.Trim();
        var r = EvaluateStateConsistencyReleaseReadiness(instrument, snap, utcNow);
        if (!r.SnapshotSufficient)
            return new MismatchConvergenceCanonicalProbeResult { HasUnexplainedBrokerExposure = true };

        var canonicalForJournalAgg = GetCanonicalInstrument(inst) ?? inst;
        var openByInst = _executionJournal.GetOpenJournalEntriesByInstrument();
        var journalGrossRaw = _executionJournal.GetOpenJournalQuantitySumForInstrumentFromMap(openByInst, inst,
            canonicalForJournalAgg);
        var journalNetRaw = _executionJournal.GetOpenJournalSignedNetForInstrumentFromMap(openByInst, inst,
            canonicalForJournalAgg);
        var brokerGrossAbs = SumBrokerPositionQty(snap, inst);
        var netBrokerQty = SumBrokerPositionSignedQty(snap, inst);
        var (pendingGrossOv, pendingNetOv) = _pendingFillBridge.GetEffectiveOverlays(inst, canonicalForJournalAgg,
            journalGrossRaw, journalNetRaw, brokerGrossAbs, netBrokerQty, utcNow);

        var positionExplained = MismatchConvergenceBridgeProbeMath.IsPositionAggregateExplained(
            brokerGrossAbs,
            netBrokerQty,
            journalGrossRaw,
            journalNetRaw,
            pendingGrossOv,
            pendingNetOv,
            out var effectiveJournalGross,
            out var effectiveNetJournal,
            out var rawPosDiffGross,
            out var effectivePosDiffGross);

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString ?? "", eventType: "CONVERGENCE_PROBE_BRIDGE_AUDIT",
            state: "ENGINE",
            new
            {
                instrument = inst,
                raw_journal_qty = journalGrossRaw,
                bridge_gross_overlay = pendingGrossOv,
                effective_probe_journal_qty = effectiveJournalGross,
                raw_pos_diff = rawPosDiffGross,
                effective_pos_diff = effectivePosDiffGross,
                bridge_net_overlay = pendingNetOv,
                effective_probe_net_journal = effectiveNetJournal,
                position_aggregate_explained = positionExplained
            }));

        var positionUnexplained = !positionExplained;
        var has = positionUnexplained
                  || r.UnexplainedBrokerWorkingCount > 0
                  || !r.BrokerWorkingExplainable
                  || !r.LocalStateCoherent;

        var uPos = positionUnexplained
            ? MismatchConvergenceBridgeProbeMath.EffectiveUnexplainedPositionQty(brokerGrossAbs, netBrokerQty,
                effectiveJournalGross, effectiveNetJournal)
            : 0;

        return new MismatchConvergenceCanonicalProbeResult
        {
            HasUnexplainedBrokerExposure = has,
            UnexplainedBrokerPositionQty = uPos,
            UnexplainedBrokerWorkingCount = r.UnexplainedBrokerWorkingCount
        };
    }

    private StateConsistencyReleaseReadinessResult EvaluateStateConsistencyReleaseReadiness(string instrument,
        AccountSnapshot? snapshot, DateTimeOffset utcNow, bool forceFullEvaluation = false)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst))
            return StateConsistencyReleaseEvaluator.Indeterminate(instrument ?? "", "no_instrument");

        if (!forceFullEvaluation && snapshot != null &&
            TryBuildReleaseReadinessSuppressionProbe(inst, snapshot, utcNow, out var suppressionProbe))
        {
            var gen = _releaseReconRedundancy.ExecutionActivityGeneration;
            if (_releaseReconRedundancy.TryGetCachedReleaseReadiness(inst, in suppressionProbe, gen, utcNow, out var cached,
                    out var suppressionReason))
            {
                LogReleaseSuppressionDecisionIfDiag(inst, skipped: true, suppressionReason, in suppressionProbe, gen);
                return ApplyMismatchReleaseAuthority(inst, snapshot, utcNow, null, cached!,
                    "RobotEngine.MismatchRelease.Cached");
            }

            LogReleaseSuppressionDecisionIfDiag(inst, skipped: false, suppressionReason, in suppressionProbe, gen);
        }

        var genAtStart = _releaseReconRedundancy.ExecutionActivityGeneration;
        var input = BuildStateConsistencyReleaseEvaluationInput(inst, snapshot, utcNow);
        var result = StateConsistencyReleaseEvaluator.Evaluate(input);
        result = ApplyMismatchReleaseAuthority(inst, snapshot, utcNow, input, result,
            "RobotEngine.MismatchRelease");

        if (snapshot != null && input.SnapshotSufficient && _journalIntegrityEnsuredForInstrument.ContainsKey(inst))
        {
            var canonicalPost = GetCanonicalInstrument(inst) ?? inst;
            var parityPost = JournalParityChecker.CheckJournalParity(inst, snapshot, _executionJournal,
                new JournalParityRegistryViewImpl
                {
                    UseInstrumentExecutionAuthority = input.UseInstrumentExecutionAuthority,
                    IeaOwnedPlusAdoptedWorking = input.IeaOwnedPlusAdoptedWorking
                }, canonicalPost, utcNow);
            if (!parityPost.IsOkOrPendingAlignment)
            {
                var knownConvergence =
                    PendingAlignmentAuthority.IsPendingAlignment(inst, utcNow) ||
                    QuantExecutionControlStore.IsPostFillAlignmentWindowActive(inst, utcNow) ||
                    QuantExecutionControlStore.IsWorkingOrderSubmitWindowActive(inst, utcNow) ||
                    QuantExecutionControlStore.IsBrokerExecutionCallbackPendingActive(inst, utcNow);
                if (!knownConvergence)
                {
                    LogJournalIntegrityInvariantOrTransient(inst, parityPost.Status.ToString(),
                        parityPost.BrokerPositionQty, parityPost.JournalOpenQty, utcNow);
                }
                else
                {
                    ClearJournalIntegrityInvariantDebounce(inst);
                }
            }
            else
            {
                ClearJournalIntegrityInvariantDebounce(inst);
            }
        }

        if (snapshot != null && input.SnapshotSufficient)
        {
            var fp = ReleaseReconciliationRedundancySuppression.BuildReleaseMaterialFingerprint(input, result);
            if (!_releaseReconRedundancy.ShouldSuppressIdenticalReadinessAudit(inst, fp, utcNow))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "RELEASE_READINESS_INPUT_AUDIT", "ENGINE",
                    new
                    {
                        instrument = inst,
                        broker_position_qty = input.BrokerPositionQty,
                        broker_working_count = input.BrokerWorkingCount,
                        journal_open_qty = input.JournalOpenQty,
                        ownership_snapshot_available = input.OwnershipSnapshotAvailable,
                        ownership_gross_open_qty = input.OwnershipGrossOpenQty,
                        ownership_signed_net_qty = input.OwnershipSignedNetQty,
                        ownership_active_slot_count = input.OwnershipActiveSlotCount,
                        ownership_orphan_slot_count = input.OwnershipOrphanSlotCount,
                        iea_trusted_working_count = input.IeaOwnedPlusAdoptedWorking,
                        pending_execution_workload = input.PendingExecutionWorkload,
                        pending_candidate_count = input.PendingAdoptionCandidateCount,
                        release_ready = result.ReleaseReady,
                        contradictions = string.Join(";", result.Contradictions ?? new List<string>())
                    }));
                _releaseReconRedundancy.MarkReadinessAuditEmitted(inst, fp, utcNow);
            }
        }

        if (snapshot != null && input.SnapshotSufficient)
            _releaseReconRedundancy.RecordReleaseFullEvaluation(inst, input, result, utcNow, genAtStart);

        return result;
    }

    private StateConsistencyReleaseReadinessResult ApplyMismatchReleaseAuthority(
        string inst,
        AccountSnapshot? snapshot,
        DateTimeOffset utcNow,
        StateConsistencyReleaseEvaluationInput? input,
        StateConsistencyReleaseReadinessResult result,
        string source)
    {
        var authorized = ReleaseReconciliationRedundancySuppression.CloneReadiness(result);
        var snapshotSufficient = input?.SnapshotSufficient ?? authorized.SnapshotSufficient;
        var brokerPositionQty = input?.BrokerPositionQty ?? authorized.DiagnosticBrokerPositionQty;
        var brokerWorkingCount = input?.BrokerWorkingCount ?? authorized.DiagnosticBrokerWorkingCount;
        var journalOpenQty = input?.JournalOpenQty ?? authorized.DiagnosticJournalOpenQty;
        var ownershipGrossOpenQty = input?.OwnershipGrossOpenQty ?? authorized.DiagnosticOwnershipGrossOpenQty;
        var ownershipSignedNetQty = input?.OwnershipSignedNetQty ?? authorized.DiagnosticOwnershipSignedNetQty;
        var ownershipActiveSlotCount = input?.OwnershipActiveSlotCount ?? authorized.DiagnosticOwnershipActiveSlotCount;
        var ownershipOrphanSlotCount = input?.OwnershipOrphanSlotCount ?? authorized.DiagnosticOwnershipOrphanSlotCount;
        var ieaOwnedPlusAdoptedWorking = input?.IeaOwnedPlusAdoptedWorking ?? authorized.DiagnosticIeaOwnedPlusAdoptedWorking;
        var useIea = input?.UseInstrumentExecutionAuthority ?? ieaOwnedPlusAdoptedWorking >= 0;
        var pendingExecutionWorkload = input?.PendingExecutionWorkload ?? authorized.DiagnosticPendingExecutionWorkload;

        var releaseFrame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            Source = source,
            Account = _accountName ?? "",
            Instrument = inst,
            CanonicalInstrument = GetCanonicalInstrument(inst) ?? inst,
            TradingDate = TradingDateString,
            SubmitPath = "MISMATCH_RELEASE",
            DecisionUtc = utcNow,
            FrameCreatedUtc = utcNow,
            BrokerSnapshotCapturedUtc = snapshot?.CapturedAtUtc,
            SnapshotError = !snapshotSufficient || snapshot == null ? "snapshot_unavailable" : null,
            BrokerPositionQty = brokerPositionQty,
            BrokerWorkingOrdersCount = brokerWorkingCount,
            JournalOpenQty = journalOpenQty,
            OwnershipOpenQty = ownershipGrossOpenQty,
            OwnershipSignedQty = ownershipSignedNetQty,
            OwnershipActiveSlots = ownershipActiveSlotCount,
            OwnershipOrphanSlots = ownershipOrphanSlotCount,
            IeaMismatchTrustedWorkingCount = ieaOwnedPlusAdoptedWorking,
            UseInstrumentExecutionAuthority = useIea,
            MismatchBlockActive = _mismatchCoordinator?.IsInstrumentBlockedByMismatch(inst) == true,
            MismatchBlockReason = authorized.ReleaseReady ? "" : string.Join(";", authorized.Contradictions ?? new List<string>()),
            AuthorityState = "MISMATCH_RELEASE"
        });
        var releaseAuthority = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.MismatchRelease,
            Source = source,
            Instrument = inst,
            UtcNow = utcNow,
            SnapshotSufficient = snapshotSufficient,
            ReleaseValidatorReady = authorized.ReleaseReady,
            PendingExecutionWorkload = pendingExecutionWorkload,
            BrokerAbsQty = Math.Abs(brokerPositionQty),
            BrokerWorkingOrderCount = brokerWorkingCount,
            JournalOpenQty = journalOpenQty,
            OwnershipOpenQty = ownershipGrossOpenQty,
            OwnershipActiveSlotCount = ownershipActiveSlotCount,
            OwnershipOrphanSlotCount = ownershipOrphanSlotCount,
            AuthorityFrame = releaseFrame
        });
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "AUTHORITY_FRAME_SNAPSHOT", state: "ENGINE",
            ExecutionAuthorityFrameBuilder.ToLogPayload(
                releaseFrame,
                action: "MISMATCH_RELEASE",
                decision: releaseAuthority.Allowed ? "ALLOW" : "DENY",
                denyReason: releaseAuthority.DenyReason)));

        authorized.CanonicalReleaseAuthorityAllowed =
            releaseAuthority.Allowed &&
            string.Equals(releaseAuthority.GateName, "AuthorityMismatchRelease", StringComparison.Ordinal);
        authorized.CanonicalReleaseAuthorityGate = releaseAuthority.GateName ?? "";
        authorized.CanonicalReleaseAuthorityDenyReason = releaseAuthority.DenyReason ?? "";
        authorized.CanonicalReleaseAuthorityFrameId = releaseAuthority.AuthorityFrame?.FrameId ?? releaseFrame.FrameId;

        if (releaseAuthority.Allowed)
            return authorized;

        if (authorized.ReleaseReady)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "AUTHORITY_FRAME_UNSAFE_LEGACY_ALLOW", state: "ENGINE",
                new
                {
                    action = "MISMATCH_RELEASE",
                    instrument = inst,
                    authority_gate = releaseAuthority.GateName,
                    authority_deny_reason = releaseAuthority.DenyReason ?? "UNKNOWN",
                    authority_detail = releaseAuthority.Detail ?? "",
                    note = "State-consistency validator was release-ready, but canonical authority denied mismatch release."
                }));
        }

        authorized.ReleaseReady = false;
        var denyReason = releaseAuthority.DenyReason ?? "UNKNOWN";
        authorized.Contradictions ??= new List<string>();
        authorized.Contradictions.Add($"authority_mismatch_release_denied:{denyReason}");
        authorized.Summary = $"authority_mismatch_release_denied:{denyReason}";
        return authorized;
    }

    private void LogBrokerFlatJournalRepairDeferredToAuthority(
        string source,
        string instrument,
        string executionInstrumentKey,
        string canonicalInstrument,
        int brokerPositionQty,
        int brokerWorkingCount,
        int journalOpenQty,
        int pendingExecutionWorkload,
        bool pendingFlattenLifecycle,
        bool brokerJournalAlignmentActive,
        bool ownershipGrossOpen,
        DateTimeOffset utcNow)
    {
        if (journalOpenQty <= 0)
            return;

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
            eventType: "BROKER_FLAT_JOURNAL_REPAIR_DEFERRED_TO_AUTHORITY", state: "ENGINE",
            new
            {
                source,
                instrument,
                execution_instrument_key = executionInstrumentKey,
                canonical_instrument = canonicalInstrument,
                broker_position_qty = brokerPositionQty,
                broker_working_count = brokerWorkingCount,
                journal_open_qty = journalOpenQty,
                pending_execution_workload = pendingExecutionWorkload,
                pending_flatten_lifecycle = pendingFlattenLifecycle,
                broker_journal_alignment_active = brokerJournalAlignmentActive,
                ownership_gross_open = ownershipGrossOpen,
                required_authority_action = "JOURNAL_COMPLETE_BROKER_FLAT",
                note = "Release readiness is read-only for broker-flat journal repair; journal completion must be routed through canonical authority."
            }));
    }

    private StateConsistencyReleaseEvaluationInput BuildStateConsistencyReleaseEvaluationInput(string inst, AccountSnapshot snap, DateTimeOffset utcNow)
    {
        var input = new StateConsistencyReleaseEvaluationInput
        {
            Instrument = inst,
            SnapshotSufficient = snap != null,
            UseInstrumentExecutionAuthority = _executionPolicy?.UseInstrumentExecutionAuthority ?? false
        };
        if (snap == null) return input;

        _executionAdapter?.PrepareOrderRegistryForMismatchAssembly(inst, snap, utcNow);

        input.BrokerPositionQty = SumBrokerPositionQty(snap, inst);
        input.BrokerWorkingCount = CountBrokerWorkingOrders(snap, inst);
        var pendingIeAWorkload = GetPendingIeAWorkloadForBrokerInstrument(inst);
        input.PendingExecutionWorkload = pendingIeAWorkload;

        var brokerSigned = SumBrokerPositionSignedQty(snap, inst);
        var execVariant = inst.StartsWith("M", StringComparison.OrdinalIgnoreCase) && inst.Length > 1 ? inst : "M" + inst;
        var canonical = GetCanonicalInstrument(inst) ?? inst;
        var robotIntentIds = CollectRobotTaggedIntentIdsForInstrument(snap, inst);

        var useIea = input.UseInstrumentExecutionAuthority;
        var account = _accountName ?? "";
        InstrumentExecutionAuthority? ieaResolved = null;
        HashSet<string>? registryIntentIds = null;
        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, input.BrokerWorkingCount, GetExecutionInstrument, out ieaResolved, out _) &&
            ieaResolved != null)
        {
            input.IeaOwnedPlusAdoptedWorking = ieaResolved.GetMismatchTrustedWorkingCount();
            registryIntentIds = ieaResolved.GetMismatchTrustedWorkingIntentIds();
        }
        else
            input.IeaOwnedPlusAdoptedWorking = useIea ? -1 : 0;

        var releaseOwnershipKey = ieaResolved?.ExecutionInstrumentKey ?? inst;
        ApplyReleaseOwnershipSnapshot(input, TryGetReleaseOwnershipSnapshot(releaseOwnershipKey));

        var journalOpenQtyBeforePreSum =
            _executionJournal.GetOpenJournalStructuralStateForInstrument(inst, canonical).OpenQtySum;
        var integrityResult = RunEnsureJournalIntegrity(inst, snap, utcNow, markEnsuredForInvariant: true,
            readOnlyParityWhenPendingAlignment: true);
        var staleAdoptionJournalClosedCount = integrityResult.Reconstruction?.StaleAdoptionRowsClosed ?? 0;
        var journalAlignmentWriteCount = integrityResult.Reconstruction?.JournalWritesFromAlignment ?? 0;
        var recoveredIntentWrites = integrityResult.RecoveredIntentWrites;
        var brokerJournalAlignmentActive = QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow);
        var pendingFlattenLifecycle = HasPendingFlattenLifecycle(inst);
        var ownershipGrossOpen = HasReleaseOwnershipGrossOpen(input);
        var suppressBrokerFlatJournalClose =
            pendingFlattenLifecycle ||
            pendingIeAWorkload != 0 ||
            brokerJournalAlignmentActive ||
            ownershipGrossOpen;

        if (input.BrokerPositionQty == 0 && input.BrokerWorkingCount == 0)
        {
            LogBrokerFlatJournalRepairDeferredToAuthority(
                "ReleaseReadinessBrokerFlat",
                inst,
                releaseOwnershipKey,
                canonical,
                input.BrokerPositionQty,
                input.BrokerWorkingCount,
                journalOpenQtyBeforePreSum,
                pendingIeAWorkload,
                pendingFlattenLifecycle,
                brokerJournalAlignmentActive,
                ownershipGrossOpen,
                utcNow);
            if (!suppressBrokerFlatJournalClose && pendingIeAWorkload == 0)
            {
                LogBrokerFlatJournalRepairDeferredToAuthority(
                    "ResidualBrokerFlatCleanupPulse",
                    inst,
                    releaseOwnershipKey,
                    canonical,
                    input.BrokerPositionQty,
                    input.BrokerWorkingCount,
                    journalOpenQtyBeforePreSum,
                    pendingIeAWorkload,
                    pendingFlattenLifecycle,
                    brokerJournalAlignmentActive,
                    ownershipGrossOpen,
                    utcNow);
            }
        }

        var openJournalMap = _executionJournal.GetOpenJournalEntriesByInstrument();
        var (journalOpenQty, journalOpenIntentHash) =
            _executionJournal.GetOpenJournalStructuralStateForInstrumentFromMap(openJournalMap, inst, canonical);
        input.JournalOpenQty = journalOpenQty;
        input.JournalOpenIntentSetHash = journalOpenIntentHash;
        if (input.BrokerPositionQty != 0 && journalOpenQty == 0 ||
            journalOpenQtyBeforePreSum != journalOpenQty ||
            staleAdoptionJournalClosedCount != 0 ||
            journalAlignmentWriteCount != 0 ||
            recoveredIntentWrites != 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, "", "RELEASE_READINESS_JOURNAL_PRE_SUM_CHAIN", "ENGINE",
                new
                {
                    run_id = _runId ?? "",
                    instrument = inst,
                    canonical_instrument = canonical,
                    broker_position_qty = input.BrokerPositionQty,
                    journal_open_qty_before_pre_sum_chain = journalOpenQtyBeforePreSum,
                    stale_adoption_journal_closed_count = staleAdoptionJournalClosedCount,
                    journal_alignment_write_count = journalAlignmentWriteCount,
                    broker_flat_journal_closed_count = 0,
                    residual_broker_flat_journal_closed_count = 0,
                    broker_flat_journal_repair_deferred_to_authority = input.BrokerPositionQty == 0 &&
                                                                       input.BrokerWorkingCount == 0 &&
                                                                       journalOpenQtyBeforePreSum > 0,
                    ownership_snapshot_available = input.OwnershipSnapshotAvailable,
                    ownership_gross_open_qty = input.OwnershipGrossOpenQty,
                    ownership_signed_net_qty = input.OwnershipSignedNetQty,
                    ownership_active_slot_count = input.OwnershipActiveSlotCount,
                    ownership_orphan_slot_count = input.OwnershipOrphanSlotCount,
                    recovered_intent_writes = recoveredIntentWrites,
                    journal_open_qty_after_pre_sum_chain = journalOpenQty,
                    note =
                        "Journal scan before stale-adoption reconciliation vs structural sum after integrity alignment; broker-flat journal repair is deferred to canonical authority"
                }));
        }
        if (input.BrokerPositionQty != 0)
        {
            var (misusedSecondArgQty, _) =
                _executionJournal.GetOpenJournalStructuralStateForInstrumentFromMapMisusedExecVariantAsCanonical(
                    openJournalMap, inst, execVariant);
            if (journalOpenQty > misusedSecondArgQty)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "JOURNAL_OPEN_QTY_KEY_MISMATCH_SUSPECTED", "ENGINE",
                    new
                    {
                        instrument = inst,
                        canonical_instrument = canonical,
                        exec_variant = execVariant,
                        broker_position_qty = input.BrokerPositionQty,
                        journal_open_qty = journalOpenQty,
                        journal_open_qty_misused_exec_variant_second_arg = misusedSecondArgQty,
                        note =
                            "Open journal rows under canonical/execution family were excluded when second aggregation arg was execVariant (e.g. MES,MES) instead of true canonical (ES); canonical-aware sum is higher"
                    }));
            }
        }
        input.RegistryMismatchTrustedIntentSetHash =
            ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHash(registryIntentIds);
        input.BlockingAdoptionIntentSetHash = 0;

        if (ieaResolved != null)
        {
            var audit = _executionJournal.BuildReleaseBlockingCandidateAudit(
                ieaResolved.ExecutionInstrumentKey,
                canonical,
                input.BrokerPositionQty,
                brokerSigned,
                robotIntentIds,
                registryIntentIds);
            input.PendingAdoptionCandidateCount = audit.BlockingCandidateCount;
            input.BlockingAdoptionIntentSetHash = audit.BlockingIntentSetHash;
            input.BlockingCandidateDiagnostics = _executionJournal.BuildReleaseBlockingCandidateDiagnostics(
                ieaResolved.ExecutionInstrumentKey,
                canonical,
                input.BrokerPositionQty,
                brokerSigned,
                robotIntentIds,
                registryIntentIds);
            input.ReconciliationBlockers = _executionJournal.BuildReconciliationBlockers(
                ieaResolved.ExecutionInstrumentKey,
                canonical,
                input.BrokerPositionQty,
                brokerSigned,
                robotIntentIds,
                registryIntentIds,
                utcNow);
            var blockingFp = ReleaseReconciliationRedundancySuppression.BuildBlockingCandidateAuditFingerprint(input);
            if (!_releaseReconRedundancy.ShouldSuppressBlockingCandidateAudit(inst, blockingFp, utcNow))
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "RELEASE_BLOCKING_CANDIDATE_AUDIT", "ENGINE",
                    new
                    {
                        instrument = inst,
                        broker_position_qty = input.BrokerPositionQty,
                        raw_candidate_count = audit.RawCandidateCount,
                        blocking_candidate_count = audit.BlockingCandidateCount,
                        excluded_candidate_count = audit.ExcludedCandidateCount,
                        blocking_intent_ids_sample = audit.BlockingIntentIdsSample.ToArray(),
                        excluded_intent_ids_sample = audit.ExcludedIntentIdsSample.ToArray(),
                        exclusion_reasons_sample = audit.ExclusionReasonsSample.ToArray()
                    }));
                _releaseReconRedundancy.MarkBlockingCandidateAuditEmitted(inst, blockingFp, utcNow);
            }
        }

        input.PendingAlignmentActive = PendingAlignmentAuthority.IsPendingAlignment(inst, utcNow);
        return input;
    }

    private bool TryBuildReleaseReadinessSuppressionProbe(string inst, AccountSnapshot snapshot, DateTimeOffset utcNow,
        out ReleaseReadinessSuppressionProbe probe)
    {
        probe = default;
        if (string.IsNullOrWhiteSpace(inst)) return false;

        _executionAdapter?.PrepareOrderRegistryForMismatchAssembly(inst, snapshot, utcNow);

        var bp = SumBrokerPositionQty(snapshot, inst);
        var bw = CountBrokerWorkingOrders(snapshot, inst);
        var brokerSigned = SumBrokerPositionSignedQty(snapshot, inst);
        var execVariant = inst.StartsWith("M", StringComparison.OrdinalIgnoreCase) && inst.Length > 1 ? inst : "M" + inst;
        var canonical = GetCanonicalInstrument(inst) ?? inst;
        var robotIntentIds = CollectRobotTaggedIntentIdsForInstrument(snapshot, inst);

        var useIea = _executionPolicy?.UseInstrumentExecutionAuthority ?? false;
        var account = _accountName ?? "";
        var pendingExecutionWorkload = GetPendingIeAWorkloadForBrokerInstrument(inst);
        InstrumentExecutionAuthority? ieaResolved = null;
        HashSet<string>? registryIntentIds = null;
        var ieaTrusted = 0;
        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, bw, GetExecutionInstrument, out ieaResolved, out _) &&
            ieaResolved != null)
        {
            ieaTrusted = ieaResolved.GetMismatchTrustedWorkingCount();
            registryIntentIds = ieaResolved.GetMismatchTrustedWorkingIntentIds();
        }
        else if (useIea)
            ieaTrusted = -1;

        var ownershipProbeInput = new StateConsistencyReleaseEvaluationInput();
        ApplyReleaseOwnershipSnapshot(ownershipProbeInput,
            TryGetReleaseOwnershipSnapshot(ieaResolved?.ExecutionInstrumentKey ?? inst));

        var (journalQty, journalIntentHash) =
            _executionJournal.GetOpenJournalStructuralStateForInstrument(inst, canonical);

        var pending = 0;
        long blockingHash = 0;
        if (ieaResolved != null)
        {
            var (pc, bh) = _executionJournal.GetReleaseBlockingAdoptionStructuralFingerprint(
                ieaResolved.ExecutionInstrumentKey,
                canonical,
                bp,
                brokerSigned,
                robotIntentIds,
                registryIntentIds);
            pending = pc;
            blockingHash = bh;
        }

        var registryHash = ReleaseReconciliationRedundancySuppression.ComputeStableOrderedStringSetHash(registryIntentIds);

        probe = new ReleaseReadinessSuppressionProbe
        {
            BrokerPositionQty = bp,
            BrokerWorkingCount = bw,
            JournalOpenQty = journalQty,
            PendingExecutionWorkload = pendingExecutionWorkload,
            PendingCandidateCount = pending,
            IeaTrustedWorkingCount = ieaTrusted,
            UseIea = useIea,
            OwnershipSnapshotAvailable = ownershipProbeInput.OwnershipSnapshotAvailable,
            OwnershipGrossOpenQty = ownershipProbeInput.OwnershipGrossOpenQty,
            OwnershipSignedNetQty = ownershipProbeInput.OwnershipSignedNetQty,
            OwnershipActiveSlotCount = ownershipProbeInput.OwnershipActiveSlotCount,
            OwnershipOrphanSlotCount = ownershipProbeInput.OwnershipOrphanSlotCount,
            BlockingAdoptionIntentSetHash = blockingHash,
            RegistryMismatchTrustedIntentSetHash = registryHash,
            JournalOpenIntentSetHash = journalIntentHash
        };
        return true;
    }

    private InstrumentOwnershipSnapshot? TryGetReleaseOwnershipSnapshot(string instrument)
    {
        if (!FeatureFlags.CanonicalOwnershipLedgerEnabled || _ownershipLedger == null ||
            string.IsNullOrWhiteSpace(instrument))
            return null;

        try
        {
            return _ownershipLedger.GetOwnershipSnapshot(OwnershipAccountKey, instrument.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyReleaseOwnershipSnapshot(StateConsistencyReleaseEvaluationInput input,
        InstrumentOwnershipSnapshot? snapshot)
    {
        if (snapshot == null)
            return;

        input.OwnershipSnapshotAvailable = true;
        input.OwnershipSignedNetQty = snapshot.LedgerSignedNetQty;
        input.OwnershipActiveSlotCount = snapshot.ActiveSlotCount;
        input.OwnershipOrphanSlotCount = snapshot.OrphanSlotCount;
        input.OwnershipGrossOpenQty = snapshot.Slots
            .Where(s => s.State != SlotState.Closed && s.Remaining > 0)
            .Sum(s => Math.Abs(s.Remaining));
    }

    private static bool HasReleaseOwnershipGrossOpen(StateConsistencyReleaseEvaluationInput input) =>
        input.OwnershipGrossOpenQty > 0 ||
        input.OwnershipActiveSlotCount > 0 ||
        input.OwnershipOrphanSlotCount > 0;

    private void LogReleaseSuppressionDecisionIfDiag(string instrument, bool skipped, string internalReason,
        in ReleaseReadinessSuppressionProbe probe, long activityGeneration)
    {
        if (_loggingConfig?.DiagnosticsEnabled != true) return;

        var fpHash = ReleaseReconciliationRedundancySuppression.ComputeFingerprintHash64(
            ReleaseReconciliationRedundancySuppression.BuildStructuralSuppressionKey(in probe));

        LogEvent(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "RELEASE_SUPPRESSION_DECISION", "ENGINE",
            new
            {
                instrumentation_source = "ReleaseReconciliationRedundancySuppression.release_readiness_cache",
                instrument,
                skipped,
                reason = MapReleaseSuppressionPayloadReason(skipped, internalReason),
                fingerprint_hash = fpHash,
                activity_generation = activityGeneration,
                pending_candidate_count = probe.PendingCandidateCount,
                broker_position_qty = probe.BrokerPositionQty,
                journal_open_qty = probe.JournalOpenQty,
                ownership_snapshot_available = probe.OwnershipSnapshotAvailable,
                ownership_gross_open_qty = probe.OwnershipGrossOpenQty,
                ownership_signed_net_qty = probe.OwnershipSignedNetQty,
                ownership_active_slot_count = probe.OwnershipActiveSlotCount,
                ownership_orphan_slot_count = probe.OwnershipOrphanSlotCount
            }));
    }

    private static string MapReleaseSuppressionPayloadReason(bool skipped, string internalReason)
    {
        if (skipped)
            return "fingerprint_match";
        if (string.Equals(internalReason, "no_activity_match_failed", StringComparison.Ordinal))
            return "no_activity";
        if (string.Equals(internalReason, "backoff_elapsed", StringComparison.Ordinal))
            return "backoff";
        return "forced_eval";
    }

    private GateReconciliationResult? RunInstrumentGateReconciliation(string instrument, DateTimeOffset utcNow,
        int gateCycleOneBased)
    {
        if (_executionAdapter == null || string.IsNullOrWhiteSpace(instrument)) return null;
        var inst = instrument.Trim();
        var sw = Stopwatch.StartNew();
        var result = new GateReconciliationResult
        {
            Instrument = inst,
            Mode = ReconciliationRunMode.GateRecovery,
            RunnerInvoked = _reconciliationRunner != null
        };

        var execVariant = inst.StartsWith("M", StringComparison.OrdinalIgnoreCase) && inst.Length > 1 ? inst : "M" + inst;
        var canonicalInst = GetCanonicalInstrument(inst) ?? inst;
        var useIea = _executionPolicy?.UseInstrumentExecutionAuthority ?? false;
        var account = _accountName ?? "";

        AccountSnapshot? snapBefore;
        try
        {
            snapBefore = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.OutcomeStatus = ReconciliationOutcomeStatus.NoDataOptional;
            result.Reason = "snapshot_before_failed";
            return result;
        }

        result.BrokerWorkingCountBefore = CountBrokerWorkingOrders(snapBefore, inst);
        var posBefore = SumBrokerPositionQty(snapBefore, inst);
        var signedBefore = SumBrokerPositionSignedQty(snapBefore, inst);
        var (journalOpenBefore, _) = _executionJournal.GetOpenJournalStructuralStateForInstrument(inst, canonicalInst);
        InstrumentExecutionAuthority? ieaBeforeProbe = null;
        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, result.BrokerWorkingCountBefore, GetExecutionInstrument, out ieaBeforeProbe, out _) &&
            ieaBeforeProbe != null)
        {
            result.IeaOwnedCountBefore = ieaBeforeProbe.GetMismatchTrustedWorkingCount();
            result.AdoptionCandidateCountBefore = _executionJournal.CountReleaseBlockingAdoptionCandidates(
                ieaBeforeProbe.ExecutionInstrumentKey,
                canonicalInst,
                posBefore,
                signedBefore,
                CollectRobotTaggedIntentIdsForInstrument(snapBefore, inst),
                ieaBeforeProbe.GetMismatchTrustedWorkingIntentIds());
        }
        else
        {
            result.IeaOwnedCountBefore = useIea ? -1 : 0;
            result.AdoptionCandidateCountBefore = 0;
        }

        var ieaOwnedPlusAdoptedOnlyBefore = ieaBeforeProbe?.GetOwnedPlusAdoptedWorkingCount() ?? 0;
        var unexplainedStart = result.IeaOwnedCountBefore >= 0
            ? Math.Max(0, result.BrokerWorkingCountBefore - result.IeaOwnedCountBefore)
            : result.BrokerWorkingCountBefore;

        LogEvent(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_CYCLE_STATE", "ENGINE",
            new
            {
                phase = "start",
                instrument = inst,
                gate_cycle = gateCycleOneBased,
                broker_position_qty = posBefore,
                journal_open_qty = journalOpenBefore,
                broker_working_count = result.BrokerWorkingCountBefore,
                iea_owned_mismatch_trusted_working = result.IeaOwnedCountBefore,
                iea_owned_plus_adopted_working_only = ieaOwnedPlusAdoptedOnlyBefore,
                pending_adoption_candidate_count = result.AdoptionCandidateCountBefore,
                unexplained_working_count = unexplainedStart,
                release_ready = (bool?)null,
                delta_pending_adoption = (int?)null,
                delta_unexplained_working = (int?)null,
                delta_iea_owned = (int?)null,
                note = "Gate recovery cycle — snapshot before runner + adoption schedule"
            }));

        _reconciliationRunner?.ForceRunGateRecoveryForInstrument(utcNow, inst);

        var adoptionScheduled = false;
        var adoptedInline = 0;
        if (ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, result.BrokerWorkingCountBefore, GetExecutionInstrument, out var ieaRecover, out _) &&
            ieaRecover != null)
        {
            try
            {
                var so = ieaRecover.TryScheduleRecoveryAdoptionScan(out adoptedInline);
                adoptionScheduled = ReconciliationScheduleSignals.AdoptionWorkOrQueueInflight(so);
            }
            catch
            {
                // Adoption is best-effort during gate recovery
            }
        }

        AccountSnapshot? snapAfter;
        try
        {
            snapAfter = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            result.OutcomeStatus = ReconciliationOutcomeStatus.NoDataOptional;
            result.Reason = "snapshot_after_failed";
            return result;
        }

        result.BrokerWorkingCountAfter = CountBrokerWorkingOrders(snapAfter, inst);
        var posAfter = SumBrokerPositionQty(snapAfter, inst);
        InstrumentExecutionAuthority? ieaAfterProbe = null;
        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, result.BrokerWorkingCountAfter, GetExecutionInstrument, out var ieaAfterProbeOut, out _) &&
            ieaAfterProbeOut != null)
        {
            ieaAfterProbe = ieaAfterProbeOut;
            result.IeaOwnedCountAfter = ieaAfterProbe.GetMismatchTrustedWorkingCount();
            result.AdoptionCandidateCountAfter = _executionJournal.CountReleaseBlockingAdoptionCandidates(
                ieaAfterProbe.ExecutionInstrumentKey,
                canonicalInst,
                posAfter,
                SumBrokerPositionSignedQty(snapAfter, inst),
                CollectRobotTaggedIntentIdsForInstrument(snapAfter, inst),
                ieaAfterProbe.GetMismatchTrustedWorkingIntentIds());
        }
        else
        {
            result.IeaOwnedCountAfter = useIea ? -1 : 0;
            result.AdoptionCandidateCountAfter = 0;
        }

        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, result.BrokerWorkingCountAfter, GetExecutionInstrument, out var ieaStaleJournal, out _) &&
            ieaStaleJournal != null)
        {
            _executionJournal.ReconcileStaleAdoptionJournalCandidatesForRelease(
                ieaStaleJournal.ExecutionInstrumentKey,
                canonicalInst,
                posAfter,
                CollectRobotTaggedIntentIdsForInstrument(snapAfter, inst),
                utcNow);
        }
        else
        {
            _executionJournal.ReconcileStaleAdoptionJournalCandidatesForRelease(
                inst,
                canonicalInst,
                posAfter,
                CollectRobotTaggedIntentIdsForInstrument(snapAfter, inst),
                utcNow);
        }

        var gateOwnershipInput = new StateConsistencyReleaseEvaluationInput();
        ApplyReleaseOwnershipSnapshot(gateOwnershipInput,
            TryGetReleaseOwnershipSnapshot(ieaAfterProbe?.ExecutionInstrumentKey ?? inst));
        var gateOwnershipGrossOpen = HasReleaseOwnershipGrossOpen(gateOwnershipInput);

        if (result.BrokerWorkingCountAfter == 0 && !gateOwnershipGrossOpen)
        {
            var gateExecutionKey = ieaAfterProbe?.ExecutionInstrumentKey ?? inst;
            var (gateJournalOpenQty, _) =
                _executionJournal.GetOpenJournalStructuralStateForInstrument(gateExecutionKey, canonicalInst);
            LogBrokerFlatJournalRepairDeferredToAuthority(
                "GateRecoveryBrokerFlat",
                inst,
                gateExecutionKey,
                canonicalInst,
                posAfter,
                result.BrokerWorkingCountAfter,
                gateJournalOpenQty,
                pendingExecutionWorkload: 0,
                pendingFlattenLifecycle: HasPendingFlattenLifecycle(inst),
                brokerJournalAlignmentActive: QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow),
                ownershipGrossOpen: gateOwnershipGrossOpen,
                utcNow);
        }
        else if (result.BrokerWorkingCountAfter == 0 && gateOwnershipGrossOpen)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, "", "GATE_RECOVERY_BROKER_FLAT_JOURNAL_CLOSURE_SUPPRESSED", "ENGINE",
                new
                {
                    instrument = inst,
                    ownership_snapshot_available = gateOwnershipInput.OwnershipSnapshotAvailable,
                    ownership_gross_open_qty = gateOwnershipInput.OwnershipGrossOpenQty,
                    ownership_signed_net_qty = gateOwnershipInput.OwnershipSignedNetQty,
                    ownership_active_slot_count = gateOwnershipInput.OwnershipActiveSlotCount,
                    ownership_orphan_slot_count = gateOwnershipInput.OwnershipOrphanSlotCount,
                    note = "Broker-net flat is not clean-flat while gross ownership slots remain open; journal closure suppressed"
                }));
        }

        var readiness = EvaluateStateConsistencyReleaseReadiness(inst, snapAfter, utcNow, forceFullEvaluation: true);

        result.ReleaseReadyAfter = readiness.ReleaseReady;
        result.UnexplainedWorkingCountAfter = readiness.UnexplainedBrokerWorkingCount;
        result.UnexplainedPositionQtyAfter = readiness.UnexplainedBrokerPositionQty;

        result.OutcomeStatus = readiness.ReleaseReady
            ? ReconciliationOutcomeStatus.Success
            : (result.BrokerWorkingCountAfter != result.BrokerWorkingCountBefore ||
               result.IeaOwnedCountAfter != result.IeaOwnedCountBefore ||
               result.AdoptionCandidateCountAfter != result.AdoptionCandidateCountBefore
                ? ReconciliationOutcomeStatus.Partial
                : ReconciliationOutcomeStatus.Failed);

        result.Reason = readiness.Summary;
        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;

        var (journalOpenEnd, _) = _executionJournal.GetOpenJournalStructuralStateForInstrument(inst, canonicalInst);
        var ieaAfterForOwned = ieaAfterProbe;
        var ieaOwnedPlusAdoptedOnlyAfter = ieaAfterForOwned?.GetOwnedPlusAdoptedWorkingCount() ?? 0;

        var rootCauseClass = ClassifyConvergenceRootCause(readiness);

        LogEvent(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_CYCLE_STATE", "ENGINE",
            new
            {
                phase = "end",
                instrument = inst,
                gate_cycle = gateCycleOneBased,
                broker_position_qty = posAfter,
                journal_open_qty = journalOpenEnd,
                broker_working_count = result.BrokerWorkingCountAfter,
                iea_owned_mismatch_trusted_working = readiness.DiagnosticIeaOwnedPlusAdoptedWorking,
                iea_owned_plus_adopted_working_only = ieaOwnedPlusAdoptedOnlyAfter,
                pending_adoption_candidate_count = readiness.DiagnosticPendingAdoptionCandidateCount,
                unexplained_working_count = readiness.UnexplainedBrokerWorkingCount,
                release_ready = readiness.ReleaseReady,
                adoption_recovery_scheduled_or_active = adoptionScheduled,
                adoption_delta_if_inline = adoptedInline,
                outcome_status = result.OutcomeStatus.ToString(),
                root_cause_class = rootCauseClass,
                readiness_summary = readiness.Summary,
                contradictions = string.Join(";", readiness.Contradictions ?? new List<string>()),
                note = "single-pass gate alignment (no retry/progress chain)"
            }));

        EmitUnexplainedWorkingOrdersIfNeeded(utcNow, inst, gateCycleOneBased, snapAfter, ieaAfterForOwned);

        return result;
    }

    private static string ClassifyConvergenceRootCause(StateConsistencyReleaseReadinessResult r)
    {
        if (r == null) return "UNKNOWN";
        var cts = string.Join(";", r.Contradictions ?? new List<string>());
        if (cts.IndexOf("pending_adoption", StringComparison.OrdinalIgnoreCase) >= 0)
            return "ADOPTION_NOT_EXECUTED_OR_NOT_CLEARED";
        if (cts.IndexOf("unexplained_working", StringComparison.OrdinalIgnoreCase) >= 0)
            return "ORDER_NOT_RESOLVABLE_OR_REGISTRY_DRIFT";
        if (cts.IndexOf("iea_unavailable", StringComparison.OrdinalIgnoreCase) >= 0)
            return "STATE_EVALUATION_TOO_STRICT_OR_IEA_UNAVAILABLE";
        if (cts.IndexOf("position_qty", StringComparison.OrdinalIgnoreCase) >= 0)
            return "JOURNAL_BROKER_QTY_MISMATCH";
        if (cts.IndexOf("broker_working_without_iea", StringComparison.OrdinalIgnoreCase) >= 0)
            return "REGISTRY_MISSING_LINK_OR_IEA_COVERAGE";
        if (!string.IsNullOrEmpty(r.Summary) && string.Equals(r.Summary, "snapshot_insufficient", StringComparison.OrdinalIgnoreCase))
            return "SNAPSHOT_INSUFFICIENT";
        return r.ReleaseReady ? "RELEASED" : "UNCLASSIFIED_BLOCKERS";
    }

    private void EmitUnexplainedWorkingOrdersIfNeeded(DateTimeOffset utcNow, string instrument, int gateCycleOneBased,
        AccountSnapshot? snap, InstrumentExecutionAuthority? iea)
    {
        if (snap?.WorkingOrders == null || iea == null) return;
        var inst = instrument.Trim();
        foreach (var w in snap.WorkingOrders)
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (!string.Equals(w.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            if (iea.TryConvergenceAuditResolveWorkingOrder(w, out var path, out _))
                continue;

            LogEvent(RobotEvents.ExecutionBase(utcNow, "", inst, "UNEXPLAINED_WORKING_ORDER", new
            {
                gate_cycle = gateCycleOneBased,
                broker_order_id = w.OrderId,
                order_tag = w.Tag,
                oco = w.OcoGroup,
                instrument = w.Instrument,
                quantity = w.Quantity,
                order_type = w.OrderType,
                last_resolution_path_attempted = path,
                note = "All resolution paths failed: direct broker id, intent/alias decode, registry instrument scan"
            }));
        }
    }
}
