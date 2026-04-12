// Canonical broker position identity: reconciliation vs resolver alignment.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BROKER_POSITION_IDENTITY

using System;
using System.Collections.Generic;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class BrokerPositionIdentityTests
{
    public static (bool Pass, string? Error) RunBrokerPositionIdentityTests()
    {
        // A: Reconciliation totals match resolver for same snapshot
        var positions = new List<PositionSnapshot>
        {
            new() { Instrument = "MNQ", Quantity = 1, AveragePrice = 1m, ContractLabel = "MNQ 03-26" },
            new() { Instrument = "MNQ", Quantity = 1, AveragePrice = 1m, ContractLabel = "MNQ 06-26" }
        };
        var dict = BrokerPositionResolver.BuildReconciliationAbsTotalsByCanonicalKey(positions);
        if (!dict.TryGetValue("MNQ", out var dQty) || dQty != 2)
            return (false, $"Expected reconciliation dict MNQ=2, got {dQty}");
        var exp = BrokerPositionResolver.ResolveFromSnapshots(positions, "MNQ");
        if (exp.ReconciliationAbsQuantityTotal != 2 || exp.Legs.Count != 2)
            return (false, $"Resolver total/legs: expected 2/2, got {exp.ReconciliationAbsQuantityTotal}/{exp.Legs.Count}");
        if (!exp.IsAggregatedMultipleRows)
            return (false, "Expected aggregated multiple rows");

        // C: Two rows same key — total abs matches sum of legs
        if (exp.Legs.Sum(l => Math.Abs(l.SignedQuantity)) != 2)
            return (false, "Leg abs sum mismatch");

        // D: Flat bucket — zero legs
        var flat = BrokerPositionResolver.ResolveFromSnapshots(new List<PositionSnapshot>(), "ES");
        if (flat.ReconciliationAbsQuantityTotal != 0 || flat.Legs.Count != 0)
            return (false, "Empty snapshot should yield flat exposure");

        // E: Normalize key from full name
        if (BrokerPositionResolver.NormalizeCanonicalKey("MYM 06-26") != "MYM")
            return (false, "NormalizeCanonicalKey should take first token");

        // Diagnostic rows
        var rows = BrokerPositionResolver.ToDiagnosticRows(exp);
        if (rows.Count != 2)
            return (false, "ToDiagnosticRows count");

        return (true, null);
    }
}
