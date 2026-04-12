namespace QTSW2.Robot.Contracts
{

/// <summary>
/// IntentRegistered event payload. Must precede ExecutionUpdate/BE/fill for that intent.
/// Reference: IEA_REPLAY_CONTRACT.md §3.1
/// </summary>
public sealed class ReplayIntentRegistered
{
    public string IntentId { get; set; } = "";
    public ReplayIntent Intent { get; set; } = null!;
}
}
