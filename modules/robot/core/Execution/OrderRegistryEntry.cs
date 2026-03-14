using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>Order role in the execution lifecycle.</summary>
public enum OrderRole
{
    ENTRY,
    STOP,
    TARGET,
    FLATTEN,
    RECOVERY,
    RECOVERY_FLATTEN,
    ADOPTED,
    EXTERNAL  // Phase 2: Manual or broker-originated, not under robot ownership
}

/// <summary>Ownership status: who placed or adopted this order.</summary>
public enum OrderOwnershipStatus
{
    OWNED,
    ADOPTED,
    UNOWNED,
    TERMINAL
}

/// <summary>Lifecycle state of the order.</summary>
public enum OrderLifecycleState
{
    CREATED,
    SUBMITTED,
    WORKING,
    PART_FILLED,
    FILLED,
    CANCELED,
    REJECTED
}

/// <summary>
/// Minimal order info for registry tests. Full OrderInfo is in RobotCore_For_NinjaTrader.
/// </summary>
public sealed class MinimalOrderInfo
{
    public string OrderId { get; set; } = "";
    public string Instrument { get; set; } = "";
}

/// <summary>
/// Canonical runtime order record for Execution Ownership.
/// Identity is broker/native order id; intent linkage is secondary.
/// </summary>
public sealed class OrderRegistryEntry
{
    public string BrokerOrderId { get; set; } = "";
    public string? IntentId { get; set; }
    public string Instrument { get; set; } = "";
    public string? Stream { get; set; }
    public OrderRole OrderRole { get; set; }
    public OrderOwnershipStatus OwnershipStatus { get; set; }
    public OrderLifecycleState LifecycleState { get; set; }
    public string? SourceContext { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? TerminalUtc { get; set; }
    /// <summary>Order info reference. Use MinimalOrderInfo for tests.</summary>
    public object OrderInfo { get; set; } = null!;
    public string? LastResolutionPath { get; set; }
}
