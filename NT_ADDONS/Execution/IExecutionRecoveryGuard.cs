namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Interface for execution recovery guard (blocks execution during disconnect recovery).
/// </summary>
public interface IExecutionRecoveryGuard
{
    /// <summary>
    /// Check if execution is allowed based on recovery state.
    /// </summary>
    bool IsExecutionAllowed();
    
    /// <summary>
    /// Get recovery state reason string (for logging/failure messages).
    /// </summary>
    string GetRecoveryStateReason();
}
