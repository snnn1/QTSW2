namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Recovery readiness guard (disconnect / reconnection). Does not represent global kill switch.
/// G3: Readiness reflects engine connection recovery state — adapters must not infer completion independently.
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
