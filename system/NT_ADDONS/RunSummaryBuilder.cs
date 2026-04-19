using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core;

/// <summary>
/// Derives <see cref="RunRootArtifacts.SummaryFileName"/> from KEY_EVENTS (primary), durable decision JSONL (secondary),
/// and <see cref="ExecutionSummarySnapshot"/> (tertiary). No robot log parsing.
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
            trades = Math.Max(execSnap.OrdersFilled, agg.EntryFilled),
            pnl = null,
            errors = execSnap.OrdersRejected + execSnap.OrdersBlocked,
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
                commit_persist_failed = agg.CommitPersistFailed
            },
            flags = new RunSummaryFlags
            {
                had_mismatch_block = agg.MismatchEnter > 0,
                had_recovery = agg.RecoveryStarted > 0 || agg.RecoveryComplete > 0,
                had_flatten = agg.FlattenActivity,
                had_commit_failure = agg.CommitPersistFailed > 0,
                had_execution_block = agg.ExecutionBlocked > 0 || execSnap.OrdersBlocked > 0,
                had_order_rejection = agg.EntryRejected > 0 || execSnap.OrdersRejected > 0,
                had_protective_failure = agg.ProtectiveFailed > 0
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
        public long MaxResolvedMismatchDurationMs;

        public readonly Dictionary<string, int> MismatchDepth = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTimeOffset> MismatchOpenSince = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> FlattenPending = new(StringComparer.OrdinalIgnoreCase);

        public bool FlattenActivity;
        public bool TertiaryEntryReject;
        public bool TertiaryExecutionBlock;

        public bool HasMismatchBlocks => MismatchEnter > 0 || MismatchExit > 0;
        public bool HasLongRecoveredMismatch => MaxResolvedMismatchDurationMs > RecoveredMismatchWarnThresholdMs;
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
        if (agg.CommitPersistFailed > 0) return true;
        if (agg.ProtectiveFailed > 0) return true;
        if (agg.EntryRejected > 0 || agg.TertiaryEntryReject) return true;

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
        if (agg.CommitPersistFailed > 0) return "COMMIT_PERSIST_FAILED";
        if (agg.ProtectiveFailed > 0) return "PROTECTIVE_FAILED";
        if (agg.EntryRejected > 0 || agg.TertiaryEntryReject) return "ENTRY_REJECTED";
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
                            break;
                        case "FLATTEN_CONFIRMED":
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
                    if (!keyEventsPresent) agg.FlattenConfirmed++;
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

    private static string NormInst(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToUpperInvariant();

    private static DateTimeOffset? TryGetTimestampUtc(Dictionary<string, object> d)
    {
        if (!d.TryGetValue("ts_utc", out var tsObj) || tsObj == null)
            return null;
        return DateTimeOffset.TryParse(tsObj.ToString(), out var ts) ? ts : null;
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
}
