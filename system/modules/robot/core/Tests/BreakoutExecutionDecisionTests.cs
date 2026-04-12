// Unit tests for breakout execution decision: STOP vs MARKET when price already crossed.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BREAKOUT_EXECUTION_DECISION
//
// Verifies: When bid <= brk_short → MARKET SELL; when ask >= brk_long → MARKET BUY; else STOP.

using System;

namespace QTSW2.Robot.Core.Tests;

public static class BreakoutExecutionDecisionTests
{
    /// <summary>Mirrors logic in SubmitStopEntryBracketsAtLock for testing.</summary>
    private static (bool longCrossed, bool shortCrossed) ComputeCrossed(decimal? bid, decimal? ask, decimal brkLong, decimal brkShort)
    {
        var longCrossed = ask.HasValue && ask.Value >= brkLong;
        var shortCrossed = bid.HasValue && bid.Value <= brkShort;
        return (longCrossed, shortCrossed);
    }

    public static (bool Pass, string? Error) RunBreakoutExecutionDecisionTests()
    {
        // Breakout levels
        const decimal brkLong = 100.5m;
        const decimal brkShort = 99.5m;

        // 1. Breakout NOT crossed - both STOP
        var (l1, s1) = ComputeCrossed(100.0m, 100.2m, brkLong, brkShort);
        if (l1 || s1)
            return (false, $"Both not crossed: bid=100 ask=100.2 brkL=100.5 brkS=99.5 → expected (false,false) got ({l1},{s1})");

        // 2. Long crossed only (ask >= brk_long, bid > brk_short) - LONG→MARKET, SHORT→STOP
        var (l2, s2) = ComputeCrossed(100.0m, 101.0m, brkLong, brkShort);
        if (!l2 || s2)
            return (false, $"Long crossed only: bid=100 ask=101 brkL=100.5 brkS=99.5 → expected (true,false) got ({l2},{s2})");

        // 3. Short crossed only (bid <= brk_short, ask < brk_long) - LONG→STOP, SHORT→MARKET
        var (l3, s3) = ComputeCrossed(99.0m, 100.0m, brkLong, brkShort);
        if (l3 || !s3)
            return (false, $"Short crossed only: bid=99 ask=100 brkL=100.5 brkS=99.5 → expected (false,true) got ({l3},{s3})");

        // 4. Both crossed (edge case) - both MARKET
        var (l4, s4) = ComputeCrossed(99.0m, 101.0m, brkLong, brkShort);
        if (!l4 || !s4)
            return (false, $"Both crossed: bid=99 ask=101 → expected (true,true) got ({l4},{s4})");

        // 5. No market data (bid/ask null) - both STOP (fail open)
        var (l5, s5) = ComputeCrossed(null, null, brkLong, brkShort);
        if (l5 || s5)
            return (false, $"No market data: bid=null ask=null → expected (false,false) got ({l5},{s5})");

        // 6. Exactly at breakout - long: ask=100.5 >= 100.5 = true; short: bid=99.5 <= 99.5 = true
        var (l6, s6) = ComputeCrossed(99.5m, 100.5m, brkLong, brkShort);
        if (!l6 || !s6)
            return (false, $"Exactly at breakout: bid=99.5 ask=100.5 → expected (true,true) got ({l6},{s6})");

        return (true, null);
    }
}
