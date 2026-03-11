using System;
using QTSW2.Robot.Core;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Stage 1: Queued protective submission when entry fill detected during recovery.
/// Processed in Stage 2 when broker ready, or Stage 3 fail-safe flatten if timeout exceeded.
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
