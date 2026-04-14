using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

/// <summary>Phase 3.2: bridge-aligned position aggregates for convergence probe (same math as mismatch assembly).</summary>
public static class MismatchConvergenceBridgeProbeTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var (a, ea) = BridgeClosesGap_PositionExplained();
        if (!a) return (false, ea);
        var (b, eb) = BridgePartialGap_PositionUnexplained();
        if (!b) return (false, eb);
        var (c, ec) = NoBridgeSameAsRawWhenOverlayZero();
        if (!c) return (false, ec);
        return (true, null);
    }

    /// <summary>Raw journal lags broker by Δ; overlay Δ → effective journal matches broker; net flat.</summary>
    private static (bool, string?) BridgeClosesGap_PositionExplained()
    {
        const int brokerGross = 10;
        const int brokerNet = 10;
        const int journalRaw = 8;
        const int journalNet = 8;
        const int ovGross = 2;
        const int ovNet = 2;
        var ok = MismatchConvergenceBridgeProbeMath.IsPositionAggregateExplained(
            brokerGross, brokerNet, journalRaw, journalNet, ovGross, ovNet,
            out var effG, out var effN, out var rawD, out var effD);
        if (!ok) return (false, "Bridge1: expected position aggregate explained");
        if (effG != 10 || effN != 10) return (false, "Bridge1: effective journal");
        if (rawD != 2 || effD != 0) return (false, "Bridge1: diffs");
        return (true, null);
    }

    /// <summary>Overlay only partially closes gross gap → still unexplained.</summary>
    private static (bool, string?) BridgePartialGap_PositionUnexplained()
    {
        const int brokerGross = 10;
        const int brokerNet = 10;
        const int journalRaw = 8;
        const int journalNet = 8;
        const int ovGross = 1;
        const int ovNet = 1;
        var ok = MismatchConvergenceBridgeProbeMath.IsPositionAggregateExplained(
            brokerGross, brokerNet, journalRaw, journalNet, ovGross, ovNet,
            out _, out _, out _, out _);
        if (ok) return (false, "Bridge2: expected position aggregate NOT explained");
        return (true, null);
    }

    /// <summary>No bridge (0 overlay): effective equals raw; same alignment as without bridge path.</summary>
    private static (bool, string?) NoBridgeSameAsRawWhenOverlayZero()
    {
        const int brokerGross = 5;
        const int brokerNet = 5;
        const int journalRaw = 5;
        const int journalNet = 5;
        var ok = MismatchConvergenceBridgeProbeMath.IsPositionAggregateExplained(
            brokerGross, brokerNet, journalRaw, journalNet, 0, 0,
            out var effG, out var effN, out var rawD, out var effD);
        if (!ok) return (false, "Bridge3: aligned raw should explain");
        if (effG != journalRaw || effN != journalNet) return (false, "Bridge3: effective should match raw when overlay 0");
        if (rawD != 0 || effD != 0) return (false, "Bridge3: diffs should be 0");
        return (true, null);
    }
}
