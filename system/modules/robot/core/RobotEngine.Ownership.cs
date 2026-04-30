using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core;

using QTSW2.Robot.Core.Execution;

public sealed partial class RobotEngine
{
    /// <summary>
    /// After trading date is locked and streams exist, rebuild <see cref="InstrumentIntentCoordinator"/> rows
    /// from open journal files so exit fills on reconnect hit a real exposure object (see INTENT_EXIT_FILL_NO_EXPOSURE).
    /// </summary>
    private void TryRehydrateSingleIntentExposureFromDurableJournal(
        string intentId,
        string tradingDate,
        string stream,
        string executionInstrument,
        string direction,
        int entryFilledQty,
        int exitFilledQty,
        DateTimeOffset utcNow,
        string source)
    {
        if (_intentExposureCoordinator == null || _executionAdapter is not NinjaTraderSimAdapter)
            return;
        if (string.IsNullOrEmpty(TradingDateString) ||
            !string.Equals(tradingDate, TradingDateString, StringComparison.Ordinal))
            return;
        if (string.Equals(stream, ExecutionJournal.UntrackedFillRecoveryStream, StringComparison.OrdinalIgnoreCase))
            return;
        var remaining = entryFilledQty - exitFilledQty;
        if (remaining <= 0)
            return;
        var execInst = executionInstrument?.Trim() ?? "";
        if (string.IsNullOrEmpty(execInst))
            return;
        var canonical = GetCanonicalInstrument(execInst);
        var dir = string.IsNullOrWhiteSpace(direction) ? "Long" : direction;

        _intentExposureCoordinator.TryRehydrateOpenExposureFromJournal(
            intentId,
            stream,
            canonical,
            dir,
            entryFilledQty,
            exitFilledQty,
            utcNow,
            source);
    }

    private void RehydrateOpenIntentExposuresFromJournal(DateTimeOffset utcNow)
    {
        var openByInstrument = _executionJournal.GetOpenJournalEntriesByInstrument();
        foreach (var kv in openByInstrument)
        {
            foreach (var (tradingDate, stream, intentId, entry) in kv.Value)
            {
                var execInst = entry.Instrument?.Trim() ?? "";
                var dir = string.IsNullOrWhiteSpace(entry.Direction) ? "Long" : entry.Direction!;
                TryRehydrateSingleIntentExposureFromDurableJournal(
                    intentId,
                    tradingDate,
                    stream,
                    execInst,
                    dir,
                    entry.EntryFilledQuantityTotal,
                    entry.ExitFilledQuantityTotal,
                    utcNow,
                    "journal_recovery");
            }
        }

        if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
            RestoreOwnershipLedgerFromJournal(openByInstrument, utcNow);
    }

    private void RestoreOwnershipLedgerFromJournal(
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument,
        DateTimeOffset utcNow)
    {
        if (_ownershipLedger == null) return;

        var td = TradingDateString;
        if (string.IsNullOrEmpty(td)) td = utcNow.ToString("yyyy-MM-dd");
        _ownershipLedger.SetEventJournal(_ownershipEventJournal, td);

        var accountName = OwnershipAccountKey;

        // Phase 6c: Prefer the ownership event journal for restore when available.
        // It provides true monotonic durable sequence and includes transfers + orphans.
        // Falls back to the interim timestamp ordering (Phase 1c) if the event journal is empty.
        bool restoredFromEventJournal = false;
        if (_ownershipEventJournal != null)
        {
            try
            {
                var events = _ownershipEventJournal.ReadEvents(td);
                if (events.Count > 0)
                {
                    restoredFromEventJournal = true;
                    var groupedByInstrument = events.GroupBy(e => e.Instrument?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

                    foreach (var group in groupedByInstrument)
                    {
                        long seq = 0;
                        var rows = new List<JournalRestoreRow>();
                        foreach (var evt in group.OrderBy(e => e.OwnershipEventSequence))
                        {
                            if (evt.Kind == OwnershipEventKind.MappedEntryFill ||
                                evt.Kind == OwnershipEventKind.MappedExitFill ||
                                evt.Kind == OwnershipEventKind.OrphanFill)
                            {
                                rows.Add(new JournalRestoreRow
                                {
                                    IntentId = evt.IntentId,
                                    Stream = "",
                                    Direction = evt.Direction == SlotDirection.Short ? "Short" : "Long",
                                    EntryFilledQty = evt.Kind == OwnershipEventKind.MappedEntryFill || evt.Kind == OwnershipEventKind.OrphanFill
                                        ? evt.Qty : 0,
                                    ExitFilledQty = evt.Kind == OwnershipEventKind.MappedExitFill ? evt.Qty : 0,
                                    ExecutionSequence = seq++,
                                    IsOrphan = evt.Kind == OwnershipEventKind.OrphanFill
                                });
                            }
                            else if (evt.Kind == OwnershipEventKind.OwnershipTransfer)
                            {
                                rows.Add(new JournalRestoreRow
                                {
                                    IntentId = evt.IntentId,
                                    Stream = "",
                                    Direction = evt.Direction == SlotDirection.Short ? "Short" : "Long",
                                    RowType = JournalRowType.Transfer,
                                    FromIntentId = evt.FromIntentId,
                                    ToIntentId = evt.ToIntentId,
                                    TransferQty = evt.Qty,
                                    ExecutionSequence = seq++
                                });
                            }
                        }

                        if (rows.Count > 0)
                        {
                            _ownershipLedger.RestoreFromJournal(accountName, group.Key, rows, utcNow);

                            var isConsistent = _ownershipLedger.AssertDeterministicRestore(accountName, group.Key, rows);
                            if (!isConsistent)
                            {
                                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                                    eventType: "OWNERSHIP_RESTORE_DETERMINISM_FAILURE", state: "ENGINE",
                                    new { instrument = group.Key, row_count = rows.Count, source = "event_journal" }));
                            }
                        }
                    }

                    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                        eventType: "OWNERSHIP_RESTORED_FROM_EVENT_JOURNAL", state: "ENGINE",
                        new { event_count = events.Count }));
                }
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                    eventType: "OWNERSHIP_EVENT_JOURNAL_RESTORE_ERROR", state: "ENGINE",
                    new { error = ex.Message }));
            }
        }

        // Fallback: interim timestamp ordering from Phase 1c (for first run or pre-event-journal trading dates)
        if (!restoredFromEventJournal)
        {
            foreach (var kv in openByInstrument)
            {
                var instrument = kv.Key;
                var sorted = kv.Value
                    .Select(t => (t.TradingDate, t.Stream, t.IntentId, t.Entry,
                        Seq: ParseRestoreSequence(t.Entry,
                            string.Equals(t.Entry.IntentType, ExecutionJournal.IntentTypeRecovered, StringComparison.OrdinalIgnoreCase))))
                    .OrderBy(t => t.Seq)
                    .ThenBy(t => t.TradingDate, StringComparer.Ordinal)
                    .ThenBy(t => t.Stream, StringComparer.Ordinal)
                    .ThenBy(t => t.IntentId, StringComparer.Ordinal)
                    .ToList();

                long seq = 0;
                var rows = new List<JournalRestoreRow>();
                foreach (var (_, stream, intentId, entry, _) in sorted)
                {
                    var isOrphan = string.Equals(entry.IntentType, ExecutionJournal.IntentTypeRecovered, StringComparison.OrdinalIgnoreCase);
                    rows.Add(new JournalRestoreRow
                    {
                        IntentId = intentId,
                        Stream = stream,
                        Direction = entry.Direction,
                        EntryFilledQty = entry.EntryFilledQuantityTotal,
                        ExitFilledQty = entry.ExitFilledQuantityTotal,
                        ExecutionSequence = seq++,
                        IsOrphan = isOrphan
                    });
                }

                if (rows.Count > 0)
                {
                    _ownershipLedger.RestoreFromJournal(accountName, instrument, rows, utcNow);

                    var isConsistent = _ownershipLedger.AssertDeterministicRestore(accountName, instrument, rows);
                    if (!isConsistent)
                    {
                        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                            eventType: "OWNERSHIP_RESTORE_DETERMINISM_FAILURE", state: "ENGINE",
                            new
                            {
                                instrument,
                                row_count = rows.Count,
                                source = "interim_timestamp_fallback",
                                note = "CRITICAL: non-deterministic restore from interim ordering"
                            }));
                    }
                }
            }
        }

        // Phase 3: Restore still-open orphan fills from the orphan journal.
        // Deduplication by BrokerOrderId against entries already in the ledger.
        int orphansRestored = 0;
        if (_orphanFillJournal != null)
        {
            try
            {
                var orphanTradingDate = TradingDateString ?? utcNow.ToString("yyyy-MM-dd");
                var openOrphans = _orphanFillJournal.ReadOrphanFills(orphanTradingDate);

                var knownBrokerOrderIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kv in openByInstrument)
                    foreach (var (_, _, _, entry) in kv.Value)
                        if (!string.IsNullOrEmpty(entry.BrokerOrderId))
                            knownBrokerOrderIds.Add(entry.BrokerOrderId);

                foreach (var orphan in openOrphans)
                {
                    if (knownBrokerOrderIds.Contains(orphan.BrokerOrderId))
                        continue;

                    var dir = orphan.Direction != default ? orphan.Direction : SlotDirection.Long;
                    _ownershipLedger.RecordOrphanFill(
                        accountName, orphan.Instrument.Trim(), orphan.BrokerOrderId,
                        dir, orphan.FillQty, utcNow, orphan.OrphanReason);
                    knownBrokerOrderIds.Add(orphan.BrokerOrderId);
                    orphansRestored++;
                }
            }
            catch (Exception ex)
            {
                LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString,
                    eventType: "ORPHAN_FILL_RESTORE_ERROR", state: "ENGINE",
                    new { error = ex.Message, note = "Failed to restore orphan fills from journal" }));
            }
        }

        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "OWNERSHIP_LEDGER_RESTORED", state: "ENGINE",
            new
            {
                instrument_count = openByInstrument.Count,
                total_rows = openByInstrument.Values.Sum(v => v.Count),
                orphans_restored = orphansRestored,
                note = "Canonical ownership ledger restored from journal (dual-run)"
            }));

        _stateEmitter?.EmitRestoreBaseline();
    }

    /// <summary>
    /// INTERIM: Parse a deterministic ordering key from journal entry timestamps.
    /// Uses EntryFilledAtUtc for normal entries, RecoveryTimestampUtc for orphan/recovered entries.
    /// Returns UTC ticks, or long.MaxValue if unparseable (sorts last).
    /// Will be replaced by durable OwnershipEventSequence in Phase 6.
    /// </summary>
    private static long ParseRestoreSequence(ExecutionJournalEntry entry, bool isOrphan)
    {
        var ts = isOrphan ? entry.RecoveryTimestampUtc : entry.EntryFilledAtUtc;
        if (!string.IsNullOrEmpty(ts) && DateTimeOffset.TryParse(ts, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dto))
            return dto.UtcTicks;
        if (!string.IsNullOrEmpty(entry.EntryFilledAtUtc) && DateTimeOffset.TryParse(entry.EntryFilledAtUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dto2))
            return dto2.UtcTicks;
        return long.MaxValue;
    }

    private static readonly HashSet<OwnershipEventKind> _immediateSnapshotKinds = new()
    {
        OwnershipEventKind.OrphanFill,
        OwnershipEventKind.InvariantViolation,
        OwnershipEventKind.ExitOverflow,
        OwnershipEventKind.OwnershipConflictRejected,
        OwnershipEventKind.TransferRejected
    };

    private static readonly HashSet<OwnershipEventKind> _coalescedSnapshotKinds = new()
    {
        OwnershipEventKind.MappedEntryFill,
        OwnershipEventKind.MappedExitFill
    };

    private void HandleOwnershipClassAEvent(OwnershipEvent evt)
    {
        try
        {
            LogEvent(RobotEvents.EngineBase(evt.Utc, tradingDate: TradingDateString,
                eventType: $"OWNERSHIP_CLASS_A_{evt.Kind}", state: "ENGINE",
                new
                {
                    kind = evt.Kind.ToString(),
                    instrument = evt.ExecutionInstrumentKey,
                    intent_id = evt.IntentId,
                    qty_delta = evt.QtyDelta,
                    direction = evt.Direction.ToString(),
                    ownership_version = evt.OwnershipVersion,
                    detail = evt.Detail
                }));

            QueueOwnershipClassASideEffects(evt);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_CLASS_A_HANDLER_ERROR", "ENGINE",
                new { error = ex.Message, kind = evt.Kind.ToString() }));
        }
    }

    private void QueueOwnershipClassASideEffects(OwnershipEvent evt)
    {
        try
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DispatchOwnershipClassASideEffects(evt);
                }
                catch (Exception ex)
                {
                    _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_CLASS_A_SIDE_EFFECT_ERROR", "ENGINE",
                        new { error = ex.Message, kind = evt.Kind.ToString() }));
                }
            });
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_CLASS_A_SIDE_EFFECT_ENQUEUE_ERROR", "ENGINE",
                new { error = ex.Message, kind = evt.Kind.ToString() }));
        }
    }

    private void DispatchOwnershipClassASideEffects(OwnershipEvent evt)
    {
        if (_immediateSnapshotKinds.Contains(evt.Kind))
        {
            var trigger = evt.Kind switch
            {
                OwnershipEventKind.OrphanFill => SnapshotTrigger.OrphanFill,
                OwnershipEventKind.ExitOverflow => SnapshotTrigger.ExitOverflow,
                OwnershipEventKind.OwnershipConflictRejected => SnapshotTrigger.OwnershipConflictRejected,
                OwnershipEventKind.TransferRejected => SnapshotTrigger.TransferRejected,
                _ => SnapshotTrigger.SupervisorEscalation
            };
            _stateEmitter?.NotifyImmediateTrigger(trigger);
        }
        else if (_coalescedSnapshotKinds.Contains(evt.Kind))
        {
            _stateEmitter?.NotifyCoalescedTrigger(SnapshotTrigger.Fill);
        }

        PersistClassAEvent(evt);
    }

    private void HandleOwnershipClassBEvent(OwnershipEvent evt)
    {
        LogEvent(RobotEvents.EngineBase(evt.Utc, tradingDate: TradingDateString,
            eventType: $"OWNERSHIP_CLASS_B_{evt.Kind}", state: "ENGINE",
            new
            {
                kind = evt.Kind.ToString(),
                instrument = evt.ExecutionInstrumentKey,
                ownership_version = evt.OwnershipVersion,
                detail = evt.Detail
            }));
    }

    private void PersistClassAEvent(OwnershipEvent evt)
    {
        try
        {
            var td = TradingDateString;
            if (string.IsNullOrEmpty(td)) td = evt.Utc.ToString("yyyy-MM-dd");
            var dir = System.IO.Path.Combine(_persistenceBase, "events", "ownership_events", td);
            System.IO.Directory.CreateDirectory(dir);
            var filePath = System.IO.Path.Combine(dir, "class_a.jsonl");
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = evt.Kind.ToString(),
                event_class = evt.EventClass.ToString(),
                account = evt.Account,
                instrument = evt.ExecutionInstrumentKey,
                ownership_version = evt.OwnershipVersion,
                intent_id = evt.IntentId,
                from_intent_id = evt.FromIntentId,
                to_intent_id = evt.ToIntentId,
                qty_delta = evt.QtyDelta,
                direction = evt.Direction.ToString(),
                orphan_reason = evt.OrphanReason.ToString(),
                utc = evt.Utc.ToString("o"),
                detail = evt.Detail
            });
            System.IO.File.AppendAllText(filePath, json + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, "", "OWNERSHIP_CLASS_A_PERSIST_ERROR", "ENGINE",
                new { error = ex.Message }));
        }
    }

    /// <summary>
    private bool RecordOwnershipBrokerFlatJournalClose(
        string tradingDate,
        string stream,
        string intentId,
        string instrument,
        int journalOpenBefore,
        DateTimeOffset utcNow,
        string note)
    {
        if (_ownershipLedger == null || journalOpenBefore <= 0 || string.IsNullOrWhiteSpace(intentId) || string.IsNullOrWhiteSpace(instrument))
            return false;

        var account = OwnershipAccountKey;
        var snapshot = _ownershipLedger.GetOwnershipSnapshot(account, instrument);
        var remaining = snapshot?.Slots
            .Where(s => string.Equals(s.IntentId, intentId, StringComparison.OrdinalIgnoreCase) && s.Remaining > 0)
            .Sum(s => s.Remaining) ?? 0;

        if (remaining <= 0)
        {
            LogEvent(RobotEvents.EngineBase(utcNow, tradingDate, "OWNERSHIP_RECONCILIATION_BROKER_FLAT_CLOSE", "ENGINE",
                new
                {
                    account,
                    instrument,
                    stream,
                    intent_id = intentId,
                    qty_delta = 0,
                    journal_open_qty_before = journalOpenBefore,
                    success = true,
                    skipped = true,
                    skip_reason = "already_retired",
                    ownership_version = snapshot?.OwnershipVersion ?? 0,
                    note = note + "; ownership mirror skipped because slot was already retired"
                }));
            return true;
        }

        var qtyToClose = Math.Min(journalOpenBefore, remaining);
        var result = _ownershipLedger.RecordMappedExitFill(account, instrument, intentId, qtyToClose, utcNow, 0);
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate, "OWNERSHIP_RECONCILIATION_BROKER_FLAT_CLOSE", "ENGINE",
            new
            {
                account,
                instrument,
                stream,
                intent_id = intentId,
                qty_delta = qtyToClose,
                journal_open_qty_before = journalOpenBefore,
                ownership_remaining_before = remaining,
                success = result.Success,
                skipped = false,
                error = result.ErrorReason ?? "",
                ownership_version = result.Snapshot?.OwnershipVersion ?? snapshot?.OwnershipVersion ?? 0,
                note
            }));
        return result.Success;
    }

    /// <summary>

    /// Check if any stream for this instrument has slot journal with RANGE_LOCKED + StopBracketsSubmittedAtLock.
    /// Used at bootstrap to ADOPT working entry stops on NinjaTrader restart instead of flattening them as orphans.
    /// </summary>
    internal bool HasSlotJournalWithEntryStopsForInstrument(string instrument)
    {
        lock (_engineLock)
        {
            if (!_activeTradingDate.HasValue || _streams == null || _streams.Count == 0)
                return false;

            var canonicalOfExecution = GetCanonicalInstrument(instrument);
            var tradingDateStr = _activeTradingDate.Value.ToString("yyyy-MM-dd");

            foreach (var stream in _streams.Values)
            {
                if (!string.Equals(stream.CanonicalInstrument, canonicalOfExecution, StringComparison.OrdinalIgnoreCase))
                    continue;

                var journal = _journals.TryLoad(tradingDateStr, stream.Stream);
                if (journal != null &&
                    string.Equals(journal.LastState, "RANGE_LOCKED", StringComparison.OrdinalIgnoreCase) &&
                    journal.StopBracketsSubmittedAtLock)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
