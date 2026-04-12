using System;
using System.IO;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// G1: Append-only durable record for EPA-owned mismatch execution block enter/exit under the active run namespace
/// (<see cref="RobotEngine"/> persistence root). Forensics: who blocked instrument X at time T and why.
/// </summary>
public sealed class MismatchExecutionBlockDecisionLog
{
    private readonly string _jsonlPath;
    private readonly object _fileLock = new();

    public MismatchExecutionBlockDecisionLog(string persistenceBaseRoot)
    {
        var root = string.IsNullOrWhiteSpace(persistenceBaseRoot) ? "" : persistenceBaseRoot.Trim();
        var dir = RobotRunArtifactPaths.DecisionsDir(root);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // best-effort; Append will fail closed below
        }

        _jsonlPath = Path.Combine(dir, "mismatch_execution_block_decisions.jsonl");
    }

    /// <summary>One line per transition (ENTER = blocked, EXIT = cleared).</summary>
    public void Append(MismatchExecutionBlockDecisionRecord record)
    {
        try
        {
            var line = JsonUtil.Serialize(record) + Environment.NewLine;
            lock (_fileLock)
            {
                File.AppendAllText(_jsonlPath, line);
            }
        }
        catch
        {
            // Best-effort: do not fail authority path
        }
    }
}

/// <summary>G1 durable line for mismatch execution block authority (append-only JSONL).</summary>
public sealed class MismatchExecutionBlockDecisionRecord
{
    /// <summary>ENTER | EXIT</summary>
    public string Kind { get; set; } = "";

    public string Instrument { get; set; } = "";

    /// <summary>Engine trading date when known.</summary>
    public string? TradingDate { get; set; }

    public DateTimeOffset Utc { get; set; }

    public bool Blocked { get; set; }

    public string? BlockReason { get; set; }

    /// <summary>Always MISMATCH_ESCALATION_COORDINATOR for this path — observations feed EPA authority.</summary>
    public string CauseSource { get; set; } = "MISMATCH_ESCALATION_COORDINATOR";

    /// <summary>Authority owner for enforcement (normative G1).</summary>
    public string DecisionOwner { get; set; } = "ENGINE_EPA_MISMATCH_EXECUTION_BLOCK";
}
