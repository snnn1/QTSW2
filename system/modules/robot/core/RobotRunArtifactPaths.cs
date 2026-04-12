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

    public static string DerivedExecutionSummaries(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "derived", "execution_summaries");

    public static string DecisionsDir(string persistenceBase)
        => Path.Combine(persistenceBase ?? "", "decisions");
}
