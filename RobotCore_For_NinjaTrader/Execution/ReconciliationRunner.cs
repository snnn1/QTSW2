using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Reconciles orphaned execution journals when broker position is flat.
/// Run on Realtime start and periodically (throttled) to close journals
/// for trades closed externally (e.g. strategy stop before slot expiry).
/// Also performs quantity reconciliation: account vs journal.
/// </summary>
public sealed class ReconciliationRunner
{
    private readonly IExecutionAdapter _adapter;
    private readonly ExecutionJournal _journal;
    private readonly RobotLogger _log;
    private readonly Action<string, DateTimeOffset, string>? _onQuantityMismatch;
    private readonly Action<Dictionary<string, (int AccountQty, int JournalQty)>>? _onReconciliationPassComplete;

    private DateTimeOffset _lastRunUtc = DateTimeOffset.MinValue;
    private const double ThrottleIntervalSeconds = 60.0;

    public ReconciliationRunner(IExecutionAdapter adapter, ExecutionJournal journal, RobotLogger log,
        Action<string, DateTimeOffset, string>? onQuantityMismatch = null,
        Action<Dictionary<string, (int AccountQty, int JournalQty)>>? onReconciliationPassComplete = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onQuantityMismatch = onQuantityMismatch;
        _onReconciliationPassComplete = onReconciliationPassComplete;
    }

    /// <summary>
    /// Run once on Realtime transition (NT context ready).
    /// </summary>
    public void RunOnRealtimeStart(DateTimeOffset utcNow)
    {
        RunInternal(utcNow, null);
    }

    /// <summary>
    /// Run periodically; throttled to at most once per ThrottleIntervalSeconds.
    /// </summary>
    public void RunPeriodicThrottle(DateTimeOffset utcNow)
    {
        if ((utcNow - _lastRunUtc).TotalSeconds < ThrottleIntervalSeconds)
            return;
        RunInternal(utcNow, null);
    }

    /// <summary>
    /// Bypass periodic throttle (e.g. normal-mode full reconciliation).
    /// </summary>
    public void ForceRunNow(DateTimeOffset utcNow)
    {
        RunInternal(utcNow, null);
    }

    /// <summary>
    /// P1.5-D: Instrument-scoped gate recovery — skips qty-mismatch freeze callbacks and destructive orphan journal closure for this instrument.
    /// </summary>
    public void ForceRunGateRecoveryForInstrument(DateTimeOffset utcNow, string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        RunInternal(utcNow, new ReconciliationRunOptions { GateRecoveryInstrument = instrument.Trim() });
    }

    private void RunInternal(DateTimeOffset utcNow, ReconciliationRunOptions? options)
    {
        _lastRunUtc = utcNow;
        var gateInst = options?.GateRecoveryInstrument?.Trim();
        var gateMode = !string.IsNullOrEmpty(gateInst);

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
        var accountQtyByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions)
        {
            if (p.Quantity != 0 && !string.IsNullOrWhiteSpace(p.Instrument))
            {
                var inst = p.Instrument.Trim();
                instrumentsWithPosition.Add(inst);
                var qty = Math.Abs(p.Quantity);
                accountQtyByInstrument.TryGetValue(inst, out var existing);
                accountQtyByInstrument[inst] = existing + qty;
            }
        }

        var openByInstrument = _journal.GetOpenJournalEntriesByInstrument();
        foreach (var kvp in accountQtyByInstrument)
        {
            var inst = kvp.Key;
            var accountQty = kvp.Value;
            var execVariant = inst.StartsWith("M") && inst.Length > 1 ? inst : "M" + inst;
            var journalQty = _journal.GetOpenJournalQuantitySumForInstrument(inst, execVariant);
            if (journalQty != accountQty)
            {
                if (gateMode && MatchesGateInstrument(inst, gateInst!))
                    continue;

                // Emit RECONCILIATION_CONTEXT before RECONCILIATION_QTY_MISMATCH for ops-grade diagnostics
                var intentIds = new List<string>();
                var lastFills = new List<object>();
                foreach (var okvp in openByInstrument)
                {
                    var jinst = okvp.Key?.Trim() ?? "";
                    if (string.IsNullOrEmpty(jinst)) continue;
                    if (!string.Equals(jinst, inst, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(jinst, execVariant, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var (td, stream, iid, entry) in okvp.Value)
                    {
                        intentIds.Add(iid);
                        if (lastFills.Count < 5 && entry.EntryFilledQuantityTotal > 0)
                        {
                            lastFills.Add(new
                            {
                                intent_id = iid,
                                qty = entry.EntryFilledQuantityTotal,
                                price = entry.EntryAvgFillPrice,
                                at = entry.EntryFilledAtUtc
                            });
                        }
                    }
                }
                var taxonomy = journalQty > accountQty ? "journal_ahead" : (accountQty > journalQty ? "broker_ahead" : "unknown");
                var openInstSummary = openByInstrument.ToDictionary(k => k.Key, v => v.Value.Sum(e => e.Entry.EntryFilledQuantityTotal));
                _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_CONTEXT", "ENGINE",
                    new
                    {
                        instrument = inst,
                        broker_qty = accountQty,
                        journal_qty = journalQty,
                        intent_ids = intentIds,
                        last_fills = lastFills,
                        mismatch_taxonomy = taxonomy,
                        journal_dir = _journal.JournalDirectory,
                        open_instruments_qty = openInstSummary,
                        note = "Context for RECONCILIATION_QTY_MISMATCH"
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_QTY_MISMATCH", "ENGINE",
                    new
                    {
                        instrument = inst,
                        account_qty = accountQty,
                        journal_qty = journalQty,
                        note = "Partial protection is worse than none. Freeze instrument-level."
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, "", "POSITION_DRIFT_DETECTED", "ENGINE",
                    new
                    {
                        instrument = inst,
                        broker_qty = accountQty,
                        engine_qty = journalQty,
                        journal_qty = journalQty,
                        drift_class = journalQty > accountQty ? "journal_ahead" : "broker_ahead",
                        intent_ids = intentIds,
                        note = "Broker position and journal disagree."
                    }));
                _log.Write(RobotEvents.EngineBase(utcNow, "", "EXPOSURE_INTEGRITY_VIOLATION", "ENGINE",
                    new
                    {
                        instrument = inst,
                        expected_position_from_intents = journalQty,
                        broker_position = accountQty,
                        drift_class = journalQty > accountQty ? "journal_ahead" : "broker_ahead",
                        intent_ids = intentIds,
                        note = "Total exposure must match system intent. Critical invariant violated."
                    }));
                _onQuantityMismatch?.Invoke(inst, utcNow, $"QTY_MISMATCH:account={accountQty},journal={journalQty}");
            }
        }

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

            if (gateMode && MatchesGateInstrument(instrument, gateInst!))
                continue;

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

        // Notify engine of qty by instrument (for unfreezing when mismatch resolved)
        _onReconciliationPassComplete?.Invoke(BuildQtyByInstrument(accountQtyByInstrument));
    }

    private Dictionary<string, (int AccountQty, int JournalQty)> BuildQtyByInstrument(Dictionary<string, int> accountQtyByInstrument)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var allInstruments = new HashSet<string>(accountQtyByInstrument.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var inst in _journal.GetOpenJournalEntriesByInstrument().Keys)
        {
            if (!string.IsNullOrWhiteSpace(inst))
                allInstruments.Add(inst.Trim());
        }
        foreach (var inst in allInstruments)
        {
            var accountQty = accountQtyByInstrument.TryGetValue(inst, out var aq) ? aq : 0;
            var execVariant = inst.StartsWith("M") && inst.Length > 1 ? inst : "M" + inst;
            var journalQty = _journal.GetOpenJournalQuantitySumForInstrument(inst, execVariant);
            result[inst] = (accountQty, journalQty);
        }
        return result;
    }

    private static bool MatchesGateInstrument(string journalOrBrokerKey, string gateInstrument)
    {
        if (string.IsNullOrWhiteSpace(journalOrBrokerKey) || string.IsNullOrWhiteSpace(gateInstrument))
            return false;
        var k = journalOrBrokerKey.Trim();
        var g = gateInstrument.Trim();
        if (string.Equals(k, g, StringComparison.OrdinalIgnoreCase))
            return true;
        var gVariant = g.StartsWith("M", StringComparison.OrdinalIgnoreCase) && g.Length > 1 ? g : "M" + g;
        var kVariant = k.StartsWith("M", StringComparison.OrdinalIgnoreCase) && k.Length > 1 ? k : "M" + k;
        return string.Equals(k, gVariant, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kVariant, g, StringComparison.OrdinalIgnoreCase);
    }
}
