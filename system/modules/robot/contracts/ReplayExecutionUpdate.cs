using System;

namespace QTSW2.Robot.Contracts
{

/// <summary>
/// ExecutionUpdate event payload. Minimal deterministic replay shape.
/// executionTime is the ONLY authoritative time input.
/// Reference: IEA_REPLAY_CONTRACT.md §3.3
/// execution_sequence: Monotonic per executionInstrumentKey. Preserved from source when present;
/// otherwise assigned in replay order for determinism. EXECUTION_LOGGING_CANONICAL_SPEC §1.
/// </summary>
public sealed class ReplayExecutionUpdate
{
    public string? ExecutionId { get; set; }
    public string OrderId { get; set; } = "";
    public decimal FillPrice { get; set; }
    public int FillQuantity { get; set; }
    public string MarketPosition { get; set; } = "";
    public DateTimeOffset ExecutionTime { get; set; }
    public string? Tag { get; set; }
    public string? IntentId { get; set; }
    public string ExecutionInstrumentKey { get; set; } = "";

    /// <summary>
    /// Monotonic fill sequence per executionInstrumentKey. When present (e.g. from EXECUTION_FILLED),
    /// preserved. When absent, replay assigns in processing order for determinism.
    /// </summary>
    public int? ExecutionSequence { get; set; }
}
}
