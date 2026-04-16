using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core;

/// <summary>
/// Run-root operator files: <c>summary.json</c>, <c>NOTES.md</c>, <c>KEY_EVENTS.jsonl</c>, and <c>runs/LATEST_RUN.txt</c>.
/// </summary>
public static class RunRootArtifacts
{
    public const string NotesFileName = "NOTES.md";
    public const string KeyEventsFileName = "KEY_EVENTS.jsonl";
    public const string SummaryFileName = "summary.json";
    public const string AuditManifestFileName = "AUDIT_MANIFEST.json";
    public const string LatestRunPointerFileName = "LATEST_RUN.txt";

    private static readonly string NotesTemplate =
        "# Run notes" + Environment.NewLine + Environment.NewLine +
        "Add operator notes for this run (mismatches, flatten, recovery, instruments that behaved well)." + Environment.NewLine +
        Environment.NewLine +
        "- " + Environment.NewLine;

    /// <param name="projectRootForPlaybackPointer">When set (isolated playback runs), updates <c>runs/LATEST_RUN.txt</c>.</param>
    public static void EnsureBootstrapFiles(string persistenceBase, string? projectRootForPlaybackPointer = null)
    {
        try
        {
            var bootstrapTradingDate = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            RobotRunArtifactPaths.EnsureAuditDirectories(persistenceBase, bootstrapTradingDate);

            var notes = Path.Combine(persistenceBase, NotesFileName);
            if (!File.Exists(notes))
                File.WriteAllText(notes, NotesTemplate);
            var keyEvents = Path.Combine(persistenceBase, KeyEventsFileName);
            if (!File.Exists(keyEvents))
                File.WriteAllText(keyEvents, "");
            if (!string.IsNullOrEmpty(projectRootForPlaybackPointer))
                WriteLatestRunPointer(projectRootForPlaybackPointer, persistenceBase);
        }
        catch
        {
            // best-effort: do not block engine start
        }
    }

    public static void WriteAuditManifestJson(
        string persistenceBase,
        string? fullRunId,
        DateTimeOffset engineStartUtc,
        string? tradingDate,
        bool isolatedPlayback,
        bool canonicalOwnershipLedgerEnabled,
        bool unifiedExecutionAuthorityShadowEnabled,
        bool unifiedExecutionAuthorityEnabled,
        bool reconciliationRepairExecutorEnabled,
        bool structuralLayerUseLedgerOwnership)
    {
        try
        {
            var td = string.IsNullOrWhiteSpace(tradingDate)
                ? engineStartUtc.ToString("yyyy-MM-dd")
                : tradingDate.Trim();
            RobotRunArtifactPaths.EnsureAuditDirectories(persistenceBase, td);

            var dto = new Dictionary<string, object?>
            {
                ["run_id"] = fullRunId ?? "",
                ["trading_date"] = td,
                ["engine_start_utc"] = engineStartUtc.ToString("o"),
                ["persistence_base"] = Path.GetFullPath(persistenceBase),
                ["isolated_playback"] = isolatedPlayback,
                ["audit_scope"] = RobotRunArtifactPaths.AuditScopeLabel(persistenceBase),
                ["trusted_sources"] = new Dictionary<string, object?>
                {
                    ["engine_log"] = Path.Combine(RobotRunArtifactPaths.LogsRobot(persistenceBase), "robot_ENGINE.jsonl"),
                    ["instrument_logs_glob"] = Path.Combine(RobotRunArtifactPaths.LogsRobot(persistenceBase), "robot_*.jsonl"),
                    ["ownership_events"] = Path.Combine(RobotRunArtifactPaths.EventsOwnershipEventsTradingDate(persistenceBase, td), "events.jsonl"),
                    ["ownership_snapshots"] = Path.Combine(RobotRunArtifactPaths.EventsOwnershipSnapshotsTradingDate(persistenceBase, td), "ownership_snapshots.jsonl"),
                    ["orphan_fills"] = Path.Combine(RobotRunArtifactPaths.EventsOrphanFillsTradingDate(persistenceBase, td), "orphan_fills.jsonl"),
                    ["execution_events_dir"] = RobotRunArtifactPaths.EventsExecutionEventsTradingDate(persistenceBase, td)
                },
                ["shadow_flags"] = new Dictionary<string, object?>
                {
                    ["canonical_ownership_ledger_enabled"] = canonicalOwnershipLedgerEnabled,
                    ["unified_execution_authority_shadow_enabled"] = unifiedExecutionAuthorityShadowEnabled,
                    ["unified_execution_authority_enabled"] = unifiedExecutionAuthorityEnabled,
                    ["reconciliation_repair_executor_enabled"] = reconciliationRepairExecutorEnabled,
                    ["structural_layer_use_ledger_ownership"] = structuralLayerUseLedgerOwnership
                }
            };

            File.WriteAllText(Path.Combine(persistenceBase, AuditManifestFileName), JsonUtil.Serialize(dto));
        }
        catch
        {
            // best-effort: audits can still fall back to path conventions
        }
    }

    /// <summary>Path of the run folder relative to project root, POSIX slashes, no leading slash.</summary>
    public static string? TryGetRunFolderRelativeToRoot(string projectRoot, string persistenceBase)
    {
        try
        {
            var root = Path.GetFullPath(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var full = Path.GetFullPath(persistenceBase);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            var rel = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return null;
        }
    }

    public static void WriteLatestRunPointer(string projectRoot, string persistenceBase)
    {
        try
        {
            var rel = TryGetRunFolderRelativeToRoot(projectRoot, persistenceBase);
            if (string.IsNullOrEmpty(rel)) return;
            var runsDir = Path.Combine(projectRoot, "runs");
            Directory.CreateDirectory(runsDir);
            var ptr = Path.Combine(runsDir, LatestRunPointerFileName);
            File.WriteAllText(ptr, rel + Environment.NewLine);
        }
        catch
        {
            // best-effort
        }
    }

    public static void WriteRunSummaryJson(
        string persistenceBase,
        string projectRoot,
        string? fullRunId,
        DateTimeOffset engineStartUtc,
        ExecutionMode mode,
        IReadOnlyList<string> instruments,
        ExecutionSummarySnapshot execSnap)
    {
        try
        {
            var dto = RunSummaryBuilder.Build(
                persistenceBase,
                fullRunId,
                engineStartUtc,
                mode,
                instruments,
                execSnap);

            var path = Path.Combine(persistenceBase, SummaryFileName);
            File.WriteAllText(path, JsonUtil.Serialize(dto));
            WriteLatestRunPointer(projectRoot, persistenceBase);
        }
        catch
        {
            // best-effort shutdown path
        }
    }
}
