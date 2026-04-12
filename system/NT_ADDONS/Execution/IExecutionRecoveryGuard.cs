namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Recovery readiness guard only (disconnect / reconnection recovery). Does not represent global kill switch;
/// callers combine with <see cref="KillSwitch"/> / EPA for full permission semantics.
/// G3: Readiness reflects engine <c>ConnectionRecoveryState</c> only — adapters must not infer recovery completion independently.
/// </summary>
public interface IExecutionRecoveryGuard
{
    /// <summary>
    /// True when engine recovery state allows execution (CONNECTED_OK / RECOVERY_COMPLETE). Not a kill-switch check.
    /// </summary>
    bool IsExecutionAllowed();
    
    /// <summary>
    /// Get recovery state reason string (for logging/failure messages).
    /// </summary>
    string GetRecoveryStateReason();
}
