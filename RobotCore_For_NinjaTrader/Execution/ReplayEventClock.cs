using System;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Event clock for replay. SetNow() before each event; NowEvent()/NowWall() return the set value.
/// Audit replay: wall-clock timeouts bypassed; NowWall = NowEvent is safe.
/// Reference: IEA_REPLAY_CONTRACT.md §4.1
/// </summary>
public sealed class ReplayEventClock : IEventClock, IWallClock
{
    private DateTimeOffset _now = DateTimeOffset.MinValue;

    public void SetNow(DateTimeOffset value) => _now = value;
    public DateTimeOffset NowEvent() => _now;
    public DateTimeOffset NowWall() => _now;
}
