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

    private void ClearJournalIntegrityInvariantDebounce(string instrument)
    {
        if (!string.IsNullOrWhiteSpace(instrument))
            _journalIntegrityInvariantDebounceByInstrument.TryRemove(instrument.Trim(), out _);
    }

    private void LogJournalIntegrityInvariantOrTransient(string instrument, string status, int brokerQty, int journalQty,
        DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(inst)) return;

        var state = _journalIntegrityInvariantDebounceByInstrument.AddOrUpdate(inst,
            _ => new JournalIntegrityInvariantDebounceState(status, brokerQty, journalQty, utcNow, utcNow, 1, false),
            (_, existing) =>
            {
                if (!string.Equals(existing.Status, status, StringComparison.OrdinalIgnoreCase) ||
                    existing.BrokerQty != brokerQty ||
                    existing.JournalQty != journalQty)
                {
                    return new JournalIntegrityInvariantDebounceState(status, brokerQty, journalQty, utcNow, utcNow, 1, false);
                }

                return existing with
                {
                    LastSeenUtc = utcNow,
                    SeenCount = existing.SeenCount + 1
                };
            });

        var elapsedMs = Math.Max(0.0, (utcNow - state.FirstSeenUtc).TotalMilliseconds);
        if (elapsedMs < JournalIntegrityInvariantCriticalAfterMilliseconds)
        {
            if (state.SeenCount == 1)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "JOURNAL_INTEGRITY_TRANSIENT_ALIGNMENT", "ENGINE",
                    new
                    {
                        instrument = inst,
                        status,
                        broker_qty = brokerQty,
                        journal_qty = journalQty,
                        first_seen_utc = state.FirstSeenUtc,
                        elapsed_ms = (long)elapsedMs,
                        critical_after_ms = (long)JournalIntegrityInvariantCriticalAfterMilliseconds,
                        note = "Parity is not OK after integrity evaluation, but the mismatch has not persisted long enough to classify as invariant break."
                    }));
            }
            return;
        }

        if (state.CriticalEmitted) return;

        LogEvent(RobotEvents.EngineBase(utcNow, "", "JOURNAL_INTEGRITY_INVARIANT_CYCLE", "CRITICAL",
            new
            {
                instrument = inst,
                status,
                broker_qty = brokerQty,
                journal_qty = journalQty,
                first_seen_utc = state.FirstSeenUtc,
                elapsed_ms = (long)elapsedMs,
                seen_count = state.SeenCount,
                note = "CheckJournalParity != PARITY_OK after integrity pipeline and release evaluation beyond transient alignment window"
            }));

        _journalIntegrityInvariantDebounceByInstrument[inst] = state with { CriticalEmitted = true };
    }

    /// <summary>Authoritative journal integrity pipeline (parity check + deterministic repair). Used by release input and post-adoption hooks.</summary>
    /// <param name="readOnlyParityWhenPendingAlignment">
    /// When true and <see cref="PendingAlignmentAuthority.IsPendingAlignment"/>, skips <see cref="JournalIntegrityGuarantee.EnsureJournalIntegrity"/>
    /// (no journal mutations / escalation) and returns a parity-only result — used by release readiness input to avoid amplifying lag.
    /// </param>
    private JournalIntegrityEnsureResult RunEnsureJournalIntegrity(string inst, AccountSnapshot snap, DateTimeOffset utcNow,
        bool markEnsuredForInvariant, bool readOnlyParityWhenPendingAlignment = false)
    {
        var useIea = _executionPolicy?.UseInstrumentExecutionAuthority ?? false;
        var account = _accountName ?? "";
        var bw = CountBrokerWorkingOrders(snap, inst);
        InstrumentExecutionAuthority? ieaResolved = null;
        HashSet<string>? registryIntentIds = null;
        var ieaOwned = 0;
        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, inst, bw, GetExecutionInstrument, out ieaResolved, out _) &&
            ieaResolved != null)
        {
            ieaOwned = ieaResolved.GetMismatchTrustedWorkingCount();
            registryIntentIds = ieaResolved.GetMismatchTrustedWorkingIntentIds();
        }
        else
            ieaOwned = useIea ? -1 : 0;

        var canonical = GetCanonicalInstrument(inst) ?? inst;
        var robotIntentIds = CollectRobotTaggedIntentIdsForInstrument(snap, inst);
        var parityRegistryView = new JournalParityRegistryViewImpl
        {
            UseInstrumentExecutionAuthority = useIea,
            IeaOwnedPlusAdoptedWorking = ieaOwned
        };

        if (readOnlyParityWhenPendingAlignment && PendingAlignmentAuthority.IsPendingAlignment(inst, utcNow))
        {
            var initialOnly = JournalParityChecker.CheckJournalParity(inst, snap, _executionJournal, parityRegistryView, canonical, utcNow);
            if (markEnsuredForInvariant)
                _journalIntegrityEnsuredForInstrument[inst] = 1;
            return new JournalIntegrityEnsureResult
            {
                InitialCheck = initialOnly,
                Outcome = JournalIntegrityPhaseOutcome.Ok,
                RecoveredIntentWrites = 0
            };
        }

        var integrityResult = JournalIntegrityGuarantee.EnsureJournalIntegrity(
            inst,
            snap,
            _executionJournal,
            parityRegistryView,
            ieaResolved?.ExecutionInstrumentKey ?? inst,
            canonical,
            robotIntentIds,
            registryIntentIds,
            ieaResolved,
            utcNow,
            (evt, st, extra) => LogEvent(RobotEvents.EngineBase(utcNow, "", evt, st, extra)),
            allowReconstruction: true,
            tradingDateForJournal: string.IsNullOrEmpty(TradingDateString) ? utcNow.ToString("yyyy-MM-dd") : TradingDateString);
        if (markEnsuredForInvariant)
            _journalIntegrityEnsuredForInstrument[inst] = 1;
        return integrityResult;
    }

    /// <summary>Runs after recovery adoption scheduling attempt — integrity layer owns parity, not adoption.</summary>
    private void RunPostAdoptionJournalIntegrity(string inst, AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (snap == null) return;
        RunEnsureJournalIntegrity(inst, snap, utcNow, markEnsuredForInvariant: true);
    }

    /// <summary>
    /// Hard fail-closed broker flatten only for material parity breaks (position divergence, non-robot-tagged working orders).
    /// <see cref="JournalParityStatus.WORKING_ORDER_MISMATCH"/> is left to mismatch escalation / state-consistency gate.
    /// </summary>
    private static bool JournalParityStatusWarrantsHardFailClosedFlatten(JournalParityStatus status) =>
        status is JournalParityStatus.POSITION_MISMATCH or JournalParityStatus.UNKNOWN_ORDER_PRESENT;

    /// <summary>Fast path: only runs full ensure when parity is already broken (avoids log noise on hot execution paths).</summary>
    private void TryEnsureJournalIntegrityAfterExecutionActivity(string inst, DateTimeOffset utcNow,
        MismatchExecutionTriggerDetails triggerDetails = default)
    {
        if (_executionAdapter == null || string.IsNullOrWhiteSpace(inst)) return;
        AccountSnapshot? snap;
        try
        {
            snap = _executionAdapter.GetAccountSnapshot(utcNow);
        }
        catch
        {
            return;
        }

        if (snap == null) return;
        var trimmed = inst.Trim();
        if (FeatureFlags.QuantExecutionControlStoreEnabled && !triggerDetails.WorkingOrderSubmitTransition)
        {
            try
            {
                var brokerGrossQ = SumBrokerPositionQty(snap, trimmed);
                var brokerSignedQ = SumBrokerPositionSignedQty(snap, trimmed);
                var qTier1 = QuantExecutionControlStore.EvaluateEscalationAndApplyIfRequired(trimmed, utcNow, brokerGrossQ, brokerSignedQ);
                if (qTier1.Kind == QuantEscalationKind.EscalationRequired)
                {
                    try
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, "", "QUANT_TIER1_RECOVERY_REQUIRED", "ENGINE",
                            new { instrument = trimmed, reason = qTier1.Reason ?? "" }));
                    }
                    catch
                    {
                        /* diagnostics only */
                    }
                }
            }
            catch
            {
                /* never block journal integrity on quant tier-1 */
            }
        }

        var useIea = _executionPolicy?.UseInstrumentExecutionAuthority ?? false;
        var account = _accountName ?? "";
        var bw = CountBrokerWorkingOrders(snap, trimmed);
        InstrumentExecutionAuthority? iea = null;
        var ieaOwned = 0;
        if (useIea && ReconciliationIeaLookup.TryResolveForMismatchAssembly(account, trimmed, bw, GetExecutionInstrument, out iea, out _) &&
            iea != null)
            ieaOwned = iea.GetMismatchTrustedWorkingCount();
        else
            ieaOwned = useIea ? -1 : 0;
        var canonical = GetCanonicalInstrument(trimmed) ?? trimmed;
        var pre = JournalParityChecker.CheckJournalParity(trimmed, snap, _executionJournal,
            new JournalParityRegistryViewImpl { UseInstrumentExecutionAuthority = useIea, IeaOwnedPlusAdoptedWorking = ieaOwned },
            canonical, utcNow);
        var wasPendingAlignment = _pendingAlignmentActiveByInstrument.TryGetValue(trimmed, out var prevPend) && prevPend;
        var nowPendingAlignment = pre.IsPendingAlignment;
        if (!wasPendingAlignment && nowPendingAlignment)
        {
            try
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "PENDING_ALIGNMENT_STATE", "ENGINE", new
                {
                    pending_alignment_active = true,
                    pending_alignment_cause = pre.PendingAlignmentCause ?? "",
                    broker_qty = pre.BrokerPositionQty,
                    journal_qty = pre.JournalOpenQty,
                    expected_fill_delta = pre.ExpectedFillDeltaAbs,
                    escalation_suppressed = pre.EscalationSuppressed
                }));
            }
            catch
            {
                /* logging best-effort */
            }
        }
        else if (wasPendingAlignment && pre.IsOk)
        {
            try
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "PENDING_ALIGNMENT_STATE", "ENGINE", new
                {
                    pending_alignment_active = false,
                    pending_alignment_cause = "",
                    broker_qty = pre.BrokerPositionQty,
                    journal_qty = pre.JournalOpenQty,
                    expected_fill_delta = 0,
                    escalation_released = true,
                    escalation_suppressed = false
                }));
            }
            catch
            {
                /* logging best-effort */
            }
        }
        else if (wasPendingAlignment && pre.Status == JournalParityStatus.POSITION_MISMATCH)
        {
            try
            {
                LogEvent(RobotEvents.EngineBase(utcNow, "", "PENDING_ALIGNMENT_STATE", "ENGINE", new
                {
                    pending_alignment_active = false,
                    pending_alignment_cause = "",
                    broker_qty = pre.BrokerPositionQty,
                    journal_qty = pre.JournalOpenQty,
                    expected_fill_delta = pre.ExpectedFillDeltaAbs,
                    escalation_released = false,
                    escalation_suppressed = false
                }));
            }
            catch
            {
                /* logging best-effort */
            }
        }

        if (nowPendingAlignment)
            _pendingAlignmentActiveByInstrument[trimmed] = true;
        else
            _pendingAlignmentActiveByInstrument.TryRemove(trimmed, out _);

        if (pre.IsOkOrPendingAlignment) return;
        if (triggerDetails.SuppressHardJournalIntegrityActions)
            return;
        // Phase A: parity/journal pre-check must not be a control-plane lever for automatic hard flatten (diagnostics only here).
        if (FeatureFlags.ControlPlaneParityHardFlattenFromTryEnsureJournalIntegrity &&
            FeatureFlags.EnableHardFailClosedBrokerFlatten &&
            JournalParityStatusWarrantsHardFailClosedFlatten(pre.Status))
        {
            var suppressHardFlattenForJournalLag =
                pre.Status == JournalParityStatus.POSITION_MISMATCH &&
                PendingAlignmentAuthority.IsPendingAlignment(trimmed, utcNow);
            if (!suppressHardFlattenForJournalLag)
            {
                try
                {
                    _executionAdapter.TryTriggerHardFlatten(trimmed, "parity_mismatch:" + pre.Status, utcNow);
                }
                catch
                {
                    /* fail-closed: broker flatten is best-effort */
                }
            }
        }

        RunEnsureJournalIntegrity(trimmed, snap, utcNow, markEnsuredForInvariant: true);
    }

}
