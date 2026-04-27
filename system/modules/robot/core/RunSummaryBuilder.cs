using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core;

/// <summary>
/// Derives <see cref="RunRootArtifacts.SummaryFileName"/> from KEY_EVENTS (primary), durable decision JSONL (secondary),
/// robot log severity counts, and <see cref="ExecutionSummarySnapshot"/> (tertiary).
/// </summary>
public static class RunSummaryBuilder
{
    private const long RecoveredMismatchWarnThresholdMs = 30_000;

    public static RunSummaryDocument Build(
        string persistenceBase,
        string? fullRunId,
        DateTimeOffset engineStartUtc,
        ExecutionMode mode,
        IReadOnlyList<string> instruments,
        ExecutionSummarySnapshot execSnap)
    {
        var ymd = RunDirectoryNaming.ChicagoCalendarDateYyyyMmDd(engineStartUtc);
        var modeLabel = RunDirectoryNaming.ModeLabel(mode);
        var instList = instruments?.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.Ordinal)
            .ToList() ?? new List<string>();

        var keyPath = Path.Combine(persistenceBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            RunRootArtifacts.KeyEventsFileName);
        var keyEventsPresent = File.Exists(keyPath);

        var agg = new KeyEventAggregate();
        if (keyEventsPresent)
            TryParseKeyEventsJsonl(keyPath, agg);

        TryParseMismatchDecisions(persistenceBase, agg, keyEventsPresent);
        TryParseFlattenCompletions(persistenceBase, agg, keyEventsPresent);
        TryParseOpenExposureAtShutdown(persistenceBase, agg);
        TryParseIncompleteStreamJournalsAtShutdown(persistenceBase, agg);
        TryParseRobotLogSeverityCounts(persistenceBase, agg);

        ApplyTertiaryExecutionSummary(execSnap, agg, keyEventsPresent);

        var fail = ComputeFail(agg);
        var warn = !fail && ComputeWarn(agg);
        string status;
        if (fail) status = "FAIL";
        else if (warn) status = "WARN";
        else status = "OK";

        var reason = PickStatusReason(agg);

        if (!keyEventsPresent && status == "OK")
        {
            status = "WARN";
            if (reason == "NORMAL_RUN")
                reason = "KEY_EVENTS_MISSING";
        }

        var (recommendedAction, confidence) = ComputeRecommendedAction(status, reason, !keyEventsPresent);

        return new RunSummaryDocument
        {
            run_id = fullRunId ?? "",
            date = ymd,
            mode = modeLabel,
            status = status,
            status_reason = reason,
            recommended_action = recommendedAction,
            confidence = confidence,
            instruments = instList,
            trades = agg.CompletedFilledTradeJournalCount > 0
                ? agg.CompletedFilledTradeJournalCount
                : Math.Max(execSnap.OrdersFilled, agg.UniqueEntryFilledIntentCount > 0 ? agg.UniqueEntryFilledIntentCount : agg.EntryFilled),
            pnl = null,
            errors = execSnap.OrdersRejected +
                     execSnap.OrdersBlocked +
                     agg.RobotLogErrorCount +
                     agg.RobotLogCriticalCount +
                     agg.CompletedRejectedJournalCount +
                     agg.IncompleteStreamJournalCount +
                     (agg.HasOpenExposureAtShutdown ? 1 : 0),
            key_counts = new RunSummaryKeyCounts
            {
                mismatch_block_enter = agg.MismatchEnter,
                mismatch_block_exit = agg.MismatchExit,
                mismatch_max_duration_ms = agg.MaxResolvedMismatchDurationMs,
                flatten_confirmed = agg.FlattenConfirmed,
                recovery_started = agg.RecoveryStarted,
                recovery_complete = agg.RecoveryComplete,
                execution_blocked = agg.ExecutionBlocked,
                entry_rejected = agg.EntryRejected,
                protective_failed = agg.ProtectiveFailed,
                commit_persist_failed = agg.CommitPersistFailed,
                open_position_at_shutdown = agg.OpenJournalIntentCount,
                open_position_qty_at_shutdown = agg.OpenJournalQty,
                submitted_intents_at_shutdown = agg.OpenSubmittedIntentCount,
                broker_position_qty_at_shutdown = agg.OpenBrokerAbsQty,
                broker_working_orders_at_shutdown = agg.OpenBrokerWorkingOrders,
                ownership_active_slots_at_shutdown = agg.OwnershipActiveSlots,
                ownership_orphan_slots_at_shutdown = agg.OwnershipOrphanSlots,
                ownership_journal_open_qty_at_shutdown = agg.OwnershipJournalOpenQty,
                unique_entry_filled_intents = agg.UniqueEntryFilledIntentCount,
                completed_filled_trade_journals = agg.CompletedFilledTradeJournalCount,
                completed_rejected_journals = agg.CompletedRejectedJournalCount,
                robot_log_errors = agg.RobotLogErrorCount,
                robot_log_critical = agg.RobotLogCriticalCount,
                incomplete_streams_at_shutdown = agg.IncompleteStreamJournalCount
            },
            flags = new RunSummaryFlags
            {
                had_mismatch_block = agg.MismatchEnter > 0,
                had_recovery = agg.RecoveryStarted > 0 || agg.RecoveryComplete > 0,
                had_flatten = agg.FlattenActivity,
                had_commit_failure = agg.CommitPersistFailed > 0,
                had_execution_block = agg.ExecutionBlocked > 0 || execSnap.OrdersBlocked > 0,
                had_order_rejection = agg.EntryRejected > 0 || execSnap.OrdersRejected > 0,
                had_protective_failure = agg.ProtectiveFailed > 0,
                had_open_exposure_at_shutdown = agg.HasOpenExposureAtShutdown,
                had_completed_rejected_journal = agg.CompletedRejectedJournalCount > 0,
                had_robot_log_error = agg.RobotLogErrorCount > 0 || agg.RobotLogCriticalCount > 0,
                had_incomplete_streams_at_shutdown = agg.IncompleteStreamJournalCount > 0
            }
        };
    }

    private sealed class KeyEventAggregate
    {
        public int MismatchEnter;
        public int MismatchExit;
        public int FlattenConfirmed;
        public int RecoveryStarted;
        public int RecoveryComplete;
        public int ExecutionBlocked;
        public int EntryRejected;
        public int ProtectiveFailed;
        public int ProtectiveSubmitted;
        public int CommitPersistFailed;
        public int EntryFilled;
        public int CompletedFilledTradeJournalCount;
        public int CompletedRejectedJournalCount;
        public int RobotLogErrorCount;
        public int RobotLogCriticalCount;
        public int OpenJournalIntentCount;
        public int OpenJournalQty;
        public int OpenSubmittedIntentCount;
        public int OpenBrokerAbsQty;
        public int OpenBrokerWorkingOrders;
        public int OwnershipActiveSlots;
        public int OwnershipOrphanSlots;
        public int OwnershipJournalOpenQty;
        public long MaxResolvedMismatchDurationMs;
        public int IncompleteStreamJournalCount => IncompleteStreamJournalKeys.Count;

        public readonly Dictionary<string, int> MismatchDepth = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTimeOffset> MismatchOpenSince = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> FlattenPending = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> FlattenConfirmedKeys = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> EntryFilledIntentIds = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> IncompleteStreamJournalKeys = new(StringComparer.OrdinalIgnoreCase);

        public bool FlattenActivity;
        public bool TertiaryEntryReject;
        public bool TertiaryExecutionBlock;
        public int UniqueEntryFilledIntentCount => EntryFilledIntentIds.Count;

        public bool HasMismatchBlocks => MismatchEnter > 0 || MismatchExit > 0;
        public bool HasLongRecoveredMismatch => MaxResolvedMismatchDurationMs > RecoveredMismatchWarnThresholdMs;
        public bool HasOpenExposureAtShutdown =>
            OpenJournalIntentCount > 0 ||
            OpenJournalQty > 0 ||
            OpenSubmittedIntentCount > 0 ||
            OpenBrokerAbsQty > 0 ||
            OpenBrokerWorkingOrders > 0 ||
            OwnershipActiveSlots > 0 ||
            OwnershipOrphanSlots > 0 ||
            OwnershipJournalOpenQty > 0;
    }

    private static void ApplyTertiaryExecutionSummary(ExecutionSummarySnapshot execSnap, KeyEventAggregate agg, bool keyEventsPresent)
    {
        if (execSnap.OrdersRejected > 0 && (!keyEventsPresent || agg.EntryRejected == 0))
            agg.TertiaryEntryReject = true;
        if (execSnap.OrdersBlocked > 0 && (!keyEventsPresent || agg.ExecutionBlocked == 0))
            agg.TertiaryExecutionBlock = true;
    }

    private static bool ComputeFail(KeyEventAggregate agg)
    {
        if (agg.RobotLogErrorCount > 0 || agg.RobotLogCriticalCount > 0) return true;
        if (agg.CompletedRejectedJournalCount > 0) return true;
        if (agg.IncompleteStreamJournalCount > 0) return true;
        if (agg.CommitPersistFailed > 0) return true;
        if (agg.ProtectiveFailed > 0) return true;
        if (agg.EntryRejected > 0 || agg.TertiaryEntryReject) return true;
        if (agg.HasOpenExposureAtShutdown) return true;

        foreach (var kv in agg.MismatchDepth)
        {
            if (kv.Value > 0) return true;
        }

        foreach (var kv in agg.FlattenPending)
        {
            if (kv.Value) return true;
        }

        if (agg.RecoveryStarted > agg.RecoveryComplete) return true;

        return false;
    }

    private static bool ComputeWarn(KeyEventAggregate agg)
    {
        if (agg.ExecutionBlocked > 0 || agg.TertiaryExecutionBlock) return true;
        if (agg.HasLongRecoveredMismatch) return true;
        if (agg.RecoveryComplete > 0) return true;
        if (agg.FlattenActivity) return true;
        return false;
    }

    private static string PickStatusReason(KeyEventAggregate agg)
    {
        if (agg.RobotLogErrorCount > 0) return "ROBOT_LOG_ERROR";
        if (agg.RobotLogCriticalCount > 0) return "ROBOT_LOG_CRITICAL";
        if (agg.CompletedRejectedJournalCount > 0) return "COMPLETED_REJECTED_JOURNAL";
        if (agg.IncompleteStreamJournalCount > 0) return "STREAM_INCOMPLETE_AT_SHUTDOWN";
        if (agg.CommitPersistFailed > 0) return "COMMIT_PERSIST_FAILED";
        if (agg.ProtectiveFailed > 0) return "PROTECTIVE_FAILED";
        if (agg.EntryRejected > 0 || agg.TertiaryEntryReject) return "ENTRY_REJECTED";
        if (agg.HasOpenExposureAtShutdown) return "OPEN_EXPOSURE_AT_SHUTDOWN";
        foreach (var kv in agg.MismatchDepth)
            if (kv.Value > 0) return "MISMATCH_BLOCK_ACTIVE_AT_END";
        if (agg.RecoveryStarted > agg.RecoveryComplete) return "RECOVERY_INCOMPLETE";
        foreach (var kv in agg.FlattenPending)
            if (kv.Value) return "FLATTEN_NOT_CONFIRMED";

        if (agg.HasLongRecoveredMismatch) return "MISMATCH_BLOCK_LONG_RECOVERED";
        if (agg.RecoveryComplete > 0 || agg.RecoveryStarted > 0) return "RECOVERY_OCCURRED";
        if (agg.ExecutionBlocked > 0 || agg.TertiaryExecutionBlock) return "EXECUTION_BLOCKED_OCCURRED";
        if (agg.FlattenActivity) return "FLATTEN_OCCURRED";
        if (agg.HasMismatchBlocks) return "MISMATCH_BLOCK_RECOVERED";

        return "NORMAL_RUN";
    }

    /// <summary>Deterministic operator hint — not authority; same mapping as Watchdog read-through display.</summary>
    private static (string action, string confidence) ComputeRecommendedAction(string status, string reason, bool keyEventsMissing)
    {
        var r = reason ?? "";
        if (r == "KEY_EVENTS_MISSING")
            return ("MONITOR", "LOW");

        switch (r)
        {
            case "NORMAL_RUN":
                return status == "OK" ? ("CONTINUE", "HIGH") : ("MONITOR", "MEDIUM");
            case "MISMATCH_BLOCK_RECOVERED":
                return ("CONTINUE", "HIGH");
            case "RECOVERY_OCCURRED":
            case "MISMATCH_BLOCK_OCCURRED":
            case "MISMATCH_BLOCK_LONG_RECOVERED":
            case "FLATTEN_OCCURRED":
                return ("MONITOR", "MEDIUM");
            case "EXECUTION_BLOCKED_OCCURRED":
                return ("PAUSE", "MEDIUM");
            case "COMMIT_PERSIST_FAILED":
            case "MISMATCH_BLOCK_ACTIVE_AT_END":
            case "RECOVERY_INCOMPLETE":
            case "FLATTEN_NOT_CONFIRMED":
            case "PROTECTIVE_FAILED":
            case "OPEN_EXPOSURE_AT_SHUTDOWN":
            case "ROBOT_LOG_ERROR":
            case "ROBOT_LOG_CRITICAL":
            case "COMPLETED_REJECTED_JOURNAL":
            case "STREAM_INCOMPLETE_AT_SHUTDOWN":
                return ("STOP", "HIGH");
            case "ENTRY_REJECTED":
                return ("PAUSE", "HIGH");
            default:
                if (status == "FAIL") return ("STOP", "MEDIUM");
                if (status == "WARN") return ("MONITOR", "MEDIUM");
                return ("CONTINUE", "HIGH");
        }
    }

    private static void TryParseKeyEventsJsonl(string path, KeyEventAggregate agg)
    {
        string? line;
        try
        {
            using var sr = new StreamReader(path);
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var d = JsonUtil.Deserialize<Dictionary<string, object>>(line);
                    if (d == null || !d.TryGetValue("event", out var evObj) || evObj == null) continue;
                    var ev = evObj.ToString() ?? "";
                    if (string.IsNullOrEmpty(ev)) continue;

                    d.TryGetValue("instrument", out var instObj);
                    var inst = NormInst(instObj?.ToString());
                    var tsUtc = TryGetTimestampUtc(d);

                    switch (ev)
                    {
                        case "MISMATCH_BLOCK_ENTER":
                            agg.MismatchEnter++;
                            BumpMismatchDepth(agg, inst, +1);
                            if (!string.IsNullOrEmpty(inst) && tsUtc.HasValue)
                                agg.MismatchOpenSince[inst] = tsUtc.Value;
                            break;
                        case "MISMATCH_BLOCK_EXIT":
                            agg.MismatchExit++;
                            TrackResolvedMismatchDuration(agg, inst, tsUtc);
                            BumpMismatchDepth(agg, inst, -1);
                            break;
                        case "ENTRY_FILLED":
                            agg.EntryFilled++;
                            var filledIntentId = TryGetDataString(d, "intent_id");
                            if (!string.IsNullOrWhiteSpace(filledIntentId))
                                agg.EntryFilledIntentIds.Add(filledIntentId.Trim());
                            break;
                        case "FLATTEN_CONFIRMED":
                            if (MarkFlattenConfirmedOnce(agg, inst, tsUtc))
                                agg.FlattenConfirmed++;
                            agg.FlattenActivity = true;
                            ClearFlattenPending(agg, inst);
                            break;
                        case "FLATTEN_REQUESTED":
                        case "FLATTEN_SUBMITTED":
                            agg.FlattenActivity = true;
                            SetFlattenPending(agg, inst, true);
                            break;
                        case "RECOVERY_STARTED":
                            agg.RecoveryStarted++;
                            break;
                        case "RECOVERY_COMPLETE":
                            agg.RecoveryComplete++;
                            break;
                        case "EXECUTION_BLOCKED":
                            agg.ExecutionBlocked++;
                            break;
                        case "ENTRY_REJECTED":
                            agg.EntryRejected++;
                            break;
                        case "PROTECTIVE_FAILED":
                            agg.ProtectiveFailed++;
                            break;
                        case "PROTECTIVE_SUBMITTED":
                            agg.ProtectiveSubmitted++;
                            break;
                        case "STREAM_COMMIT_PERSIST_FAILED":
                            agg.CommitPersistFailed++;
                            break;
                    }
                }
                catch
                {
                    // unknown line shape — ignore
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryParseMismatchDecisions(string persistenceBase, KeyEventAggregate agg, bool keyEventsPresent)
    {
        if (keyEventsPresent)
            return;

        var p = Path.Combine(RobotRunArtifactPaths.DecisionsDir(persistenceBase), "mismatch_execution_block_decisions.jsonl");
        if (!File.Exists(p)) return;
        try
        {
            string? line;
            using var sr = new StreamReader(p);
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonUtil.Deserialize<MismatchDecisionJsonlLine>(line);
                    if (r == null || string.IsNullOrWhiteSpace(r.Kind)) continue;
                    var inst = NormInst(r.Instrument);
                    if (string.IsNullOrEmpty(inst)) continue;
                    var k = r.Kind.Trim().ToUpperInvariant();
                    if (k == "ENTER")
                    {
                        agg.MismatchEnter++;
                        BumpMismatchDepth(agg, inst, +1);
                    }
                    else if (k == "EXIT")
                    {
                        agg.MismatchExit++;
                        BumpMismatchDepth(agg, inst, -1);
                    }
                }
                catch
                {
                    // ignore bad line
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryParseFlattenCompletions(string persistenceBase, KeyEventAggregate agg, bool keyEventsPresent)
    {
        var p = Path.Combine(RobotRunArtifactPaths.DecisionsDir(persistenceBase), "flatten_broker_flat_completions.jsonl");
        if (!File.Exists(p)) return;
        try
        {
            string? line;
            using var sr = new StreamReader(p);
            while ((line = sr.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonUtil.Deserialize<FlattenCompletionJsonlLine>(line);
                    if (r == null) continue;
                    var inst = NormInst(r.Instrument);
                    if (string.IsNullOrEmpty(inst)) continue;
                    if (!keyEventsPresent && MarkFlattenConfirmedOnce(agg, inst, null))
                        agg.FlattenConfirmed++;
                    agg.FlattenActivity = true;
                    ClearFlattenPending(agg, inst);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryParseOpenExposureAtShutdown(string persistenceBase, KeyEventAggregate agg)
    {
        TryParseOpenExecutionJournalsAtShutdown(persistenceBase, agg);
        TryParseLatestOwnershipSnapshotsAtShutdown(persistenceBase, agg);
    }

    private static void TryParseIncompleteStreamJournalsAtShutdown(string persistenceBase, KeyEventAggregate agg)
    {
        var dir = RobotRunArtifactPaths.StateStreamJournals(persistenceBase);
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith("_", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    var journal = JsonUtil.Deserialize<StreamJournalShutdownLine>(json);
                    if (journal == null || string.IsNullOrWhiteSpace(journal.Stream)) continue;

                    if (!journal.Committed)
                    {
                        var key = string.IsNullOrWhiteSpace(journal.SlotInstanceKey)
                            ? journal.Stream.Trim()
                            : journal.SlotInstanceKey.Trim();
                        agg.IncompleteStreamJournalKeys.Add(key);
                    }
                }
                catch
                {
                    // best-effort shutdown invariant; malformed journals should not block summary writes
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryParseRobotLogSeverityCounts(string persistenceBase, KeyEventAggregate agg)
    {
        var dir = Path.Combine(persistenceBase ?? "", "logs", "robot");
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "robot_*.jsonl", SearchOption.TopDirectoryOnly))
            {
                string? line;
                using var sr = new StreamReader(path);
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var d = JsonUtil.Deserialize<Dictionary<string, object>>(line);
                        if (d == null || !d.TryGetValue("level", out var levelObj)) continue;
                        var level = (levelObj?.ToString() ?? "").Trim();
                        if (level.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                            agg.RobotLogErrorCount++;
                        else if (level.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase))
                            agg.RobotLogCriticalCount++;
                    }
                    catch
                    {
                        // best-effort summary signal
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryParseOpenExecutionJournalsAtShutdown(string persistenceBase, KeyEventAggregate agg)
    {
        var dir = RobotRunArtifactPaths.StateExecutionJournals(persistenceBase);
        if (!Directory.Exists(dir)) return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(json);
                    if (entry == null) continue;

                    if (entry.EntryFilled && entry.TradeCompleted)
                    {
                        agg.CompletedFilledTradeJournalCount++;
                        if (entry.Rejected)
                            agg.CompletedRejectedJournalCount++;
                    }

                    if (entry.TradeCompleted) continue;

                    if (entry.EntryFilled)
                    {
                        var entryQty = entry.EntryFilledQuantityTotal > 0
                            ? entry.EntryFilledQuantityTotal
                            : Math.Max(0, entry.FillQuantity ?? 0);
                        var remaining = Math.Max(0, entryQty - Math.Max(0, entry.ExitFilledQuantityTotal));
                        if (remaining <= 0) continue;

                        agg.OpenJournalIntentCount++;
                        agg.OpenJournalQty += remaining;
                    }
                    else if (entry.EntrySubmitted && !entry.Rejected)
                    {
                        agg.OpenSubmittedIntentCount++;
                    }
                }
                catch
                {
                    // best-effort shutdown invariant; malformed journals should not block summary writes
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryParseLatestOwnershipSnapshotsAtShutdown(string persistenceBase, KeyEventAggregate agg)
    {
        var root = Path.Combine(persistenceBase ?? "", "events", "ownership_snapshots");
        if (!Directory.Exists(root)) return;

        var shutdownUtc = TryReadRunShutdownUtc(persistenceBase);
        var latest = new Dictionary<string, LatestOwnershipSnapshot>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                string? line;
                using var sr = new StreamReader(path);
                while ((line = sr.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var snapshot = JsonUtil.Deserialize<OwnershipSnapshotJsonlLine>(line);
                        if (snapshot?.Instruments == null || snapshot.Instruments.Count == 0) continue;

                        foreach (var inst in snapshot.Instruments)
                        {
                            var instrument = NormInst(inst.Instrument);
                            if (string.IsNullOrEmpty(instrument)) continue;

                            var snapshotUtc = TryParseDateTimeOffset(inst.SnapshotUtc) ??
                                              TryParseDateTimeOffset(snapshot.EmittedUtc) ??
                                              DateTimeOffset.MinValue;
                            if (!IsFreshShutdownSnapshot(snapshotUtc, shutdownUtc)) continue;

                            var candidate = new LatestOwnershipSnapshot
                            {
                                TimestampUtc = snapshotUtc,
                                SnapshotSequence = inst.SnapshotSequence,
                                Instrument = inst
                            };

                            if (!latest.TryGetValue(instrument, out var existing) ||
                                IsNewerOwnershipSnapshot(candidate, existing))
                            {
                                latest[instrument] = candidate;
                            }
                        }
                    }
                    catch
                    {
                        // ignore malformed snapshot line
                    }
                }
            }
        }
        catch
        {
            // best-effort
        }

        foreach (var snapshot in latest.Values)
        {
            var inst = snapshot.Instrument;
            agg.OpenBrokerAbsQty += Math.Abs(inst.BrokerPositionQty);
            agg.OpenBrokerWorkingOrders += Math.Max(0, inst.BrokerWorkingOrderCount);
            agg.OwnershipActiveSlots += Math.Max(0, inst.ActiveSlotCount);
            agg.OwnershipOrphanSlots += Math.Max(0, inst.OrphanSlotCount);
            agg.OwnershipJournalOpenQty += Math.Max(0, inst.JournalOpenQty);
        }
    }

    private static bool IsNewerOwnershipSnapshot(LatestOwnershipSnapshot candidate, LatestOwnershipSnapshot existing)
    {
        if (candidate.TimestampUtc > existing.TimestampUtc) return true;
        if (candidate.TimestampUtc < existing.TimestampUtc) return false;
        return candidate.SnapshotSequence > existing.SnapshotSequence;
    }

    private static bool IsFreshShutdownSnapshot(DateTimeOffset snapshotUtc, DateTimeOffset? shutdownUtc)
    {
        if (!shutdownUtc.HasValue || snapshotUtc == DateTimeOffset.MinValue)
            return true;

        return shutdownUtc.Value - snapshotUtc <= TimeSpan.FromMinutes(5);
    }

    private static DateTimeOffset? TryReadRunShutdownUtc(string persistenceBase)
    {
        try
        {
            var path = Path.Combine(persistenceBase ?? "", RunRootArtifacts.RunShutdownSignalFileName);
            if (!File.Exists(path)) return null;

            var d = JsonUtil.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (d != null && d.TryGetValue("ts_utc", out var tsObj))
                return TryParseDateTimeOffset(tsObj?.ToString());
        }
        catch
        {
            // best-effort
        }

        return null;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value.Trim(), out var ts) ? ts : null;
    }

    private static string NormInst(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant();

    private static DateTimeOffset? TryGetTimestampUtc(Dictionary<string, object> d)
    {
        if (!d.TryGetValue("ts_utc", out var tsObj) || tsObj == null)
            return null;
        return DateTimeOffset.TryParse(tsObj.ToString(), out var ts) ? ts : null;
    }

    private static string? TryGetDataString(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue("data", out var dataObj) || dataObj == null)
            return null;
        if (dataObj is Dictionary<string, object> data &&
            data.TryGetValue(key, out var value) &&
            value != null)
            return value.ToString();
        if (dataObj is System.Text.Json.JsonElement dataElement &&
            dataElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
            dataElement.TryGetProperty(key, out var property))
        {
            return property.ValueKind == System.Text.Json.JsonValueKind.String
                ? property.GetString()
                : property.ToString();
        }
        return null;
    }

    private static void TrackResolvedMismatchDuration(KeyEventAggregate agg, string inst, DateTimeOffset? exitUtc)
    {
        if (string.IsNullOrEmpty(inst) || !exitUtc.HasValue)
            return;
        if (!agg.MismatchOpenSince.TryGetValue(inst, out var enterUtc))
            return;
        var durationMs = (long)(exitUtc.Value - enterUtc).TotalMilliseconds;
        if (durationMs > agg.MaxResolvedMismatchDurationMs)
            agg.MaxResolvedMismatchDurationMs = durationMs;
        agg.MismatchOpenSince.Remove(inst);
    }

    private static bool MarkFlattenConfirmedOnce(KeyEventAggregate agg, string inst, DateTimeOffset? tsUtc)
    {
        var keyInst = string.IsNullOrWhiteSpace(inst) ? "__all__" : inst.Trim().ToUpperInvariant();
        var keyTs = tsUtc.HasValue
            ? tsUtc.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            : "no_ts";
        return agg.FlattenConfirmedKeys.Add($"{keyInst}|{keyTs}");
    }

    private static void BumpMismatchDepth(KeyEventAggregate agg, string inst, int delta)
    {
        if (string.IsNullOrEmpty(inst)) return;
        if (!agg.MismatchDepth.TryGetValue(inst, out var v)) v = 0;
        v += delta;
        if (v < 0) v = 0;
        agg.MismatchDepth[inst] = v;
    }

    private static void SetFlattenPending(KeyEventAggregate agg, string inst, bool pending)
    {
        if (string.IsNullOrEmpty(inst))
        {
            foreach (var k in agg.FlattenPending.Keys.ToList())
                agg.FlattenPending[k] = pending;
            return;
        }

        agg.FlattenPending[inst] = pending;
    }

    private static void ClearFlattenPending(KeyEventAggregate agg, string inst)
    {
        if (string.IsNullOrEmpty(inst))
        {
            foreach (var k in agg.FlattenPending.Keys.ToList())
                agg.FlattenPending[k] = false;
            return;
        }

        agg.FlattenPending[inst] = false;
    }

    private sealed class MismatchDecisionJsonlLine
    {
        public string Kind { get; set; } = "";
        public string Instrument { get; set; } = "";
    }

    private sealed class FlattenCompletionJsonlLine
    {
        public string Instrument { get; set; } = "";
    }

    private sealed class OwnershipSnapshotJsonlLine
    {
        public string EmittedUtc { get; set; } = "";
        public List<OwnershipSnapshotInstrumentLine> Instruments { get; set; } = new();
    }

    private sealed class OwnershipSnapshotInstrumentLine
    {
        public string Instrument { get; set; } = "";
        public int BrokerPositionQty { get; set; }
        public int BrokerWorkingOrderCount { get; set; }
        public int JournalOpenQty { get; set; }
        public int ActiveSlotCount { get; set; }
        public int OrphanSlotCount { get; set; }
        public long SnapshotSequence { get; set; }
        public string SnapshotUtc { get; set; } = "";
    }

    private sealed class LatestOwnershipSnapshot
    {
        public DateTimeOffset TimestampUtc { get; set; }
        public long SnapshotSequence { get; set; }
        public OwnershipSnapshotInstrumentLine Instrument { get; set; } = new();
    }

    private sealed class StreamJournalShutdownLine
    {
        public string TradingDate { get; set; } = "";
        public string Stream { get; set; } = "";
        public bool Committed { get; set; }
        public string LastState { get; set; } = "";
        public string LastUpdateUtc { get; set; } = "";
        public string SlotInstanceKey { get; set; } = "";
    }
}

/// <summary>Human-facing run summary (summary.json).</summary>
public sealed class RunSummaryDocument
{
    public string run_id { get; set; } = "";
    public string date { get; set; } = "";
    public string mode { get; set; } = "";
    public string status { get; set; } = "";
    public string status_reason { get; set; } = "";
    /// <summary>CONTINUE | MONITOR | PAUSE | STOP — operator guidance from status + status_reason.</summary>
    public string recommended_action { get; set; } = "";
    /// <summary>HIGH | MEDIUM | LOW</summary>
    public string confidence { get; set; } = "";
    public List<string> instruments { get; set; } = new();
    public int trades { get; set; }
    public decimal? pnl { get; set; }
    public int errors { get; set; }
    public RunSummaryKeyCounts key_counts { get; set; } = new();
    public RunSummaryFlags flags { get; set; } = new();
}

public sealed class RunSummaryKeyCounts
{
    public int mismatch_block_enter { get; set; }
    public int mismatch_block_exit { get; set; }
    public long mismatch_max_duration_ms { get; set; }
    public int flatten_confirmed { get; set; }
    public int recovery_started { get; set; }
    public int recovery_complete { get; set; }
    public int execution_blocked { get; set; }
    public int entry_rejected { get; set; }
    public int protective_failed { get; set; }
    public int commit_persist_failed { get; set; }
    public int open_position_at_shutdown { get; set; }
    public int open_position_qty_at_shutdown { get; set; }
    public int submitted_intents_at_shutdown { get; set; }
    public int broker_position_qty_at_shutdown { get; set; }
    public int broker_working_orders_at_shutdown { get; set; }
    public int ownership_active_slots_at_shutdown { get; set; }
    public int ownership_orphan_slots_at_shutdown { get; set; }
    public int ownership_journal_open_qty_at_shutdown { get; set; }
    public int unique_entry_filled_intents { get; set; }
    public int completed_filled_trade_journals { get; set; }
    public int completed_rejected_journals { get; set; }
    public int robot_log_errors { get; set; }
    public int robot_log_critical { get; set; }
    public int incomplete_streams_at_shutdown { get; set; }
}

public sealed class RunSummaryFlags
{
    public bool had_mismatch_block { get; set; }
    public bool had_recovery { get; set; }
    public bool had_flatten { get; set; }
    public bool had_commit_failure { get; set; }
    public bool had_execution_block { get; set; }
    public bool had_order_rejection { get; set; }
    public bool had_protective_failure { get; set; }
    public bool had_open_exposure_at_shutdown { get; set; }
    public bool had_completed_rejected_journal { get; set; }
    public bool had_robot_log_error { get; set; }
    public bool had_incomplete_streams_at_shutdown { get; set; }
}
