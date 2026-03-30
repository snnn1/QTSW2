using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

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
    private readonly Action<string, DateTimeOffset, string, int, int>? _onQuantityMismatch;
    private readonly Action<Dictionary<string, (int AccountQty, int JournalQty)>>? _onReconciliationPassComplete;
    private readonly Func<string?>? _reconciliationAccountName;
    private readonly Func<string>? _reconciliationInstanceId;
    private readonly ReconciliationStateTracker? _reconciliationTracker;
    private readonly TimeSpan _reconciliationDebounceWindow;
    private readonly RuntimeAuditHub? _runtimeAudit;
    private readonly ReconciliationConvergenceTracker? _convergenceTracker;
    private readonly ReleaseReconciliationRedundancySuppression? _redundancySuppression;

    private DateTimeOffset _lastRunUtc = DateTimeOffset.MinValue;
    private const double ThrottleIntervalSeconds = 60.0;

    private sealed class SecondaryMismatchFastPathEntry
    {
        public int AccountQty;
        public int CachedJournalQty;
        public long ActivityGeneration;
        public DateTimeOffset LastFullVerifyUtc;
        public DateTimeOffset LastSecondarySkipLogUtc;
    }

    private readonly Dictionary<string, SecondaryMismatchFastPathEntry> _secondaryMismatchFastPath =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan SecondaryMismatchFastPathMaxStale =
        ReconciliationStateTracker.DefaultDebounceWindow;

    private static readonly TimeSpan SecondarySkipLogCoalesce = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (int AccountQty, int JournalQty, long ActivityGen, DateTimeOffset LastSampleUtc)>
        _nonOwnerConvergenceThrottle = new(StringComparer.OrdinalIgnoreCase);

    public ReconciliationRunner(IExecutionAdapter adapter, ExecutionJournal journal, RobotLogger log,
        Action<string, DateTimeOffset, string, int, int>? onQuantityMismatch = null,
        Action<Dictionary<string, (int AccountQty, int JournalQty)>>? onReconciliationPassComplete = null,
        Func<string?>? reconciliationAccountName = null,
        Func<string>? reconciliationInstanceId = null,
        ReconciliationStateTracker? reconciliationTracker = null,
        TimeSpan? reconciliationDebounceWindow = null,
        RuntimeAuditHub? runtimeAudit = null,
        ReconciliationConvergenceTracker? convergenceTracker = null,
        ReleaseReconciliationRedundancySuppression? redundancySuppression = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onQuantityMismatch = onQuantityMismatch;
        _onReconciliationPassComplete = onReconciliationPassComplete;
        _reconciliationAccountName = reconciliationAccountName;
        _reconciliationInstanceId = reconciliationInstanceId;
        _reconciliationTracker = reconciliationTracker;
        _reconciliationDebounceWindow = reconciliationDebounceWindow ?? ReconciliationStateTracker.DefaultDebounceWindow;
        _runtimeAudit = runtimeAudit;
        _convergenceTracker = convergenceTracker;
        _redundancySuppression = redundancySuppression;
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
        RunInternal(utcNow, new ReconciliationRunOptions { BypassRedundancySuppression = true });
    }

    /// <summary>
    /// P1.5-D: Instrument-scoped gate recovery — skips <c>RECONCILIATION_QTY_MISMATCH</c> / freeze callbacks for the gated instrument
    /// when broker and journal disagree with risk on the broker side, but still runs the same broker-flat open-journal closure path
    /// when the account is flat and there are no working orders (local journal alignment only; no broker flatten).
    /// </summary>
    public void ForceRunGateRecoveryForInstrument(DateTimeOffset utcNow, string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        RunInternal(utcNow, new ReconciliationRunOptions { GateRecoveryInstrument = instrument.Trim() });
    }

    private void RunInternal(DateTimeOffset utcNow, ReconciliationRunOptions? options)
    {
        var gateInst = options?.GateRecoveryInstrument?.Trim();
        var gateMode = !string.IsNullOrEmpty(gateInst);
        var bypassRedundancy = options?.BypassRedundancySuppression ?? false;
        _lastRunUtc = utcNow;
        _runtimeAudit?.NotifyReconciliationRunCompleted();
        var cpuStart = _runtimeAudit != null ? RuntimeAuditHub.CpuStart() : 0L;

        AccountSnapshot snap;
        try
        {
            snap = _adapter.GetAccountSnapshot(utcNow);
        }
        catch (Exception ex)
        {
            if (cpuStart != 0) _runtimeAudit?.CpuEnd(cpuStart, RuntimeAuditSubsystem.Reconciliation);
            _log.Write(RobotEvents.EngineBase(utcNow, "", "ACCOUNT_SNAPSHOT_ERROR", "ENGINE",
                new { error = ex.Message, context = "ReconciliationRunner" }));
            return;
        }

        if (snap == null)
        {
            if (cpuStart != 0) _runtimeAudit?.CpuEnd(cpuStart, RuntimeAuditSubsystem.Reconciliation);
            return;
        }

        var positions = snap.Positions ?? new List<PositionSnapshot>();
        var workingOrders = snap.WorkingOrders ?? new List<WorkingOrderSnapshot>();

        var instrumentsWithPosition = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountQtyByInstrument = BrokerPositionResolver.BuildReconciliationAbsTotalsByCanonicalKey(positions);
        foreach (var p in positions)
        {
            if (p.Quantity != 0 && !string.IsNullOrWhiteSpace(p.Instrument))
                instrumentsWithPosition.Add(p.Instrument.Trim());
        }

        try
        {
            var closedRecovery = _journal.CloseUntrackedFillRecoveryMarkersWhenBrokerFlat(accountQtyByInstrument, utcNow);
            if (closedRecovery > 0)
            {
                _log.Write(RobotEvents.EngineBase(utcNow, "", "UNTRACKED_FILL_RECOVERY_JOURNAL_CLOSED_BROKER_FLAT", "ENGINE",
                    new { count = closedRecovery, note = "Broker snapshot flat for instrument(s) with recovery marker" }));
            }
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "UNTRACKED_FILL_RECOVERY_JOURNAL_CLOSE_ERROR", "ENGINE",
                new { error = ex.Message, exception_type = ex.GetType().Name }));
        }

        var openByInstrument = _journal.GetOpenJournalEntriesByInstrument();
        var passSignature = ReleaseReconciliationRedundancySuppression.BuildPeriodicPassSignature(accountQtyByInstrument, openByInstrument);
        if (_redundancySuppression != null && !gateMode && !bypassRedundancy &&
            _redundancySuppression.TrySkipRedundantPeriodicPass(passSignature, _redundancySuppression.ExecutionActivityGeneration, utcNow))
        {
            if (cpuStart != 0) _runtimeAudit?.CpuEnd(cpuStart, RuntimeAuditSubsystem.Reconciliation);
            return;
        }

        foreach (var kvp in accountQtyByInstrument)
        {
            var inst = kvp.Key;
            var accountQty = kvp.Value;
            var execVariant = inst.StartsWith("M") && inst.Length > 1 ? inst : "M" + inst;

            var acct = _reconciliationAccountName?.Invoke();
            var instanceId = _reconciliationInstanceId?.Invoke() ?? "";
            var actGen = _redundancySuppression?.ExecutionActivityGeneration ?? 0;

            var allowSecondaryFastPath = !gateMode && !bypassRedundancy && _reconciliationTracker != null;
            if (allowSecondaryFastPath &&
                _secondaryMismatchFastPath.TryGetValue(inst, out var fp) &&
                fp.AccountQty == accountQty &&
                fp.ActivityGeneration == actGen &&
                (utcNow - fp.LastFullVerifyUtc) < SecondaryMismatchFastPathMaxStale &&
                _reconciliationTracker.TryPeekSecondaryStableMismatchFastPath(acct, inst, instanceId, accountQty, out var peekJq,
                    out var peekOwner) &&
                fp.CachedJournalQty == peekJq)
            {
                var shouldLog = (utcNow - fp.LastSecondarySkipLogUtc) >= SecondarySkipLogCoalesce;
                if (shouldLog)
                    fp.LastSecondarySkipLogUtc = utcNow;
                if (shouldLog)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED", "ENGINE",
                        new
                        {
                            account = acct ?? "",
                            instrument = inst,
                            account_qty = accountQty,
                            journal_qty = peekJq,
                            owner_instance_id = peekOwner,
                            current_instance_id = instanceId,
                            suppressed_repeat = true,
                            metrics = new
                            {
                                reconciliation_mismatch_total = _reconciliationTracker.Metrics.ReconciliationMismatchTotal,
                                reconciliation_debounced_total = _reconciliationTracker.Metrics.ReconciliationDebouncedTotal,
                                reconciliation_secondary_skipped_total = _reconciliationTracker.Metrics.ReconciliationSecondarySkippedTotal,
                                reconciliation_resolved_total = _reconciliationTracker.Metrics.ReconciliationResolvedTotal
                            },
                            note =
                                "Single-writer reconciliation: non-owner chart skipped mismatch pipeline (pre-gate fast path; full verify on timer/activity)"
                        }));
                }
                continue;
            }

            var journalQty = _journal.GetOpenJournalQuantitySumForInstrumentFromMap(openByInstrument, inst, execVariant);

            if (accountQty > 0 && journalQty == 0 &&
                _adapter.TryRepairTaggedBrokerWithoutJournal(inst, accountQty, journalQty, utcNow, out _, out _))
            {
                openByInstrument = _journal.GetOpenJournalEntriesByInstrument();
                journalQty = _journal.GetOpenJournalQuantitySumForInstrumentFromMap(openByInstrument, inst, execVariant);
            }

            if (journalQty == accountQty)
            {
                _secondaryMismatchFastPath.Remove(inst);
                continue;
            }

            if (journalQty != accountQty)
            {
                if (gateMode && MatchesGateInstrument(inst, gateInst!))
                    continue;

                ReconciliationMismatchGateResult gateResult = new(ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback, null);
                if (_reconciliationTracker != null)
                {
                    gateResult = _reconciliationTracker.EvaluateRunnerMismatch(
                        acct, inst, accountQty, journalQty, utcNow, instanceId, _reconciliationDebounceWindow);
                }

                if (gateResult.Outcome == ReconciliationMismatchGateOutcome.SecondaryInstanceSkip)
                {
                    if (allowSecondaryFastPath)
                    {
                        _secondaryMismatchFastPath[inst] = new SecondaryMismatchFastPathEntry
                        {
                            AccountQty = accountQty,
                            CachedJournalQty = journalQty,
                            ActivityGeneration = actGen,
                            LastFullVerifyUtc = utcNow,
                            LastSecondarySkipLogUtc = utcNow
                        };
                    }
                    _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_SECONDARY_INSTANCE_SKIPPED", "ENGINE",
                        new
                        {
                            account = acct ?? "",
                            instrument = inst,
                            account_qty = accountQty,
                            journal_qty = journalQty,
                            owner_instance_id = gateResult.OwnerInstanceId,
                            current_instance_id = instanceId,
                            metrics = _reconciliationTracker != null
                                ? new
                                {
                                    reconciliation_mismatch_total = _reconciliationTracker.Metrics.ReconciliationMismatchTotal,
                                    reconciliation_debounced_total = _reconciliationTracker.Metrics.ReconciliationDebouncedTotal,
                                    reconciliation_secondary_skipped_total = _reconciliationTracker.Metrics.ReconciliationSecondarySkippedTotal,
                                    reconciliation_resolved_total = _reconciliationTracker.Metrics.ReconciliationResolvedTotal
                                }
                                : null,
                            note = "Single-writer reconciliation: non-owner chart skipped mismatch pipeline"
                        }));
                    continue;
                }

                _secondaryMismatchFastPath.Remove(inst);

                if (gateResult.Outcome == ReconciliationMismatchGateOutcome.EmitStillOpenInfoOnly)
                {
                    _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_QTY_MISMATCH_STILL_OPEN", "ENGINE",
                        new
                        {
                            account = acct ?? "",
                            instrument = inst,
                            account_qty = accountQty,
                            journal_qty = journalQty,
                            owner_instance_id = gateResult.OwnerInstanceId,
                            metrics = _reconciliationTracker != null
                                ? new
                                {
                                    reconciliation_mismatch_total = _reconciliationTracker.Metrics.ReconciliationMismatchTotal,
                                    reconciliation_debounced_total = _reconciliationTracker.Metrics.ReconciliationDebouncedTotal,
                                    reconciliation_secondary_skipped_total = _reconciliationTracker.Metrics.ReconciliationSecondarySkippedTotal,
                                    reconciliation_resolved_total = _reconciliationTracker.Metrics.ReconciliationResolvedTotal
                                }
                                : null,
                            note = "Mismatch unchanged within debounce — recovery not re-requested"
                        }));
                    continue;
                }

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
                var canonicalExposure = BrokerPositionResolver.ResolveFromSnapshots(positions, inst);
                _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_CONTEXT", "ENGINE",
                    new
                    {
                        instrument = inst,
                        canonical_broker_key = canonicalExposure.CanonicalKey,
                        broker_qty = accountQty,
                        broker_exposure_aggregated = canonicalExposure.IsAggregatedMultipleRows,
                        broker_position_rows = BrokerPositionResolver.ToDiagnosticRows(canonicalExposure),
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
                _onQuantityMismatch?.Invoke(inst, utcNow, $"QTY_MISMATCH:account={accountQty},journal={journalQty}", accountQty, journalQty);
            }
        }

        var instrumentsChecked = openByInstrument.Count;
        var journalsReconciled = 0;
        var gateBrokerFlatJournalsClosed = 0;

        if (instrumentsChecked == 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_PASS_SUMMARY", "ENGINE",
                new { instruments_checked = 0, journals_reconciled = 0, note = "No open journals to reconcile" }));
            EmitConvergenceSamples(utcNow, workingOrders, accountQtyByInstrument, openByInstrument);
            if (cpuStart != 0) _runtimeAudit?.CpuEnd(cpuStart, RuntimeAuditSubsystem.Reconciliation);
            if (_redundancySuppression != null && !gateMode)
                _redundancySuppression.NotifyPeriodicReconciliationPassCompleted(passSignature, _redundancySuppression.ExecutionActivityGeneration, utcNow);
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
                    if (gateMode && !string.IsNullOrEmpty(gateInst) && MatchesGateInstrument(instrument, gateInst))
                        gateBrokerFlatJournalsClosed++;
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

        if (gateBrokerFlatJournalsClosed > 0)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, "", "GATE_RECOVERY_BROKER_FLAT_JOURNAL_CLOSURE", "ENGINE",
                new
                {
                    gate_instrument = gateInst,
                    journals_closed = gateBrokerFlatJournalsClosed,
                    note =
                        "P1.5 gate recovery: allowed broker-flat journal closure (no position, no working orders) so release readiness can converge"
                }));
        }

        _log.Write(RobotEvents.EngineBase(utcNow, "", "RECONCILIATION_PASS_SUMMARY", "ENGINE",
            new
            {
                instruments_checked = instrumentsChecked,
                journals_reconciled = journalsReconciled,
                note = "Reconciliation pass complete"
            }));

        EmitConvergenceSamples(utcNow, workingOrders, accountQtyByInstrument, openByInstrument);

        // Notify engine of qty by instrument (for unfreezing when mismatch resolved)
        _onReconciliationPassComplete?.Invoke(BuildQtyByInstrument(accountQtyByInstrument));

        if (cpuStart != 0) _runtimeAudit?.CpuEnd(cpuStart, RuntimeAuditSubsystem.Reconciliation);
        if (_redundancySuppression != null && !gateMode)
            _redundancySuppression.NotifyPeriodicReconciliationPassCompleted(passSignature, _redundancySuppression.ExecutionActivityGeneration, utcNow);
    }

    private void EmitConvergenceSamples(
        DateTimeOffset utcNow,
        List<WorkingOrderSnapshot> workingOrders,
        Dictionary<string, int> accountQtyByInstrument,
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument)
    {
        if (_convergenceTracker == null) return;

        var acct = _reconciliationAccountName?.Invoke();
        var instanceId = _reconciliationInstanceId?.Invoke() ?? "";
        var actGen = _redundancySuppression?.ExecutionActivityGeneration ?? 0;

        var instSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in accountQtyByInstrument.Keys)
            if (!string.IsNullOrWhiteSpace(k)) instSet.Add(k.Trim());
        foreach (var k in openByInstrument.Keys)
            if (!string.IsNullOrWhiteSpace(k)) instSet.Add(k.Trim());

        foreach (var inst in instSet)
        {
            var accountQty = accountQtyByInstrument.TryGetValue(inst, out var aq) ? aq : 0;
            var execVariant = inst.StartsWith("M") && inst.Length > 1 ? inst : "M" + inst;
            var journalQty = _journal.GetOpenJournalQuantitySumForInstrumentFromMap(openByInstrument, inst, execVariant);
            var openOrders = CountWorkingForInstrument(workingOrders, inst);
            var intentCount = CountJournalIntents(openByInstrument, inst, execVariant);
            var hasMismatch = accountQty != journalQty;
            var nonOwnerConv = hasMismatch && _reconciliationTracker != null &&
                _reconciliationTracker.TryPeekNonOwnerWithStableQtyMismatchEpisode(acct, inst, instanceId, accountQty,
                    journalQty, out _);
            if (nonOwnerConv)
            {
                if (_nonOwnerConvergenceThrottle.TryGetValue(inst, out var throttle) &&
                    throttle.AccountQty == accountQty && throttle.JournalQty == journalQty &&
                    throttle.ActivityGen == actGen &&
                    (utcNow - throttle.LastSampleUtc).TotalSeconds <
                    ReconciliationStateTracker.DefaultDebounceWindow.TotalSeconds)
                    continue;
            }

            _convergenceTracker.OnInstrumentReconciliationSample(utcNow, inst, accountQty, journalQty, openOrders, intentCount, hasMismatch, accountQty - journalQty);

            if (nonOwnerConv)
                _nonOwnerConvergenceThrottle[inst] = (accountQty, journalQty, actGen, utcNow);
            else
                _nonOwnerConvergenceThrottle.Remove(inst);
        }
    }

    private static int CountWorkingForInstrument(List<WorkingOrderSnapshot> workingOrders, string instrument)
    {
        var n = 0;
        foreach (var w in workingOrders)
        {
            if (string.IsNullOrWhiteSpace(w.Instrument)) continue;
            if (string.Equals(w.Instrument.Trim(), instrument, StringComparison.OrdinalIgnoreCase))
                n++;
        }
        return n;
    }

    private static int CountJournalIntents(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        string inst,
        string execVariant)
    {
        var n = 0;
        if (openByInstrument.TryGetValue(inst, out var e1)) n += e1.Count;
        if (openByInstrument.TryGetValue(execVariant, out var e2)) n += e2.Count;
        return n;
    }

    private Dictionary<string, (int AccountQty, int JournalQty)> BuildQtyByInstrument(Dictionary<string, int> accountQtyByInstrument)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var openByInstrument = _journal.GetOpenJournalEntriesByInstrument();
        var allInstruments = new HashSet<string>(accountQtyByInstrument.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var inst in openByInstrument.Keys)
        {
            if (!string.IsNullOrWhiteSpace(inst))
                allInstruments.Add(inst.Trim());
        }
        foreach (var inst in allInstruments)
        {
            var accountQty = accountQtyByInstrument.TryGetValue(inst, out var aq) ? aq : 0;
            var execVariant = inst.StartsWith("M") && inst.Length > 1 ? inst : "M" + inst;
            var journalQty = _journal.GetOpenJournalQuantitySumForInstrumentFromMap(openByInstrument, inst, execVariant);
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
