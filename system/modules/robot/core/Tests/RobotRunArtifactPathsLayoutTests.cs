using System.IO;

namespace QTSW2.Robot.Core.Tests;

/// <summary>Co-location contract: new writes under one persistence root use the run namespace layout.</summary>
public static class RobotRunArtifactPathsLayoutTests
{
    /// <summary>Harness: dotnet run ... --test ROBOT_RUN_ARTIFACT_PATHS_LAYOUT</summary>
    public static (bool Pass, string? Error) RunLayoutContract()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "qtsw2_run_layout_" + Path.GetRandomFileName());
        try
        {
            if (!string.Equals(Path.Combine(baseDir, "logs", "robot"), RobotRunArtifactPaths.LogsRobot(baseDir), StringComparison.OrdinalIgnoreCase))
                return (false, "LogsRobot mismatch");
            if (!string.Equals(Path.Combine(baseDir, "logs", "health"), RobotRunArtifactPaths.LogsHealth(baseDir), StringComparison.OrdinalIgnoreCase))
                return (false, "LogsHealth mismatch");
            if (!string.Equals(Path.Combine(baseDir, "state", "stream_journals"), RobotRunArtifactPaths.StateStreamJournals(baseDir), StringComparison.OrdinalIgnoreCase))
                return (false, "StateStreamJournals mismatch");
            if (!string.Equals(Path.Combine(baseDir, "state", "execution_journals"), RobotRunArtifactPaths.StateExecutionJournals(baseDir), StringComparison.OrdinalIgnoreCase))
                return (false, "StateExecutionJournals mismatch");
            if (!string.Equals(Path.Combine(baseDir, "events", "execution_events", "2026-04-11"), RobotRunArtifactPaths.EventsExecutionEventsTradingDate(baseDir, "2026-04-11"), StringComparison.OrdinalIgnoreCase))
                return (false, "EventsExecutionEventsTradingDate mismatch");
            if (!string.Equals(Path.Combine(baseDir, "derived", "execution_summaries"), RobotRunArtifactPaths.DerivedExecutionSummaries(baseDir), StringComparison.OrdinalIgnoreCase))
                return (false, "DerivedExecutionSummaries mismatch");
            if (!string.Equals(Path.Combine(baseDir, "decisions"), RobotRunArtifactPaths.DecisionsDir(baseDir), StringComparison.OrdinalIgnoreCase))
                return (false, "DecisionsDir mismatch");
            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true); } catch { /* ignore */ }
        }
    }
}
