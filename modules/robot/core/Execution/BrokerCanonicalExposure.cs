using System;
using System.Collections.Generic;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Canonical broker exposure for one logical instrument, shared by reconciliation, flatten, recovery, and verification.
/// <para>
/// <b>Canonical identity:</b> NinjaTrader <c>MasterInstrument.Name</c> (e.g. MNQ, MYM, MES), case-insensitive.
/// All non-flat <see cref="PositionSnapshot"/> / account position rows whose master name equals this key belong to the same bucket.
/// </para>
/// <para>
/// <b>Quantity truth for reconciliation:</b> <see cref="ReconciliationAbsQuantityTotal"/> = sum of <c>Math.Abs(signedQty)</c>
/// over every matching row — identical to legacy <see cref="ReconciliationRunner"/> aggregation.
/// </para>
/// <para>
/// <b>Flatten:</b> closes every non-zero <see cref="BrokerPositionLeg"/> using that leg's native NT <c>Instrument</c>
/// (see <see cref="BrokerPositionLeg.NativeInstrument"/>). This is design choice <b>A</b>: flatten the exact broker rows
/// reconciliation counts, not only the chart-bound instrument.
/// </para>
/// </summary>
public sealed class BrokerCanonicalExposure
{
    /// <summary>Normalized master key (NT MasterInstrument.Name).</summary>
    public string CanonicalKey { get; init; } = "";

    /// <summary>Sum of absolute quantities per matching position row; matches reconciliation broker_qty.</summary>
    public int ReconciliationAbsQuantityTotal { get; init; }

    /// <summary>Non-zero broker rows in this bucket (one per NT Position typically).</summary>
    public IReadOnlyList<BrokerPositionLeg> Legs { get; init; } = Array.Empty<BrokerPositionLeg>();

    public bool IsAggregatedMultipleRows => Legs.Count > 1;

    public static BrokerCanonicalExposure Empty(string canonicalKey) => new()
    {
        CanonicalKey = canonicalKey,
        ReconciliationAbsQuantityTotal = 0,
        Legs = Array.Empty<BrokerPositionLeg>()
    };
}

/// <summary>One broker position row within a canonical master-instrument bucket.</summary>
public sealed class BrokerPositionLeg
{
    /// <summary>Signed quantity (NT position quantity).</summary>
    public int SignedQuantity { get; init; }

    /// <summary>Contract-level label for logs (e.g. NT FullName).</summary>
    public string? ContractLabel { get; init; }

    /// <summary>NT Instrument instance for order creation; null in replay / tests.</summary>
    public object? NativeInstrument { get; init; }
}
