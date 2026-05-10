using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Receives <see cref="ReconciliationVerdict"/> records and performs auditable state mutations.
/// The ONLY reconciliation component that reads <see cref="ExecutionJournal"/> rows —
/// it needs them to decide which specific journal rows to close, repair, or mark.
/// Rejects STALE verdicts — they must be re-classified first.
/// </summary>
public sealed class ReconciliationRepairExecutor
{
    private readonly ExecutionJournal _journal;
    private readonly IExecutionAdapter _adapter;
    private readonly RobotLogger _log;

    public ReconciliationRepairExecutor(ExecutionJournal journal, IExecutionAdapter adapter, RobotLogger log)
    {
        _journal = journal;
        _adapter = adapter;
        _log = log;
    }

    /// <summary>
    /// Process verdicts. Stale verdicts are rejected. Returns a list of repair actions taken.
    /// </summary>
    public IReadOnlyList<RepairAction> ExecuteRepairs(
        IReadOnlyList<ReconciliationVerdict> verdicts,
        DateTimeOffset utcNow)
    {
        var actions = new List<RepairAction>();

        foreach (var verdict in verdicts)
        {
            if (verdict.IsStale)
            {
                actions.Add(new RepairAction
                {
                    Instrument = verdict.Instrument,
                    ActionType = RepairActionType.StaleVerdictRejected,
                    Detail = $"Verdict stale (version={verdict.OwnershipVersion}), must re-classify"
                });
                continue;
            }

            if (verdict.MismatchTier == MismatchTier.Convergence && verdict.BrokerQty == 0 &&
                verdict.JournalOpenQty == 0 && verdict.ActiveSlotCount == 0 && verdict.OrphanSlotCount == 0)
            {
                var closed = TryCloseOrphanedJournalsWhenBrokerFlat(verdict.Instrument, utcNow);
                if (closed > 0)
                {
                    actions.Add(new RepairAction
                    {
                        Instrument = verdict.Instrument,
                        ActionType = RepairActionType.OrphanJournalsClosed,
                        Detail = $"Closed {closed} orphaned journal rows (broker flat, converged)",
                        OwnershipVersion = verdict.OwnershipVersion
                    });
                }
            }

            if (verdict.MismatchTier == MismatchTier.HardMismatch && verdict.BrokerQty != 0 && verdict.JournalOpenQty == 0)
            {
                var repaired = TryRepairTaggedBrokerWithoutJournal(verdict.Instrument, verdict.BrokerQty, utcNow);
                if (repaired)
                {
                    actions.Add(new RepairAction
                    {
                        Instrument = verdict.Instrument,
                        ActionType = RepairActionType.TaggedBrokerRepaired,
                        Detail = $"Repaired tagged broker without journal (brokerQty={verdict.BrokerQty})",
                        OwnershipVersion = verdict.OwnershipVersion
                    });
                }
            }
        }

        return actions;
    }

    private int TryCloseOrphanedJournalsWhenBrokerFlat(string instrument, DateTimeOffset utcNow)
    {
        try
        {
            var openByInstrument = _journal.GetOpenJournalEntriesByInstrument();
            var instKey = instrument?.Trim() ?? "";
            var count = 0;

            foreach (var kv in openByInstrument)
            {
                if (!string.Equals(kv.Key.Trim(), instKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var (tradingDate, stream, intentId, entry) in kv.Value)
                {
                    var openQty = ExecutionJournal.GetEntryRemainingOpenQuantity(entry);
                    if (_journal.RecordReconciliationComplete(tradingDate, stream, intentId, utcNow,
                            0, openQty, "ReconciliationRepairExecutor_broker_flat"))
                    {
                        count++;
                        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TRADE_RECONCILED", "ENGINE",
                            new
                            {
                                intent_id = intentId,
                                stream,
                                instrument,
                                trading_date = tradingDate,
                                completion_reason = "RECONCILIATION_BROKER_FLAT_VIA_REPAIR_EXECUTOR",
                                note = "Orphaned journal closed by ReconciliationRepairExecutor"
                            }));
                    }
                }
            }

            return count;
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "REPAIR_EXECUTOR_CLOSE_ERROR", "ENGINE",
                new { instrument, error = ex.Message }));
            return 0;
        }
    }

    private bool TryRepairTaggedBrokerWithoutJournal(string instrument, int brokerQty, DateTimeOffset utcNow)
    {
        try
        {
            return _adapter.TryRepairTaggedBrokerWithoutJournal(instrument, Math.Abs(brokerQty), 0, utcNow, out _, out _);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "REPAIR_EXECUTOR_TAGGED_REPAIR_ERROR", "ENGINE",
                new { instrument, error = ex.Message }));
            return false;
        }
    }
}

public sealed class RepairAction
{
    public string Instrument { get; init; } = "";
    public RepairActionType ActionType { get; init; }
    public string? Detail { get; init; }
    public long OwnershipVersion { get; init; }
}

public enum RepairActionType
{
    StaleVerdictRejected,
    OrphanJournalsClosed,
    TaggedBrokerRepaired,
    UntrackedRecoveryClosed,
    NoActionRequired
}
