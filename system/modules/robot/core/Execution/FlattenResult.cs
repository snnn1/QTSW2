using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Result of a flatten <b>request</b> (cancel path + close orders submitted / enqueued).
/// G4: <see cref="Success"/> means request accepted or submit reached — not broker-flat completion.
/// Official completion: <c>FLATTEN_BROKER_FLAT_CONFIRMED</c> / canonical reconciliation abs == 0.
/// </summary>
public class FlattenResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset FlattenedAt { get; set; }

    public static FlattenResult SuccessResult(DateTimeOffset flattenedAt)
        => new()
        {
            Success = true,
            FlattenedAt = flattenedAt
        };

    public static FlattenResult FailureResult(string errorMessage, DateTimeOffset flattenedAt)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            FlattenedAt = flattenedAt
        };
}
