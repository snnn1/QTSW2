using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Result of an order submission attempt.
/// </summary>
public class OrderSubmissionResult
{
    public bool Success { get; set; }
    public string? BrokerOrderId { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public static OrderSubmissionResult SuccessResult(string? brokerOrderId, DateTimeOffset submittedAt, DateTimeOffset? acknowledgedAt = null)
        => new()
        {
            Success = true,
            BrokerOrderId = brokerOrderId,
            SubmittedAt = submittedAt,
            AcknowledgedAt = acknowledgedAt
        };

    public static OrderSubmissionResult FailureResult(string errorMessage, DateTimeOffset submittedAt)
        => new()
        {
            Success = false,
            ErrorMessage = errorMessage,
            SubmittedAt = submittedAt
        };
}
