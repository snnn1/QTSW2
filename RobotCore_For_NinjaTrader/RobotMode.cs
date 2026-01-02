namespace QTSW2.Robot.Core;

/// <summary>
/// Robot execution mode
/// </summary>
public enum RobotMode
{
    /// <summary>
    /// Step-2 skeleton mode: wiring proof, no trading logic
    /// </summary>
    SKELETON,

    /// <summary>
    /// Step-3 dry-run mode: Analyzer-equivalent intended trade outcomes without order submission
    /// </summary>
    DRYRUN
}
