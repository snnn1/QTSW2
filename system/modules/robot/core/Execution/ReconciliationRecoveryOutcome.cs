// Minimal helper for reconciliation recovery decision logic.
// Used by tests to validate expected behavior; mirrors RobotEngine.AssembleMismatchObservations (NT).
// Do not redesign — this encodes the current behavior for testability.

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Outcome of a reconciliation recovery attempt. Matches AssembleMismatchObservations logic.
/// </summary>
public readonly struct ReconciliationRecoveryOutcome
{
    public bool SkipMismatch { get; }
    public bool LogSuccess { get; }
    public bool LogPartial { get; }

    public ReconciliationRecoveryOutcome(bool skipMismatch, bool logSuccess, bool logPartial)
    {
        SkipMismatch = skipMismatch;
        LogSuccess = logSuccess;
        LogPartial = logPartial;
    }

    /// <summary>
    /// Evaluate recovery outcome given adoption results.
    /// brokerWorking &gt; 0 and localBefore == 0 are preconditions (caller checks).
    /// </summary>
    public static ReconciliationRecoveryOutcome Evaluate(int brokerWorking, int localBefore, int adopted, int localAfter)
    {
        if (brokerWorking <= 0 || localBefore != 0)
            return new ReconciliationRecoveryOutcome(false, false, false);

        var skipMismatch = localAfter == brokerWorking;
        var logSuccess = adopted > 0;
        var logPartial = adopted > 0 && localAfter != brokerWorking;

        return new ReconciliationRecoveryOutcome(skipMismatch, logSuccess, logPartial);
    }
}
