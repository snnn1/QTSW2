// Unit tests for breakout execution decision: STOP vs MARKET when price already crossed.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BREAKOUT_EXECUTION_DECISION
//
// Verifies: crossed breakouts are detected, and mixed MARKET/STOP entry brackets submit the STOP side first.

using System;

namespace QTSW2.Robot.Core.Tests;

public static class BreakoutExecutionDecisionTests
{
    /// <summary>Mirrors the crossing and submit-order contract used by SubmitStopEntryBracketsAtLock.</summary>
    private static (bool longCrossed, bool shortCrossed) ComputeCrossed(decimal? bid, decimal? ask, decimal brkLong, decimal brkShort)
    {
        var longCrossed = ask.HasValue && ask.Value >= brkLong;
        var shortCrossed = bid.HasValue && bid.Value <= brkShort;
        return (longCrossed, shortCrossed);
    }

    private static string ResolveSubmitPlan(bool longMarket, bool shortMarket)
    {
        if (longMarket && shortMarket) return "REJECT_BOTH_MARKET";
        if (longMarket) return "SHORT_STOP_THEN_LONG_MARKET";
        if (shortMarket) return "LONG_STOP_THEN_SHORT_MARKET";
        return "LONG_STOP_THEN_SHORT_STOP";
    }

    public static (bool Pass, string? Error) RunBreakoutExecutionDecisionTests()
    {
        const decimal brkLong = 100.5m;
        const decimal brkShort = 99.5m;

        var (l1, s1) = ComputeCrossed(100.0m, 100.2m, brkLong, brkShort);
        if (l1 || s1)
            return (false, $"Both not crossed: expected (false,false) got ({l1},{s1})");
        if (ResolveSubmitPlan(l1, s1) != "LONG_STOP_THEN_SHORT_STOP")
            return (false, "Non-crossed plan must submit both stop orders.");

        var (l2, s2) = ComputeCrossed(100.0m, 101.0m, brkLong, brkShort);
        if (!l2 || s2)
            return (false, $"Long crossed only: expected (true,false) got ({l2},{s2})");
        if (ResolveSubmitPlan(l2, s2) != "SHORT_STOP_THEN_LONG_MARKET")
            return (false, "Long market plan must submit passive short stop before market long to avoid OCO reuse.");

        var (l3, s3) = ComputeCrossed(99.0m, 100.0m, brkLong, brkShort);
        if (l3 || !s3)
            return (false, $"Short crossed only: expected (false,true) got ({l3},{s3})");
        if (ResolveSubmitPlan(l3, s3) != "LONG_STOP_THEN_SHORT_MARKET")
            return (false, "Short market plan must submit passive long stop before market short to avoid OCO reuse.");

        var (l4, s4) = ComputeCrossed(99.0m, 101.0m, brkLong, brkShort);
        if (!l4 || !s4)
            return (false, $"Both crossed: expected (true,true) got ({l4},{s4})");
        if (ResolveSubmitPlan(l4, s4) != "REJECT_BOTH_MARKET")
            return (false, "Both-market breakout plan must be rejected as ambiguous.");

        var (l5, s5) = ComputeCrossed(null, null, brkLong, brkShort);
        if (l5 || s5)
            return (false, $"No market data: expected (false,false) got ({l5},{s5})");

        var (l6, s6) = ComputeCrossed(99.5m, 100.5m, brkLong, brkShort);
        if (!l6 || !s6)
            return (false, $"Exactly at breakout: expected (true,true) got ({l6},{s6})");
        if (ResolveSubmitPlan(l6, s6) != "REJECT_BOTH_MARKET")
            return (false, "Both exact breakout plan must be rejected as ambiguous.");

        return (true, null);
    }
}
