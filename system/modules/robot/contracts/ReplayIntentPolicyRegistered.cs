namespace QTSW2.Robot.Contracts
{

/// <summary>
/// IntentPolicyRegistered event payload. Must precede EntrySubmissionRequest (Full-System only).
/// Reference: IEA_REPLAY_CONTRACT.md §3.2
/// </summary>
public sealed class ReplayIntentPolicyRegistered
{
    public string IntentId { get; set; } = "";
    public int ExpectedQty { get; set; }
    public int MaxQty { get; set; }
    public string Canonical { get; set; } = "";
    public string Execution { get; set; } = "";
    public string PolicySource { get; set; } = "";
}
}
