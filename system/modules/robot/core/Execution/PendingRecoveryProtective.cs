using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Queued protective submission when entry fill is observed during recovery.
/// Processed when broker readiness returns or escalated by the recovery fail-safe.
/// </summary>
public sealed class PendingRecoveryProtective
{
    public string IntentId { get; }
    public Intent Intent { get; }
    public int TotalFilledQuantity { get; }
    public DateTimeOffset QueuedAtUtc { get; }

    public PendingRecoveryProtective(string intentId, Intent intent, int totalFilledQuantity, DateTimeOffset queuedAtUtc)
    {
        IntentId = intentId ?? throw new ArgumentNullException(nameof(intentId));
        Intent = intent ?? throw new ArgumentNullException(nameof(intent));
        TotalFilledQuantity = totalFilledQuantity;
        QueuedAtUtc = queuedAtUtc;
    }
}
