using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Reconciles orphaned execution journals when broker position is flat.
/// Run on Realtime start and periodically (throttled) to close journals
/// for trades closed externally (e.g. strategy stop before slot expiry).
/// </summary>
public sealed class ReconciliationRunner
{
    private readonly IExecutionAdapter _adapter;
    private readonly ExecutionJournal _journal;
    private readonly RobotLogger _log;

    private DateTimeOffset _lastRunUtc = DateTimeOffset.MinValue;
    private const double ThrottleIntervalSeconds = 60.0;

    public ReconciliationRunner(IExecutionAdapter adapter, ExecutionJournal journal, RobotLogger log)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Run once on Realtime transition (NT context ready).
    /// </summary>
    public void RunOnRealtimeStart(DateTimeOffset utcNow)
    {
        RunInternal(utcNow);
    }

    /// <summary>
    /// Run periodically; throttled to at most once per ThrottleIntervalSeconds.
    /// </summary>
    public void RunPeriodicThrottle(DateTimeOffset utcNow)
    {
        if ((utcNow - _lastRunUtc).TotalSeconds < ThrottleIntervalSeconds)
            return;
        RunInternal(utcNow);
    }

    private void RunInternal(DateTimeOffset utcNow)
    {
        _lastRunUtc = utcNow;

        AccountSnapshot snap;
        try
        {
            snap = _adapter.GetAccountSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "ACCOUNT_SNAPSHOT_ERROR", "ENGINE",
                new { error = ex.Message, context = "ReconciliationRunner" }));
            return;
        }

        if (snap == null)
            return;

        var positions = snap.Positions ?? new List<PositionSnapshot>();
        var workingOrders = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();

        var instrumentsWithPosition = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions)
        {
            if (p.Quantity != 0 && !string.IsNullOrWhiteSpace(p.Instrument))
                instrumentsWithPosition.Add(p.Instrument.Trim());
        }

        var openByInstrument = _journal.GetOpenJournalEntriesByInstrument();
        var instrumentsChecked = openByInstrument.Count;
        var journalsReconciled = 0;

        if (instrumentsChecked == 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_PASS_SUMMARY", "ENGINE",
                new { instruments_checked = 0, journals_reconciled = 0, note = "No open journals to reconcile" }));
            return;
        }

        foreach (var kvp in openByInstrument)
        {
            var instrument = kvp.Key;
            var entries = kvp.Value;

            if (entries.Count == 0) continue;

            var brokerFlat = !instrumentsWithPosition.Contains(instrument);
            var hasWorkingOrders = workingOrders.Any(w =>
                string.Equals(w.Instrument?.Trim(), instrument, StringComparison.OrdinalIgnoreCase));

            if (!brokerFlat)
                continue;

            if (hasWorkingOrders)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, entries[0].TradingDate, "RECONCILIATION_SKIPPED_HAS_WORKING_ORDERS", "ENGINE",
                    new
                    {
                        instrument,
                        open_journal_count = entries.Count,
                        note = "Broker flat but working orders exist; defer reconciliation"
                    }));
                continue;
            }

            foreach (var (tradingDate, stream, intentId, _) in entries)
            {
                if (_journal.RecordReconciliationComplete(tradingDate, stream, intentId, utcNow))
                {
                    journalsReconciled++;
                    _log.Write(RobotEvents.EngineBase(utcNow, tradingDate, "TRADE_RECONCILED", "ENGINE",
                        new
                        {
                            intent_id = intentId,
                            stream,
                            instrument,
                            trading_date = tradingDate,
                            completion_reason = CompletionReasons.RECONCILIATION_BROKER_FLAT,
                            note = "Orphaned journal closed; broker position flat"
                        }));
                }
            }
        }

        _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_PASS_SUMMARY", "ENGINE",
            new
            {
                instruments_checked = instrumentsChecked,
                journals_reconciled = journalsReconciled,
                note = "Reconciliation pass complete"
            }));
    }
}
