using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// Wall clock for operational blocking (EnqueueAndWait timeouts).
/// Audit Replay does not exercise. Full-System Replay may simulate.
/// Reference: IEA_REPLAY_CONTRACT.md §4.2
/// </summary>
public interface IWallClock
{
    DateTimeOffset NowWall();
}
}
