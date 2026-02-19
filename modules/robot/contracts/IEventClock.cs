using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// Event clock for deterministic replay. Live: UtcNow. Replay: event timestamp.
/// Used for BE throttles, dedup insert, dedup eviction.
/// Reference: IEA_REPLAY_CONTRACT.md §4.1
/// </summary>
public interface IEventClock
{
    DateTimeOffset NowEvent();
}
}
