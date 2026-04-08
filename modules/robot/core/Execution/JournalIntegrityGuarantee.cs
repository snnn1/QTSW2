// Journal Integrity Guarantee Layer — authoritative parity check + deterministic repair hooks (invariant enforcement, not redesign).
// Parity semantics: see docs/robot/contracts/BROKER_QUANTITY_VIEWS.md (parity view). New integrity-parity checks should use JournalParityChecker only.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Canonical journal/broker parity status for <see cref="JournalParityChecker.CheckJournalParity"/>.</summary>
public enum JournalParityStatus
{
    PARITY_OK,
    POSITION_MISMATCH,
    WORKING_ORDER_MISMATCH,
    UNKNOWN_ORDER_PRESENT,
    INSUFFICIENT_DATA
}

/// <summary>Registry + IEA view for working-order explainability (single interface for authoritative check).</summary>
public interface IJournalParityRegistryView
{
    bool UseInstrumentExecutionAuthority { get; }
    /// <summary>Matches <see cref="StateConsistencyReleaseEvaluationInput.IeaOwnedPlusAdoptedWorking"/>; -1 when IEA required but unavailable.</summary>
    int IeaOwnedPlusAdoptedWorking { get; }
}

/// <summary>Outcome of <see cref="JournalParityChecker.CheckJournalParity"/> — single source of truth for parity classification.</summary>
public sealed class JournalParityResult
{
    public JournalParityStatus Status { get; init; }
    public int BrokerPositionQty { get; init; }
    public int JournalOpenQty { get; init; }
    public int UnexplainedPositionQty { get; init; }
    public int UnexplainedWorkingOrders { get; init; }
    public int OrphanOrdersDetected { get; init; }
    public long? SnapshotAgeMs { get; init; }
    public bool IeaUnavailableWhenRequired { get; init; }

    public bool IsOk => Status == JournalParityStatus.PARITY_OK;
}

public enum JournalReconstructionReasonCode
{
    None,
    TrimJournalExcessTowardBroker,
    CloseStaleAdoptionWhenBrokerFlat,
    NoOpInsufficientData,
    EscalationRequiredNoDeterministicIntent
}

/// <summary>Deterministic journal mutations from broker-observed state (uses existing <see cref="ExecutionJournal"/> APIs).</summary>
public readonly struct JournalReconstructionResult
{
    public int JournalWritesFromAlignment { get; init; }
    public int StaleAdoptionRowsClosed { get; init; }
    public JournalReconstructionReasonCode PrimaryReason { get; init; }
    public bool AnyMutation => JournalWritesFromAlignment > 0 || StaleAdoptionRowsClosed > 0;
}

public enum JournalIntegrityPhaseOutcome
{
    Ok,
    DeferredInsufficientData,
    Repaired,
    EscalatedUnrecoverable
}

/// <summary>Result of <see cref="JournalIntegrityGuarantee.EnsureJournalIntegrity"/>.</summary>
public sealed class JournalIntegrityEnsureResult
{
    public JournalParityResult InitialCheck { get; init; } = null!;
    public JournalParityResult? PostRepairCheck { get; init; }
    public JournalReconstructionResult? Reconstruction { get; init; }
    public JournalIntegrityPhaseOutcome Outcome { get; init; }
    public int AttemptNumber { get; init; }
    public bool EscalationEmitted { get; init; }
    /// <summary>Disk writes from <see cref="ExecutionJournal.UpsertRecoveredIntentForBrokerIntegrity"/> in this run.</summary>
    public int RecoveredIntentWrites { get; init; }
}

/// <summary>
/// Authoritative **parity view** (single instrument key + canonical journal mapping). Do not reimplement broker/journal/working
/// comparisons for integrity or fail-closed release input; use <see cref="CheckJournalParity"/>. Mismatch-sweep and family
/// reconciliation use different keying—see <c>docs/robot/contracts/BROKER_QUANTITY_VIEWS.md</c>.
/// </summary>
public static class JournalParityChecker
{
    /// <summary>Single authoritative parity check for the parity view. Classifies worst-first: insufficient data → unknown orders → position → working.</summary>
    public static JournalParityResult CheckJournalParity(
        string instrument,
        AccountSnapshot? snapshot,
        ExecutionJournal journal,
        IJournalParityRegistryView? registry,
        string? canonicalInstrument,
        DateTimeOffset utcNow,
        DateTimeOffset? snapshotTakenUtc = null)
    {
        var inst = instrument?.Trim() ?? "";
        var canonical = string.IsNullOrWhiteSpace(canonicalInstrument) ? inst : canonicalInstrument.Trim();
        long? ageMs = null;
        if (snapshotTakenUtc != null)
            ageMs = (long)Math.Max(0, (utcNow - snapshotTakenUtc.Value).TotalMilliseconds);

        if (snapshot == null || string.IsNullOrEmpty(inst))
        {
            return new JournalParityResult
            {
                Status = JournalParityStatus.INSUFFICIENT_DATA,
                SnapshotAgeMs = ageMs,
                IeaUnavailableWhenRequired = registry?.UseInstrumentExecutionAuthority == true &&
                                             ((registry.IeaOwnedPlusAdoptedWorking) < 0)
            };
        }

        var brokerPos = SumAbsBrokerPosition(snapshot, inst);
        var brokerWorking = CountInstrumentWorkingOrders(snapshot, inst);
        var orphans = CountNonRobotTaggedWorkingOrders(snapshot, inst);

        var (journalOpen, _) = journal.GetOpenJournalStructuralStateForInstrument(inst, canonical);

        var posDiff = Math.Abs(brokerPos - journalOpen);
        var useIea = registry?.UseInstrumentExecutionAuthority ?? false;
        var ieaOwnedPlus = registry?.IeaOwnedPlusAdoptedWorking ?? 0;
        var ieaOk = !useIea || ieaOwnedPlus >= 0;
        var ieaWorking = ieaOk ? Math.Max(0, ieaOwnedPlus) : 0;
        var unexplainedW = ieaOk ? Math.Max(0, brokerWorking - ieaWorking) : brokerWorking;
        var ieaUnavailable = useIea && ieaOwnedPlus < 0;

        if (ieaUnavailable)
        {
            return new JournalParityResult
            {
                Status = JournalParityStatus.INSUFFICIENT_DATA,
                BrokerPositionQty = brokerPos,
                JournalOpenQty = journalOpen,
                UnexplainedPositionQty = posDiff,
                UnexplainedWorkingOrders = unexplainedW,
                OrphanOrdersDetected = orphans,
                SnapshotAgeMs = ageMs,
                IeaUnavailableWhenRequired = true
            };
        }

        if (orphans > 0)
        {
            return new JournalParityResult
            {
                Status = JournalParityStatus.UNKNOWN_ORDER_PRESENT,
                BrokerPositionQty = brokerPos,
                JournalOpenQty = journalOpen,
                UnexplainedPositionQty = posDiff,
                UnexplainedWorkingOrders = unexplainedW,
                OrphanOrdersDetected = orphans,
                SnapshotAgeMs = ageMs
            };
        }

        if (posDiff > 0)
        {
            return new JournalParityResult
            {
                Status = JournalParityStatus.POSITION_MISMATCH,
                BrokerPositionQty = brokerPos,
                JournalOpenQty = journalOpen,
                UnexplainedPositionQty = posDiff,
                UnexplainedWorkingOrders = unexplainedW,
                OrphanOrdersDetected = 0,
                SnapshotAgeMs = ageMs
            };
        }

        if (unexplainedW > 0)
        {
            return new JournalParityResult
            {
                Status = JournalParityStatus.WORKING_ORDER_MISMATCH,
                BrokerPositionQty = brokerPos,
                JournalOpenQty = journalOpen,
                UnexplainedPositionQty = 0,
                UnexplainedWorkingOrders = unexplainedW,
                OrphanOrdersDetected = 0,
                SnapshotAgeMs = ageMs
            };
        }

        return new JournalParityResult
        {
            Status = JournalParityStatus.PARITY_OK,
            BrokerPositionQty = brokerPos,
            JournalOpenQty = journalOpen,
            UnexplainedPositionQty = 0,
            UnexplainedWorkingOrders = 0,
            OrphanOrdersDetected = 0,
            SnapshotAgeMs = ageMs
        };
    }

    private static int SumAbsBrokerPosition(AccountSnapshot snap, string inst)
    {
        var sum = 0;
        foreach (var p in snap.Positions ?? new List<PositionSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(p.Instrument)) continue;
            if (!string.Equals(p.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            sum += Math.Abs(p.Quantity);
        }

        return sum;
    }

    private static int CountInstrumentWorkingOrders(AccountSnapshot snap, string inst)
    {
        var n = 0;
        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (!string.Equals(w.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            n++;
        }

        return n;
    }

    /// <summary>Working orders on instrument without QTSW2 tag/OCO group (external / orphan for robot policy).</summary>
    private static int CountNonRobotTaggedWorkingOrders(AccountSnapshot snap, string inst)
    {
        var n = 0;
        foreach (var w in snap.WorkingOrders ?? new List<WorkingOrderSnapshot>())
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (!string.Equals(w.Instrument.Trim(), inst, StringComparison.OrdinalIgnoreCase)) continue;
            var tag = w.Tag ?? "";
            var oco = w.OcoGroup ?? "";
            var robot = tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase) ||
                        oco.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase);
            if (!robot) n++;
        }

        return n;
    }
}

/// <summary>Deterministic broker-anchored journal repair using existing journal reconciliation (no silent divergence — all mutations logged inside journal).</summary>
public static class JournalReconstruction
{
    /// <summary>
    /// Rebuild toward broker truth using existing APIs: trim excess journal qty; close stale adoption rows when flat.
    /// Does not invent intent rows without engine-provided context (that path requires adoption / tagged recovery).
    /// </summary>
    public static JournalReconstructionResult ReconstructJournalFromBroker(
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        AccountSnapshot snapshot,
        ExecutionJournal journal,
        IReadOnlyCollection<string> robotTaggedIntentIdsOnInstrument,
        IReadOnlyCollection<string>? registryMismatchTrustedIntentIds,
        int brokerPositionQtyAbs,
        DateTimeOffset utcNow,
        InstrumentExecutionAuthority? ieaForStalePath)
    {
        if (snapshot == null)
            return new JournalReconstructionResult { PrimaryReason = JournalReconstructionReasonCode.NoOpInsufficientData };

        var canonical = string.IsNullOrWhiteSpace(canonicalInstrument)
            ? executionInstrumentPrimary
            : canonicalInstrument.Trim();

        var alignmentWrites = journal.ReconcileJournalOpenQuantityWithBroker(
            executionInstrumentPrimary,
            canonical,
            brokerPositionQtyAbs,
            robotTaggedIntentIdsOnInstrument,
            registryMismatchTrustedIntentIds,
            utcNow);

        var staleClosed = journal.ReconcileStaleAdoptionJournalCandidatesForRelease(
            ieaForStalePath?.ExecutionInstrumentKey ?? executionInstrumentPrimary,
            canonical,
            brokerPositionQtyAbs,
            robotTaggedIntentIdsOnInstrument,
            utcNow);

        var reason = alignmentWrites > 0
            ? JournalReconstructionReasonCode.TrimJournalExcessTowardBroker
            : staleClosed > 0
                ? JournalReconstructionReasonCode.CloseStaleAdoptionWhenBrokerFlat
                : JournalReconstructionReasonCode.None;

        return new JournalReconstructionResult
        {
            JournalWritesFromAlignment = alignmentWrites,
            StaleAdoptionRowsClosed = staleClosed,
            PrimaryReason = reason
        };
    }
}

/// <summary>Orchestrates parity check, deterministic repair, bounded escalation.</summary>
public static class JournalIntegrityGuarantee
{
    public const int MaxIntegrityRepairAttemptsBeforeEscalation = 3;

    private static readonly ConcurrentDictionary<string, int> AttemptByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ensure journal integrity: check → repair (when safe) → re-check → escalate fail-closed if still broken.
    /// </summary>
    public static JournalIntegrityEnsureResult EnsureJournalIntegrity(
        string instrument,
        AccountSnapshot? snapshot,
        ExecutionJournal journal,
        IJournalParityRegistryView? registry,
        string executionInstrumentPrimary,
        string? canonicalInstrument,
        IReadOnlyCollection<string> robotTaggedIntentIds,
        IReadOnlyCollection<string>? registryIntentIds,
        InstrumentExecutionAuthority? ieaForStale,
        DateTimeOffset utcNow,
        Action<string, string, object?>? logEngine,
        bool allowReconstruction = true,
        string? tradingDateForJournal = null)
    {
        var inst = instrument.Trim();
        var initial = JournalParityChecker.CheckJournalParity(inst, snapshot, journal, registry, canonicalInstrument, utcNow);
        logEngine?.Invoke("JOURNAL_PARITY_CHECK", "ENGINE", new
        {
            instrument = inst,
            status = initial.Status.ToString(),
            broker_qty = initial.BrokerPositionQty,
            journal_qty = initial.JournalOpenQty,
            unexplained_position = initial.UnexplainedPositionQty,
            unexplained_working = initial.UnexplainedWorkingOrders,
            orphans = initial.OrphanOrdersDetected,
            snapshot_age_ms = initial.SnapshotAgeMs
        });

        if (initial.IsOk)
        {
            AttemptByInstrument.TryRemove(inst, out _);
            return new JournalIntegrityEnsureResult
            {
                InitialCheck = initial,
                Outcome = JournalIntegrityPhaseOutcome.Ok
            };
        }

        if (initial.Status == JournalParityStatus.INSUFFICIENT_DATA)
        {
            logEngine?.Invoke("JOURNAL_INTEGRITY_DEFERRED", "ENGINE", new
            {
                instrument = inst,
                reason = "insufficient_data",
                iea_unavailable = initial.IeaUnavailableWhenRequired
            });
            return new JournalIntegrityEnsureResult
            {
                InitialCheck = initial,
                Outcome = JournalIntegrityPhaseOutcome.DeferredInsufficientData
            };
        }

        if (initial.Status == JournalParityStatus.UNKNOWN_ORDER_PRESENT)
        {
            var n = AttemptByInstrument.AddOrUpdate(inst, 1, (_, c) => c + 1);
            EmitEscalationIfNeeded(inst, n, initial, null, logEngine, "orphan_orders");
            return new JournalIntegrityEnsureResult
            {
                InitialCheck = initial,
                Outcome = n >= MaxIntegrityRepairAttemptsBeforeEscalation
                    ? JournalIntegrityPhaseOutcome.EscalatedUnrecoverable
                    : JournalIntegrityPhaseOutcome.DeferredInsufficientData,
                AttemptNumber = n,
                EscalationEmitted = n >= MaxIntegrityRepairAttemptsBeforeEscalation
            };
        }

        JournalReconstructionResult? recon = null;
        if (allowReconstruction && snapshot != null &&
            (initial.Status == JournalParityStatus.POSITION_MISMATCH ||
             initial.Status == JournalParityStatus.WORKING_ORDER_MISMATCH))
        {
            recon = JournalReconstruction.ReconstructJournalFromBroker(
                executionInstrumentPrimary,
                canonicalInstrument,
                snapshot,
                journal,
                robotTaggedIntentIds,
                registryIntentIds,
                initial.BrokerPositionQty,
                utcNow,
                ieaForStale);

            logEngine?.Invoke("JOURNAL_RECONSTRUCTION", "ENGINE", new
            {
                instrument = inst,
                alignment_writes = recon.Value.JournalWritesFromAlignment,
                stale_adoption_closed = recon.Value.StaleAdoptionRowsClosed,
                reason = recon.Value.PrimaryReason.ToString(),
                note = "deterministic_execution_journal_repair"
            });
        }

        var mid = snapshot != null
            ? JournalParityChecker.CheckJournalParity(inst, snapshot, journal, registry, canonicalInstrument, utcNow)
            : initial;

        if (mid.IsOk)
        {
            AttemptByInstrument.TryRemove(inst, out _);
            if (recon?.AnyMutation == true)
            {
                logEngine?.Invoke("JOURNAL_INTEGRITY_RECOVERED", "ENGINE", new
                {
                    instrument = inst,
                    before_status = initial.Status.ToString(),
                    after_status = mid.Status.ToString(),
                    broker_qty = mid.BrokerPositionQty,
                    journal_qty = mid.JournalOpenQty,
                    note = "alignment_or_stale_repair_only"
                });
            }

            return new JournalIntegrityEnsureResult
            {
                InitialCheck = initial,
                PostRepairCheck = mid,
                Reconstruction = recon,
                Outcome = JournalIntegrityPhaseOutcome.Repaired,
                RecoveredIntentWrites = 0
            };
        }

        var journalTradingDate = string.IsNullOrWhiteSpace(tradingDateForJournal)
            ? utcNow.ToString("yyyy-MM-dd")
            : tradingDateForJournal.Trim();

        RecoveredIntentUpsertResult recov = default;
        if (allowReconstruction && snapshot != null &&
            mid.Status == JournalParityStatus.POSITION_MISMATCH &&
            mid.BrokerPositionQty > 0 &&
            mid.OrphanOrdersDetected == 0)
        {
            recov = journal.UpsertRecoveredIntentForBrokerIntegrity(
                executionInstrumentPrimary,
                canonicalInstrument,
                snapshot,
                journalTradingDate,
                utcNow);

            if (recov.Conflict)
            {
                logEngine?.Invoke("RECONCILIATION_RECOVERED_INTENT_FAILED", "CRITICAL", new
                {
                    instrument = inst,
                    reason = recov.ConflictReason ?? "unknown",
                    broker_qty = mid.BrokerPositionQty,
                    journal_qty = mid.JournalOpenQty
                });
                return new JournalIntegrityEnsureResult
                {
                    InitialCheck = initial,
                    PostRepairCheck = mid,
                    Reconstruction = recon,
                    Outcome = JournalIntegrityPhaseOutcome.EscalatedUnrecoverable,
                    EscalationEmitted = true,
                    RecoveredIntentWrites = 0
                };
            }
        }

        var post = snapshot != null
            ? JournalParityChecker.CheckJournalParity(inst, snapshot, journal, registry, canonicalInstrument, utcNow)
            : mid;

        if ((recon?.AnyMutation == true || recov.Writes > 0) && post.IsOk)
        {
            AttemptByInstrument.TryRemove(inst, out _);
            logEngine?.Invoke("JOURNAL_INTEGRITY_RECOVERED", "ENGINE", new
            {
                instrument = inst,
                before_status = initial.Status.ToString(),
                after_status = post.Status.ToString(),
                broker_qty = post.BrokerPositionQty,
                journal_qty = post.JournalOpenQty,
                recovered_writes = recov.Writes
            });
            return new JournalIntegrityEnsureResult
            {
                InitialCheck = initial,
                PostRepairCheck = post,
                Reconstruction = recon,
                Outcome = JournalIntegrityPhaseOutcome.Repaired,
                RecoveredIntentWrites = recov.Writes
            };
        }

        var attempt = AttemptByInstrument.AddOrUpdate(inst, 1, (_, c) => c + 1);
        var escalated = EmitEscalationIfNeeded(inst, attempt, initial, post, logEngine, "parity_not_restored_after_repair");

        if (!post.IsOk)
        {
            logEngine?.Invoke("JOURNAL_INTEGRITY_FAILED", "WARN", new
            {
                instrument = inst,
                status = post.Status.ToString(),
                attempts = attempt,
                note = "unrecoverable_mismatch_within_integrity_layer"
            });
        }

        return new JournalIntegrityEnsureResult
        {
            InitialCheck = initial,
            PostRepairCheck = post,
            Reconstruction = recon,
            Outcome = escalated ? JournalIntegrityPhaseOutcome.EscalatedUnrecoverable : JournalIntegrityPhaseOutcome.DeferredInsufficientData,
            AttemptNumber = attempt,
            EscalationEmitted = escalated,
            RecoveredIntentWrites = recov.Writes
        };
    }

    private static bool EmitEscalationIfNeeded(string inst, int attempt, JournalParityResult initial, JournalParityResult? post,
        Action<string, string, object?>? logEngine, string note)
    {
        if (attempt < MaxIntegrityRepairAttemptsBeforeEscalation) return false;
        logEngine?.Invoke("RECONCILIATION_JOURNAL_INTEGRITY_FAILED", "CRITICAL", new
        {
            instrument = inst,
            attempts = attempt,
            initial_status = initial.Status.ToString(),
            post_status = post?.Status.ToString() ?? "n/a",
            note
        });
        return true;
    }

    public static void ResetAttemptsForTests() => AttemptByInstrument.Clear();
}
