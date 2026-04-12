using System;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Single matching/grouping logic for broker positions used by reconciliation and (via live adapter) flatten.
/// Snapshot path is pure and unit-testable; NT live exposure uses the same key rules in <c>GetBrokerCanonicalExposure</c>.
/// </summary>
public static class BrokerPositionResolver
{
    /// <summary>
    /// Normalize user/chart/command input to the canonical master key.
    /// Strips trailing contract month/year when formatted as "ROOT MM-yy" (first token).
    /// </summary>
    public static string NormalizeCanonicalKey(string? logicalOrExecutionInstrument)
    {
        if (string.IsNullOrWhiteSpace(logicalOrExecutionInstrument))
            return "";
        var t = logicalOrExecutionInstrument.Trim();
        var space = t.IndexOf(' ');
        if (space > 0)
            return t.Substring(0, space);
        return t;
    }

    /// <summary>
    /// Build reconciliation broker quantities: same aggregation as legacy ReconciliationRunner (abs per row, sum by master key).
    /// </summary>
    public static Dictionary<string, int> BuildReconciliationAbsTotalsByCanonicalKey(IReadOnlyList<PositionSnapshot> positions)
    {
        var accountQtyByInstrument = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions)
        {
            if (p.Quantity == 0 || string.IsNullOrWhiteSpace(p.Instrument))
                continue;
            var inst = p.Instrument.Trim();
            var qty = Math.Abs(p.Quantity);
            accountQtyByInstrument.TryGetValue(inst, out var existing);
            accountQtyByInstrument[inst] = existing + qty;
        }
        return accountQtyByInstrument;
    }

    /// <summary>
    /// Resolve exposure for one canonical key from a snapshot (tests + log diagnostics).
    /// </summary>
    public static BrokerCanonicalExposure ResolveFromSnapshots(IReadOnlyList<PositionSnapshot> positions, string logicalInstrument)
    {
        var canonical = NormalizeCanonicalKey(logicalInstrument);
        if (string.IsNullOrEmpty(canonical))
            return BrokerCanonicalExposure.Empty("");

        var legs = new List<BrokerPositionLeg>();
        foreach (var p in positions)
        {
            if (p.Quantity == 0 || string.IsNullOrWhiteSpace(p.Instrument))
                continue;
            var rowKey = p.Instrument.Trim();
            if (!string.Equals(rowKey, canonical, StringComparison.OrdinalIgnoreCase))
                continue;
            legs.Add(new BrokerPositionLeg
            {
                SignedQuantity = p.Quantity,
                ContractLabel = string.IsNullOrEmpty(p.ContractLabel) ? rowKey : p.ContractLabel,
                NativeInstrument = null
            });
        }

        var totalAbs = legs.Sum(l => Math.Abs(l.SignedQuantity));
        return new BrokerCanonicalExposure
        {
            CanonicalKey = canonical,
            ReconciliationAbsQuantityTotal = totalAbs,
            Legs = legs
        };
    }

    /// <summary>Structured row descriptors for JSONL (no native handles).</summary>
    public static IReadOnlyList<object> ToDiagnosticRows(BrokerCanonicalExposure exposure)
    {
        var list = new List<object>();
        for (var i = 0; i < exposure.Legs.Count; i++)
        {
            var leg = exposure.Legs[i];
            list.Add(new
            {
                leg_index = i,
                contract_label = leg.ContractLabel ?? "",
                signed_quantity = leg.SignedQuantity,
                abs_quantity = Math.Abs(leg.SignedQuantity),
                has_native_instrument = leg.NativeInstrument != null
            });
        }
        return list;
    }
}
