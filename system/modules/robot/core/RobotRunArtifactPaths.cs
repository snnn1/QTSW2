using System;
using System.IO;

namespace QTSW2.Robot.Core;

/// <summary>
/// Run namespace layout under <see cref="RobotEngine"/> persistence base (<c>_persistenceBase</c>).
/// Single source for new-write paths; no migration of historical locations.
/// </summary>
public static class RobotRunArtifactPaths
{
    public static string LogsRobot(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "logs", "robot");

    public static string LogsHealth(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "logs", "health");

    public static string LogsHydration(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "logs", "hydration");

    public static string LogsRanges(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "logs", "ranges");

    public static string LogsRangeBuilding(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "logs", "range_building");

    public static string StateStreamJournals(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "state", "stream_journals");

    public static string StateExecutionJournals(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "state", "execution_journals");

    /// <summary>Directory for canonical execution event JSONL for one trading date: .../events/execution_events/{td}/</summary>
    public static string EventsExecutionEventsTradingDate(string persistenceBase, string tradingDate)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate)
            ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
            : tradingDate.Trim();
        return Path.Combine(persistenceBase ?? "", "events", "execution_events", td);
    }

    /// <summary>Directory for durable ownership events for one trading date: .../events/ownership_events/{td}/</summary>
    public static string EventsOwnershipEventsTradingDate(string persistenceBase, string tradingDate)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate)
            ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
            : tradingDate.Trim();
        return Path.Combine(persistenceBase ?? "", "events", "ownership_events", td);
    }

    /// <summary>Directory for ownership snapshot JSONL: .../events/ownership_snapshots/{td}/</summary>
    public static string EventsOwnershipSnapshotsTradingDate(string persistenceBase, string tradingDate)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate)
            ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
            : tradingDate.Trim();
        return Path.Combine(persistenceBase ?? "", "events", "ownership_snapshots", td);
    }

    /// <summary>Directory for orphan fill JSONL: .../events/orphan_fills/{td}/</summary>
    public static string EventsOrphanFillsTradingDate(string persistenceBase, string tradingDate)
    {
        var td = string.IsNullOrWhiteSpace(tradingDate)
            ? DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")
            : tradingDate.Trim();
        return Path.Combine(persistenceBase ?? "", "events", "orphan_fills", td);
    }

    public static string DerivedExecutionSummaries(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "derived", "execution_summaries");

    public static string DecisionsDir(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "decisions");

    /// <summary>Create the run directories that audits treat as canonical sources.</summary>
    public static void EnsureAuditDirectories(string persistenceBase, string tradingDate)
    {
        Directory.CreateDirectory(LogsRobot(persistenceBase));
        Directory.CreateDirectory(EventsExecutionEventsTradingDate(persistenceBase, tradingDate));
        Directory.CreateDirectory(EventsOwnershipEventsTradingDate(persistenceBase, tradingDate));
        Directory.CreateDirectory(EventsOwnershipSnapshotsTradingDate(persistenceBase, tradingDate));
        Directory.CreateDirectory(EventsOrphanFillsTradingDate(persistenceBase, tradingDate));
        Directory.CreateDirectory(StateStreamJournals(persistenceBase));
        Directory.CreateDirectory(StateExecutionJournals(persistenceBase));
        Directory.CreateDirectory(DerivedExecutionSummaries(persistenceBase));
        Directory.CreateDirectory(DecisionsDir(persistenceBase));
    }

    /// <summary>True when <paramref name="persistenceBase"/> is under a <c>runs/&lt;id&gt;/</c> tree (isolated run artifacts).</summary>
    public static bool IsRunScopedPersistence(string? persistenceBase)
    {
        if (string.IsNullOrWhiteSpace(persistenceBase)) return false;
        try
        {
            var fp = Path.GetFullPath(persistenceBase.Trim());
            var parts = fp.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "runs", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }

    /// <summary>Audit label: <c>RUN</c> under <c>runs/</c>, else <c>GLOBAL</c> (project-root persistence).</summary>
    public static string AuditScopeLabel(string? persistenceBase)
        => IsRunScopedPersistence(persistenceBase) ? "RUN" : "GLOBAL";

    /// <summary>
    /// Audit <c>run_id</c> string: prefer <paramref name="engineRunId"/> when set; else first path segment after <c>runs\</c>;
    /// else <c>NONE</c> for global (non–run-folder) persistence.
    /// </summary>
    public static string AuditRunIdLabel(string? persistenceBase, string? engineRunId = null)
    {
        if (!string.IsNullOrWhiteSpace(engineRunId)) return engineRunId.Trim();
        if (TryGetRunIdSegmentAfterRuns(persistenceBase, out var rid)) return rid;
        return "NONE";
    }

    private static bool TryGetRunIdSegmentAfterRuns(string? persistenceBase, out string runId)
    {
        runId = "";
        if (string.IsNullOrWhiteSpace(persistenceBase)) return false;
        try
        {
            var fp = Path.GetFullPath(persistenceBase.Trim());
            var parts = fp.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "runs", StringComparison.OrdinalIgnoreCase))
                {
                    runId = parts[i + 1];
                    return !string.IsNullOrEmpty(runId);
                }
            }
        }
        catch
        {
            /* ignore */
        }

        return false;
    }
}
