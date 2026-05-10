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

    private static int CountRobotTaggedBrokerWorkingForInstrument(AccountSnapshot snap, string inst)
    {
        if (snap?.WorkingOrders == null || string.IsNullOrWhiteSpace(inst)) return 0;
        var n = 0;
        foreach (var w in snap.WorkingOrders)
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(inst, w.Instrument.Trim())) continue;
            if (IsRobotTaggedWorkingOrderSnapshot(w)) n++;
        }
        return n;
    }

    private static bool IsRobotTaggedWorkingOrderSnapshot(WorkingOrderSnapshot w) =>
        (!string.IsNullOrEmpty(w.Tag) && w.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrEmpty(w.OcoGroup) && w.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gap 4: Assemble mismatch observations from broker snapshot and journal for mismatch coordinator (mismatch-sweep view).
    /// A1: Accepts snapshot as parameter — exactly one snapshot per coordinator tick.
    /// Not the same keying as <see cref="JournalParityChecker.CheckJournalParity"/>; see docs/robot/contracts/BROKER_QUANTITY_VIEWS.md.
    /// Hierarchy: broker snapshot = authority; journal = model; reconciliation = repair; gate = enforcement.
    /// Aggregate "clean" (qty + net + working) is uncommon when multiple intents share an instrument — do not treat as steady-state default.
    /// </summary>
    private IReadOnlyList<MismatchObservation> AssembleMismatchObservations(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        var list = new List<MismatchObservation>();
        if (snap == null) return list;
        if (TryRespectRunWideShutdownSignal(utcNow, "assemble_mismatch_observations"))
            return list;
        if (IsTerminalShutdownLatched()) return list;
        if (TryHandlePlaybackStallQuiescence(utcNow, requestStopIfEligible: false))
            return list;

        if (snap.Positions == null && snap.WorkingOrders == null)
            return list;

        var swAssemble = Stopwatch.StartNew();
        var rtAssemble = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
        if ((utcNow - _lastAssembleMismatchThreadAttrUtc).TotalSeconds >= 60)
        {
            _lastAssembleMismatchThreadAttrUtc = utcNow;
            var t = Thread.CurrentThread;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "IEA_EXPENSIVE_PATH_THREAD", state: "ENGINE",
                new
                {
                    path = "AssembleMismatchObservations",
                    thread_id = t.ManagedThreadId,
                    thread_name = t.Name,
                    on_iea_worker = false,
                    note = "Engine reconciliation path — not IEA worker thread"
                }));
        }

        var brokerQtyByInst = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var brokerNetByInst = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var brokerWorkingByInst = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in snap.Positions ?? new List<PositionSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            var inst = p.Instrument.Trim();
            var qty = Math.Abs(p.Quantity);
            brokerQtyByInst.TryGetValue(inst, out var existing);
            brokerQtyByInst[inst] = existing + qty;
            brokerNetByInst.TryGetValue(inst, out var existingNet);
            brokerNetByInst[inst] = existingNet + p.Quantity;
        }

        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            var inst = w.Instrument.Trim();
            brokerWorkingByInst.TryGetValue(inst, out var cnt);
            brokerWorkingByInst[inst] = cnt + 1;
        }

        var openByInst = _executionJournal.GetOpenJournalEntriesByInstrument();
        var allInstruments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in brokerQtyByInst.Keys) allInstruments.Add(k);
        foreach (var k in brokerWorkingByInst.Keys) allInstruments.Add(k);
        foreach (var k in openByInst.Keys)
        {
            if (!string.IsNullOrWhiteSpace(k)) allInstruments.Add(k.Trim());
        }

        var useIea = _executionPolicy?.UseInstrumentExecutionAuthority ?? false;
        var account = _accountName ?? "";
        var recoveryAdoptionInvocations = 0;

        foreach (var inst in allInstruments)
        {
            var brokerQty = brokerQtyByInst.TryGetValue(inst, out var bq) ? bq : 0;
            var netBrokerQty = brokerNetByInst.TryGetValue(inst, out var nbq) ? nbq : 0;
            var brokerWorking = brokerWorkingByInst.TryGetValue(inst, out var bw) ? bw : 0;
            var canonicalForJournalAgg = GetCanonicalInstrument(inst) ?? inst;
            var journalQty = _executionJournal.GetOpenJournalQuantitySumForInstrumentFromMap(openByInst, inst, canonicalForJournalAgg);
            var netJournalQty = _executionJournal.GetOpenJournalSignedNetForInstrumentFromMap(openByInst, inst, canonicalForJournalAgg);
            var (pendingGrossOv, pendingNetOv) = _pendingFillBridge.GetEffectiveOverlays(inst, canonicalForJournalAgg, journalQty,
                netJournalQty, brokerQty, netBrokerQty, utcNow);
            var effectiveJournalQty = journalQty + pendingGrossOv;
            var effectiveNetJournalQty = netJournalQty + pendingNetOv;
            if (TryApplyLedgerReconciliationAuthority(inst, brokerQty, netBrokerQty, effectiveJournalQty, effectiveNetJournalQty, utcNow,
                    out var ledgerAuthorityGrossQty, out var ledgerAuthorityNetQty))
            {
                effectiveJournalQty = ledgerAuthorityGrossQty;
                effectiveNetJournalQty = ledgerAuthorityNetQty;
            }
            var opposingMultiIntent = _executionJournal.HasOpposingDirectionOpenIntentsFromMap(openByInst, inst, canonicalForJournalAgg);
            var actGen = _releaseReconRedundancy.ExecutionActivityGeneration;

            var nonOwnerAssembleGate = useIea &&
                brokerQty != effectiveJournalQty &&
                ReconciliationStateTracker.Shared.TryPeekNonOwnerWithStableQtyMismatchEpisode(
                    account, inst, _reconciliationWriterInstanceId, brokerQty, effectiveJournalQty, out _) &&
                (!ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, brokerWorking, GetExecutionInstrument,
                    out var ieaNonOwnerPregate, out _) || ieaNonOwnerPregate == null);

            if (nonOwnerAssembleGate)
            {
                var resurfaceNonOwnerAssemble = false;
                if (_nonOwnerAssembleSuppressByInstrument.TryGetValue(inst, out var nos))
                {
                    var tupleMatch = nos.BrokerQty == brokerQty && nos.JournalQty == effectiveJournalQty && nos.BrokerWorking == brokerWorking &&
                        nos.ActivityGeneration == actGen;
                    if (!tupleMatch)
                    {
                        _nonOwnerAssembleSuppressByInstrument.Remove(inst);
                    }
                    else if ((utcNow - nos.AnchorUtc).TotalSeconds < NonOwnerAssembleSuppressResurfaceSeconds)
                    {
                        if (ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, brokerWorking, GetExecutionInstrument,
                                out var ieaNonOwnerRecheck, out _) && ieaNonOwnerRecheck != null)
                            _nonOwnerAssembleSuppressByInstrument.Remove(inst);
                        else
                            continue;
                    }
                    else
                    {
                        _nonOwnerAssembleSuppressByInstrument.Remove(inst);
                        resurfaceNonOwnerAssemble = true;
                    }
                }

                if (!resurfaceNonOwnerAssemble && !_nonOwnerAssembleSuppressByInstrument.ContainsKey(inst))
                {
                    if (ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, brokerWorking, GetExecutionInstrument,
                            out var ieaNonOwnerFirst, out _) && ieaNonOwnerFirst != null)
                    {
                    }
                    else
                    {
                        _nonOwnerAssembleSuppressByInstrument[inst] = new NonOwnerAssembleSuppressState
                        {
                            BrokerQty = brokerQty,
                            JournalQty = effectiveJournalQty,
                            BrokerWorking = brokerWorking,
                            ActivityGeneration = actGen,
                            AnchorUtc = utcNow
                        };
                        continue;
                    }
                }
            }
            else
                _nonOwnerAssembleSuppressByInstrument.Remove(inst);

            if (useIea &&
                _ieaUnavailableDegradedSuppressByInstrument.TryGetValue(inst, out var ieaDegSup) &&
                ieaDegSup.BrokerQty == brokerQty &&
                ieaDegSup.JournalQty == effectiveJournalQty &&
                ieaDegSup.BrokerWorking == brokerWorking &&
                ieaDegSup.ActivityGeneration == actGen &&
                (utcNow - ieaDegSup.AnchorUtc).TotalSeconds < IeaDegradedSuppressResurfaceSeconds)
            {
                if (ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, brokerWorking, GetExecutionInstrument,
                        out var ieaRecheck, out _) && ieaRecheck != null)
                {
                    _ieaUnavailableDegradedSuppressByInstrument.Remove(inst);
                }
                else
                    continue;
            }

            _executionAdapter?.PrepareOrderRegistryForMismatchAssembly(inst, snap, utcNow);

            // ORDER_REGISTRY_MISSING fix: local_working from IEA mismatch-trusted registry (OWNED+ADOPTED+RECOVERABLE_ROBOT_OWNED live), NOT journal.
            int localWorking;
            var journalWorking = ExecutionJournal.CountOpenJournalRowsMatchingInstrumentScope(openByInst, inst, canonicalForJournalAgg);

            InstrumentExecutionAuthority? ieaForInstrument = null;
            var ieaOwnershipAmbiguous = false;
            if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, brokerWorking, GetExecutionInstrument,
                    out ieaForInstrument, out ieaOwnershipAmbiguous) && ieaForInstrument != null)
            {
                _ieaUnavailableDegradedSuppressByInstrument.Remove(inst);
                localWorking = ieaForInstrument.GetMismatchTrustedWorkingCount();
                var ieaOwnedPlusAdoptedWorking = ieaForInstrument.GetOwnedPlusAdoptedWorkingCount();
                var pendingIeaWorkload = ieaForInstrument.PendingExecutionWorkloadCount;
                if (brokerWorking > 0 && ieaOwnedPlusAdoptedWorking == 0)
                {
                    var registryConvergenceActive =
                        pendingIeaWorkload > 0 ||
                        PendingAlignmentAuthority.IsPendingAlignment(inst, utcNow) ||
                        QuantExecutionControlStore.IsPostFillAlignmentWindowActive(inst, utcNow) ||
                        QuantExecutionControlStore.IsWorkingOrderSubmitWindowActive(inst, utcNow) ||
                        QuantExecutionControlStore.IsBrokerExecutionCallbackPendingActive(inst, utcNow) ||
                        QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow);
                    var shouldLogInvariant = true;
                    lock (_reconciliationBrokerWorkingOwnedInvariantThrottle)
                    {
                        if (_reconciliationBrokerWorkingOwnedInvariantThrottle.TryGetValue(inst, out var lastInv) &&
                            (utcNow - lastInv).TotalSeconds < ReconciliationBrokerWorkingOwnedInvariantThrottleSeconds)
                            shouldLogInvariant = false;
                        if (shouldLogInvariant)
                            _reconciliationBrokerWorkingOwnedInvariantThrottle[inst] = utcNow;
                        var cutoffInv = utcNow.AddSeconds(-ReconciliationBrokerWorkingOwnedInvariantThrottleSeconds);
                        foreach (var k in _reconciliationBrokerWorkingOwnedInvariantThrottle.Where(p => p.Value < cutoffInv).Select(p => p.Key).ToList())
                            _reconciliationBrokerWorkingOwnedInvariantThrottle.Remove(k);
                    }
                    if (shouldLogInvariant)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                            eventType: registryConvergenceActive
                                ? "RECONCILIATION_IEA_OWNED_WORKING_TRANSIENT"
                                : "RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH",
                            state: registryConvergenceActive ? "ENGINE" : "CRITICAL",
                            new
                            {
                                instrument = inst,
                                broker_working_count = brokerWorking,
                                iea_owned_plus_adopted_working = ieaOwnedPlusAdoptedWorking,
                                iea_mismatch_trusted_working = localWorking,
                                pending_execution_workload = pendingIeaWorkload,
                                convergence_active = registryConvergenceActive,
                                note = registryConvergenceActive
                                    ? "Broker reports working orders while IEA registry is settling inside a bounded order lifecycle convergence window."
                                    : "Broker reports working orders but IEA registry has no OWNED/ADOPTED live (SUBMITTED/WORKING/PART_FILLED) rows — check ownership/lifecycle/adoption"
                            }));
                    }
                }
                if (ieaOwnershipAmbiguous && brokerWorking > 0)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ORDER_REGISTRY_LOOKUP_IEA_MISMATCH", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            broker_working = brokerWorking,
                            iea_mismatch_trusted = localWorking,
                            note = "Multiple IEAs tie on distance to broker working; using engine execution hint when possible"
                        }));
                }
                if (brokerWorking != localWorking)
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_ORDER_SOURCE_BREAKDOWN", state: "ENGINE",
                        new { instrument = inst, broker_working = brokerWorking, iea_working = localWorking, journal_working = journalWorking }));
            }
            else if (useIea)
            {
                localWorking = -1;
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_IEA_UNAVAILABLE", state: "ENGINE",
                    new { instrument = inst, broker_working = brokerWorking, note = "IEA unavailable for instrument; failing closed (no journal fallback)" }));
                _ieaUnavailableDegradedSuppressByInstrument[inst] = new IeaUnavailableDegradedSuppressState
                {
                    BrokerQty = brokerQty,
                    JournalQty = effectiveJournalQty,
                    BrokerWorking = brokerWorking,
                    ActivityGeneration = actGen,
                    AnchorUtc = utcNow
                };
            }
            else
            {
                _ieaUnavailableDegradedSuppressByInstrument.Remove(inst);
                localWorking = brokerWorking > 0 ? -1 : 0;
                if (brokerWorking > 0)
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_IEA_DISABLED", state: "ENGINE",
                        new { instrument = inst, broker_working = brokerWorking, note = "UseInstrumentExecutionAuthority=false; cannot reconcile working orders; failing closed" }));
            }

            // Abs sums are not canonical for safety — use signed nets for net truth. See MismatchObservation.BrokerQty / NetBrokerQty.
            var effectiveLocalWorking = localWorking < 0 ? 0 : localWorking;

            if (MismatchClassification.IsExplainedHedgedNetFlatGrossOpen(
                    brokerQty,
                    effectiveJournalQty,
                    netBrokerQty,
                    effectiveNetJournalQty,
                    opposingMultiIntent,
                    brokerWorking,
                    effectiveLocalWorking))
            {
                _ieaUnavailableDegradedSuppressByInstrument.Remove(inst);
                continue;
            }

            var aggregatesAligned = brokerQty == effectiveJournalQty && netBrokerQty == effectiveNetJournalQty && brokerWorking == localWorking;
            if (aggregatesAligned)
            {
                _ieaUnavailableDegradedSuppressByInstrument.Remove(inst);
                continue;
            }

            // ORDER_REGISTRY_MISSING recovery: attempt adoption before fail-closed
            if (brokerWorking > 0 && effectiveLocalWorking == 0 && useIea && ieaForInstrument != null)
            {
                var throttleKey = $"{inst}_{brokerWorking}_{effectiveLocalWorking}";
                var shouldLogAttempt = true;
                lock (_recoveryAttemptLogThrottle)
                {
                    if (_recoveryAttemptLogThrottle.TryGetValue(throttleKey, out var lastLog))
                    {
                        if ((utcNow - lastLog).TotalSeconds < RecoveryAttemptLogThrottleSeconds)
                            shouldLogAttempt = false;
                    }
                    if (shouldLogAttempt)
                        _recoveryAttemptLogThrottle[throttleKey] = utcNow;
                    // Prune entries older than throttle window
                    var cutoff = utcNow.AddSeconds(-RecoveryAttemptLogThrottleSeconds);
                    var toRemove = _recoveryAttemptLogThrottle.Where(k => k.Value < cutoff).Select(k => k.Key).ToList();
                    foreach (var k in toRemove)
                        _recoveryAttemptLogThrottle.Remove(k);
                }
                if (shouldLogAttempt)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT", state: "ENGINE",
                        new { instrument = inst, broker_working = brokerWorking, iea_working_before = effectiveLocalWorking }));
                }
                recoveryAdoptionInvocations++;
                var schedOut = ieaForInstrument.TryScheduleRecoveryAdoptionScan(out var adoptedSync);
                RunPostAdoptionJournalIntegrity(inst, snap, utcNow);
                if (ReconciliationScheduleSignals.AdoptionWorkOrQueueInflight(schedOut))
                {
                    _mismatchCoordinator?.ArmConvergence(inst, "recovery_adoption_scan", utcNow);
                    var localAfter = ieaForInstrument.GetMismatchTrustedWorkingCount();
                    if (adoptedSync > 0)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_RECOVERY_ADOPTION_SUCCESS", state: "ENGINE",
                            new { instrument = inst, adopted_count = adoptedSync, iea_working_after = localAfter }));
                        var delayMs = _engineStartUtc != DateTimeOffset.MinValue ? (long)(utcNow - _engineStartUtc).TotalMilliseconds : 0;
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "STARTUP_RECOVERY_ADOPTION_OCCURRED", state: "ENGINE",
                            new { instrument = inst, broker_working = brokerWorking, adopted_count = adoptedSync, delay_from_startup_ms = delayMs }));
                    }
                    if (localAfter == brokerWorking)
                        continue; // Recovery succeeded, no mismatch
                    if (adoptedSync > 0)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_RECOVERY_ADOPTION_PARTIAL", state: "ENGINE",
                            new { instrument = inst, adopted_count = adoptedSync, broker_working = brokerWorking, iea_working_after = localAfter, note = "Still mismatched after adoption" }));
                    }
                    continue; // Scan queued or in flight — skip mismatch this assembly pass
                }
                effectiveLocalWorking = ieaForInstrument.GetMismatchTrustedWorkingCount();
            }

            var mismatchType = localWorking < 0
                ? MismatchType.ORDER_REGISTRY_MISSING
                : MismatchClassification.Classify(
                    brokerQty,
                    effectiveJournalQty,
                    netBrokerQty,
                    effectiveNetJournalQty,
                    opposingMultiIntent,
                    brokerWorking,
                    effectiveLocalWorking);

            if (mismatchType == MismatchType.WORKING_ORDER_COUNT_CONVERGENCE &&
                effectiveLocalWorking > brokerWorking)
            {
                var pendingWorkingOrderCountConvergence =
                    QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow);
                if (pendingWorkingOrderCountConvergence)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "REGISTRY_PENDING_CONVERGENCE", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            mismatch_type = mismatchType.ToString(),
                            broker_working = brokerWorking,
                            iea_working = effectiveLocalWorking,
                            note = "WORKING_ORDER_COUNT_CONVERGENCE observed inside bounded order lifecycle convergence window"
                        }));
                    continue;
                }
            }

            if (mismatchType == MismatchType.ORDER_REGISTRY_MISSING && brokerWorking > 0 && effectiveLocalWorking == 0)
            {
                var pendingRegistryConvergence =
                    QuantExecutionControlStore.IsAnyBrokerJournalAlignmentWindowActive(inst, utcNow);
                if (pendingRegistryConvergence)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "REGISTRY_PENDING_CONVERGENCE", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            broker_working = brokerWorking,
                            iea_working = effectiveLocalWorking,
                            note = "ORDER_REGISTRY_MISSING observed inside bounded submit/fill convergence window"
                        }));
                    continue;
                }

                var robotTaggedWorking = CountRobotTaggedBrokerWorkingForInstrument(snap, inst);
                var deferFailClosed = ReconciliationDeferPolicy.ShouldDeferOrderRegistryMissingFailClosed(
                    ieaOwnershipAmbiguous, brokerWorking, robotTaggedWorking);
                if (deferFailClosed)
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ORDER_REGISTRY_RECOVERY_DEFERRED_FAIL_CLOSED", state: "ENGINE",
                        new
                        {
                            instrument = inst,
                            broker_working = brokerWorking,
                            robot_tagged_working = robotTaggedWorking,
                            iea_ambiguous = ieaOwnershipAmbiguous,
                            note = "Defer ORDER_REGISTRY_MISSING_FAIL_CLOSED pending recovery / ownership resolution"
                        }));
                    if (brokerQty == effectiveJournalQty)
                        continue;
                }
                else
                {
                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "ORDER_REGISTRY_MISSING_FAIL_CLOSED", state: "ENGINE",
                        new { instrument = inst, broker_working = brokerWorking, iea_working = effectiveLocalWorking, reason = "No adoptable evidence or adoption recovery failed" }));
                }
            }

            var sev = mismatchType switch
            {
                MismatchType.NET_POSITION_MISMATCH => "CRITICAL",
                MismatchType.UNCLASSIFIED_CRITICAL_MISMATCH => "CRITICAL",
                MismatchType.ORDER_REGISTRY_MISSING => "CRITICAL",
                _ => "WARN"
            };
            if (PendingAlignmentAuthority.IsPendingAlignment(inst, utcNow) &&
                PendingAlignmentAuthority.IsJournalLagExplainedMismatchType(mismatchType))
                continue;

            list.Add(new MismatchObservation
            {
                Instrument = inst,
                MismatchType = mismatchType,
                Present = true,
                Summary =
                    $"broker_qty_abs={brokerQty} gross_journal_qty={effectiveJournalQty} (disk_gross={journalQty} pending_gross_ov={pendingGrossOv}) net_broker={netBrokerQty} net_journal={effectiveNetJournalQty} (disk_net={netJournalQty} pending_net_ov={pendingNetOv}) structural_multi_intent={opposingMultiIntent} broker_working={brokerWorking} local_working={effectiveLocalWorking}",
                BrokerQty = brokerQty,
                LocalQty = effectiveJournalQty,
                NetBrokerQty = netBrokerQty,
                NetJournalQty = effectiveNetJournalQty,
                BrokerWorkingOrderCount = brokerWorking,
                LocalWorkingOrderCount = effectiveLocalWorking,
                JournalOpenEntryCount = journalWorking,
                IntentIdsCsv = BuildIntentIdsCsvFromOpenJournal(openByInst, inst, canonicalForJournalAgg),
                ObservedUtc = utcNow,
                Severity = sev
            });
            if (mismatchType == MismatchType.STRUCTURAL_MULTI_INTENT)
                ApplyStructuralMultiIntentPolicy(inst, utcNow);
        }

        swAssemble.Stop();
        if (rtAssemble != 0)
            _runtimeAudit?.CpuEnd(rtAssemble, RuntimeAuditSubsystem.AssembleMismatch);

        var instrumentsScanned = allInstruments.Count;
        var mismatchCount = list.Count;
        var quietAssembleDiag = mismatchCount == 0 && recoveryAdoptionInvocations == 0 && swAssemble.ElapsedMilliseconds < 50;
        var diagCooldownSeconds = quietAssembleDiag ? AssembleMismatchDiagQuietSeconds : AssembleMismatchDiagBusySeconds;
        var emitAssembleDiag = swAssemble.ElapsedMilliseconds >= 50
                               || recoveryAdoptionInvocations > 0
                               || mismatchCount > 0
                               || _lastAssembleMismatchDiagUtc == DateTimeOffset.MinValue
                               || (utcNow - _lastAssembleMismatchDiagUtc).TotalSeconds >= diagCooldownSeconds;
        if (emitAssembleDiag)
        {
            var tDiag = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;
            _lastAssembleMismatchDiagUtc = utcNow;
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "RECONCILIATION_ASSEMBLE_MISMATCH_DIAG", state: "ENGINE",
                new
                {
                    wall_ms = swAssemble.ElapsedMilliseconds,
                    instruments_scanned = instrumentsScanned,
                    recovery_adoption_invocations = recoveryAdoptionInvocations,
                    mismatch_observations_emitted = mismatchCount,
                    working_orders_in_snapshot = snap.WorkingOrders?.Count ?? 0,
                    positions_in_snapshot = snap.Positions?.Count ?? 0,
                    note = "Proof diag for AssembleMismatchObservations — recovery uses TryScheduleRecoveryAdoptionScan (worker-serialized adoption)"
                }));
            if (tDiag != 0)
                _runtimeAudit?.CpuEnd(tDiag, RuntimeAuditSubsystem.MismatchDiagnostics);
        }

        return list;
    }

    private bool TryApplyLedgerReconciliationAuthority(
        string instrument,
        int brokerGrossQty,
        int brokerNetQty,
        int legacyJournalGrossQty,
        int legacyJournalNetQty,
        DateTimeOffset utcNow,
        out int authoritativeGrossQty,
        out int authoritativeNetQty)
    {
        authoritativeGrossQty = legacyJournalGrossQty;
        authoritativeNetQty = legacyJournalNetQty;

        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return false;
        if (_ownershipLedger == null) return false;
        if (!FeatureFlags.CanonicalOwnershipLedgerEnabled || !FeatureFlags.StructuralLayerUseLedgerOwnership) return false;
        if (brokerGrossQty == legacyJournalGrossQty && brokerNetQty == legacyJournalNetQty) return false;

        InstrumentOwnershipSnapshot snapshot;
        try
        {
            snapshot = _ownershipLedger.GetOwnershipSnapshot(OwnershipAccountKey, inst);
        }
        catch
        {
            return false;
        }

        if (snapshot.OwnershipVersion <= 0) return false;
        if (snapshot.OrphanSlotCount > 0) return false;

        var openSlots = snapshot.Slots
            .Where(s => s.State != SlotState.Closed && s.Remaining > 0)
            .ToList();
        if (openSlots.Any(s => s.State != SlotState.Active)) return false;

        var ledgerGrossOpenQty = openSlots.Sum(s => Math.Abs(s.Remaining));
        if (ledgerGrossOpenQty != brokerGrossQty) return false;
        if (snapshot.LedgerSignedNetQty != brokerNetQty) return false;

        authoritativeGrossQty = brokerGrossQty;
        authoritativeNetQty = brokerNetQty;

        var shouldLog = false;
        lock (_ledgerReconciliationAuthorityLogThrottle)
        {
            if (!_ledgerReconciliationAuthorityLogThrottle.TryGetValue(inst, out var lastLog) ||
                (utcNow - lastLog).TotalSeconds >= LedgerReconciliationAuthorityLogQuietSeconds)
            {
                _ledgerReconciliationAuthorityLogThrottle[inst] = utcNow;
                shouldLog = true;
            }

            var cutoff = utcNow.AddSeconds(-LedgerReconciliationAuthorityLogQuietSeconds * 4);
            foreach (var key in _ledgerReconciliationAuthorityLogThrottle.Where(p => p.Value < cutoff).Select(p => p.Key).ToList())
                _ledgerReconciliationAuthorityLogThrottle.Remove(key);
        }

        if (shouldLog)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                eventType: "RECONCILIATION_LEDGER_AUTHORITY_APPLIED", state: "ENGINE",
                new
                {
                    instrument = inst,
                    broker_qty = brokerGrossQty,
                    broker_net_qty = brokerNetQty,
                    legacy_journal_qty = legacyJournalGrossQty,
                    legacy_net_journal_qty = legacyJournalNetQty,
                    ledger_gross_open_qty = ledgerGrossOpenQty,
                    ledger_signed_net_qty = snapshot.LedgerSignedNetQty,
                    ownership_version = snapshot.OwnershipVersion,
                    note = "Broker and canonical ownership ledger agree; legacy journal mismatch is treated as diagnostic lag for mismatch authority."
                }));
        }

        return true;
    }

    private static string BuildIntentIdsCsvFromOpenJournal(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInst,
        string inst,
        string? canonicalInstrument)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in openByInst)
        {
            if (!ExecutionJournal.OpenJournalMapBucketMatches(kvp.Key, inst, canonicalInstrument)) continue;
            foreach (var item in kvp.Value)
            {
                var iid = item.IntentId?.Trim();
                if (!string.IsNullOrEmpty(iid)) set.Add(iid);
            }
        }

        if (set.Count == 0) return "";
        var arr = set.ToArray();
        Array.Sort(arr, StringComparer.OrdinalIgnoreCase);
        return string.Join(",", arr);
    }

    private static int CountBrokerWorkingOrders(AccountSnapshot? snap, string instrument)
    {
        if (snap?.WorkingOrders == null || string.IsNullOrWhiteSpace(instrument)) return 0;
        var inst = instrument.Trim();
        return snap.WorkingOrders.Count(w => string.Equals(w.Instrument?.Trim(), inst, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Intent ids referenced by QTSW2-tagged working orders on this broker instrument (Tag or OcoGroup); used for
    /// release stale-journal detection (must match <see cref="ExecutionJournal.IsStaleAdoptionJournalEntryForRelease"/>).
    /// </summary>
    private static HashSet<string> CollectRobotTaggedIntentIdsForInstrument(AccountSnapshot? snap, string instrument)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (snap?.WorkingOrders == null || string.IsNullOrWhiteSpace(instrument)) return set;
        var inst = instrument.Trim();
        foreach (var w in snap.WorkingOrders)
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (!string.Equals(w.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var tag in new[] { w.Tag, w.OcoGroup })
            {
                if (string.IsNullOrEmpty(tag) || !tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) continue;
                var id = RobotOrderIds.DecodeIntentId(tag);
                if (!string.IsNullOrEmpty(id)) set.Add(id);
            }
        }
        return set;
    }

    private bool HasPendingFlattenLifecycle(string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument))
            return false;

        var instrumentKey = instrument.Trim();
        if (HasInterruptedSessionCloseStreamForInstrument(instrumentKey))
            return true;

        return FlattenCoordinationTracker.Shared.TryPeekKey(_accountName ?? "", instrumentKey, out _, out _, out var state) &&
               (state == FlattenCoordinationState.FLATTENING || state == FlattenCoordinationState.VERIFYING);
    }

    private static int SumBrokerPositionQty(AccountSnapshot? snap, string instrument)
    {
        if (snap?.Positions == null || string.IsNullOrWhiteSpace(instrument)) return 0;
        var inst = instrument.Trim();
        var sum = 0;
        foreach (var p in snap.Positions)
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            sum += Math.Abs(p.Quantity);
        }
        return sum;
    }

    /// <summary>Signed net position quantity for instrument (sum of snapshot position quantities matching instrument).</summary>
    private static int SumBrokerPositionSignedQty(AccountSnapshot? snap, string instrument)
    {
        if (snap?.Positions == null || string.IsNullOrWhiteSpace(instrument)) return 0;
        var inst = instrument.Trim();
        var sum = 0;
        foreach (var p in snap.Positions)
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            sum += p.Quantity;
        }
        return sum;
    }
}
