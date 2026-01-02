using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Result of a flatten operation (cancel orders + close position).
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
