using System;
using QTSW2.Robot.Contracts;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Default live implementations. Both return UtcNow.</summary>
public sealed class LiveEventClock : IEventClock
{
    public DateTimeOffset NowEvent() => DateTimeOffset.UtcNow;
}

/// <summary>Default live implementation for wall clock.</summary>
public sealed class LiveWallClock : IWallClock
{
    public DateTimeOffset NowWall() => DateTimeOffset.UtcNow;
}
