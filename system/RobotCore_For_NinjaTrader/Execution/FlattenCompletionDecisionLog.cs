using System;
using System.IO;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// G4: Append-only durable line when flatten is <b>officially complete</b> per <see cref="FlattenCompletionAuthority"/>
/// (broker canonical reconciliation abs == 0), not on submit alone.
/// </summary>
public sealed class FlattenCompletionDecisionLog
{
    private readonly string _jsonlPath;
    private readonly object _fileLock = new();

    public FlattenCompletionDecisionLog(string persistenceBaseRoot)
    {
        var root = string.IsNullOrWhiteSpace(persistenceBaseRoot) ? "" : persistenceBaseRoot.Trim();
        var dir = RobotRunArtifactPaths.DecisionsDir(root);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // best-effort
        }

        _jsonlPath = Path.Combine(dir, "flatten_broker_flat_completions.jsonl");
    }

    public void Append(FlattenCompletionDecisionRecord record)
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
            // best-effort: never block execution path
        }
    }
}

/// <summary>One durable completion fact (append-only).</summary>
public sealed class FlattenCompletionDecisionRecord
{
    public const string ProofCanonicalAbsZero = "BROKER_CANONICAL_RECONCILIATION_ABS_ZERO";

    public DateTimeOffset Utc { get; set; }

    public string Instrument { get; set; } = "";

    public string? CanonicalBrokerKey { get; set; }

    /// <summary>Always 0 when written — proof is canonical model.</summary>
    public int ReconciliationAbsRemaining { get; set; }

    public string Proof { get; set; } = ProofCanonicalAbsZero;

    public string? CorrelationId { get; set; }

    public string? EpisodeId { get; set; }

    public string Source { get; set; } = "ADAPTER_VERIFY";
}
