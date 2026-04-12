using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Order role in the execution lifecycle.
/// </summary>
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

/// <summary>
/// Ownership status: who placed or adopted this order.
/// </summary>
public enum OrderOwnershipStatus
{
    OWNED,    // Submitted by current runtime
    ADOPTED,  // Discovered and intentionally taken over (e.g. restart protectives)
    UNOWNED,  // Known anomaly, not under robot ownership
    /// <summary>
    /// Robot-tagged broker order in registry but not yet promoted to ADOPTED (e.g. adoption pending, id remap).
    /// Counted toward mismatch-assembly trusted working so ORDER_REGISTRY_MISSING_FAIL_CLOSED does not fire on recoverable drift alone.
    /// </summary>
    RECOVERABLE_ROBOT_OWNED,
    TERMINAL  // Completed/canceled/rejected, no longer live
}

/// <summary>
/// Lifecycle state of the order.
/// </summary>
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
/// Canonical runtime order record for Execution Ownership.
/// Identity is broker/native order id; intent linkage is secondary.
/// </summary>
public sealed class OrderRegistryEntry
{
    /// <summary>Broker/native order id. Canonical identity.</summary>
    public string BrokerOrderId { get; set; } = "";

    /// <summary>Intent id if any (entry, stop, target). Null for flatten.</summary>
    public string? IntentId { get; set; }

    /// <summary>Instrument (e.g. MNQ, MGC).</summary>
    public string Instrument { get; set; } = "";

    /// <summary>Stream if known.</summary>
    public string? Stream { get; set; }

    /// <summary>Order role.</summary>
    public OrderRole OrderRole { get; set; }

    /// <summary>Ownership status.</summary>
    public OrderOwnershipStatus OwnershipStatus { get; set; }

    /// <summary>Lifecycle state.</summary>
    public OrderLifecycleState LifecycleState { get; set; }

    /// <summary>Source/caller context (e.g. "ENTRY_FILL", "RequestFlatten", "ScanAndAdopt").</summary>
    public string? SourceContext { get; set; }

    /// <summary>When the order was registered.</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When the order became terminal (filled/canceled/rejected).</summary>
    public DateTimeOffset? TerminalUtc { get; set; }

    /// <summary>Legacy OrderInfo for compatibility. Same object used in OrderMap aliases.</summary>
    internal OrderInfo OrderInfo { get; set; } = null!;

    /// <summary>Resolution path when looked up: DirectId, Alias, Adopted, Unresolved.</summary>
    public string? LastResolutionPath { get; set; }
}
