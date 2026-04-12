using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Record for deferred retry when OrderMap lookup fails. Non-blocking grace period (500ms).
/// </summary>
public sealed class UnresolvedExecutionRecord
{
    public object Execution { get; }
    public object Order { get; }
    public string IntentId { get; }
    public string Instrument { get; }
    public string? EncodedTag { get; }
    public decimal FillPrice { get; }
    public int FillQuantity { get; }
    public string OrderState { get; }
    public bool IsProtectiveOrder { get; }
    public string? OrderTypeFromTag { get; }
    public DateTimeOffset FirstSeenUtc { get; }
    public int RetryCount { get; set; }
    public string? ExecutionId { get; }
    public string? BrokerOrderId { get; }

    public UnresolvedExecutionRecord(
        object execution,
        object order,
        string intentId,
        string instrument,
        string? encodedTag,
        decimal fillPrice,
        int fillQuantity,
        string orderState,
        bool isProtectiveOrder,
        string? orderTypeFromTag,
        DateTimeOffset firstSeenUtc,
        string? executionId = null,
        string? brokerOrderId = null)
    {
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        Order = order ?? throw new ArgumentNullException(nameof(order));
        IntentId = intentId ?? "";
        Instrument = instrument ?? "";
        EncodedTag = encodedTag;
        FillPrice = fillPrice;
        FillQuantity = fillQuantity;
        OrderState = orderState ?? "";
        IsProtectiveOrder = isProtectiveOrder;
        OrderTypeFromTag = orderTypeFromTag;
        FirstSeenUtc = firstSeenUtc;
        ExecutionId = executionId;
        BrokerOrderId = brokerOrderId;
    }

    public double ElapsedMs => (DateTimeOffset.UtcNow - FirstSeenUtc).TotalMilliseconds;
}
