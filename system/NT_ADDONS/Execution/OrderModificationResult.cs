using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Result of an order modification attempt (e.g., stop to BE).
/// </summary>
public class OrderModificationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }

    public static OrderModificationResult SuccessResult(DateTimeOffset modifiedAt)
        => new()
        {
            Success = true,
            ModifiedAt = modifiedAt
        };

    public static OrderModificationResult FailureResult(string errorMessage, DateTimeOffset modifiedAt)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            ModifiedAt = modifiedAt
        };
}
