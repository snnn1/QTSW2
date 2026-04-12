using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// Wrapper for all replay events. Every event in the replay stream has this shape.
/// Reference: IEA_REPLAY_CONTRACT.md §3
/// </summary>
public sealed class ReplayEventEnvelope
{
    public string Source { get; set; } = "";
    public long Sequence { get; set; }
    public string ExecutionInstrumentKey { get; set; } = "";
    public ReplayEventType Type { get; set; }
    public object Payload { get; set; } = null!;
}

/// <summary>Replay event type discriminator.</summary>
public enum ReplayEventType
{
    IntentRegistered,
    IntentPolicyRegistered,
    ExecutionUpdate,
    OrderUpdate,
    Tick
}
}
