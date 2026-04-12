// IEA adoption scan hardening: instrument gate, convergence, logging schema.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test ADOPTION_SCAN_HARDENING

using System;
using System.Collections.Generic;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class AdoptionScanHardeningTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var (a, ea) = TestForeignInstrumentSkip();
        if (!a) return (false, ea);
        var (b, eb) = TestSameInstrumentStaleConvergence();
        if (!b) return (false, eb);
        var (c, ec) = TestValidCandidateNoSuppress();
        if (!c) return (false, ec);
        var (d, ed) = TestNonConvergenceSuppression();
        if (!d) return (false, ed);
        var (e, ee) = TestEngineBaseSemanticEventType();
        if (!e) return (false, ee);
        return (true, null);
    }

    private static (bool, string?) TestForeignInstrumentSkip()
    {
        if (AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument("MNG", "MNQ"))
            return (false, "MNG vs MNQ should not match");
        if (!AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument("MNG", "MNG"))
            return (false, "MNG vs MNG should match");
        if (!AdoptionScanInstrumentGate.BrokerOrderMatchesExecutionInstrument("MES", "MES 03-26"))
            return (false, "MES vs MES 03-26 should match root");
        return (true, null);
    }

    private static (bool, string?) TestSameInstrumentStaleConvergence()
    {
        var c = new AdoptionReconciliationConvergence();
        var now = DateTimeOffset.UtcNow;
        var fp = "Working|false|false|S|abc";
        for (var i = 0; i < 3; i++)
        {
            c.RegisterEvaluation("oid1", now, fp, 4, 120, out var esc, out var sup, out _);
            if (esc || sup) return (false, "unexpected escalation/suppress before threshold");
        }
        c.RegisterEvaluation("oid1", now, fp, 4, 120, out var e4, out var s4, out _);
        if (!e4 || !s4) return (false, "expected escalation+suppress at threshold");
        return (true, null);
    }

    private static (bool, string?) TestValidCandidateNoSuppress()
    {
        var c = new AdoptionReconciliationConvergence();
        var now = DateTimeOffset.UtcNow;
        c.RegisterEvaluation("o2", now, "A", 4, 120, out _, out var s1, out _);
        if (s1) return (false, "first eval should not suppress");
        c.RegisterEvaluation("o2", now, "B", 4, 120, out var e, out var s2, out _);
        if (e || s2) return (false, "fingerprint change should reset streak");
        return (true, null);
    }

    private static (bool, string?) TestNonConvergenceSuppression()
    {
        var c = new AdoptionReconciliationConvergence();
        c.ResetForTests();
        var t0 = DateTimeOffset.Parse("2026-03-23T12:00:00Z");
        var fp = "X";
        for (var i = 0; i < 3; i++)
            c.RegisterEvaluation("x", t0.AddSeconds(i), fp, 4, 60, out _, out _, out _);
        c.RegisterEvaluation("x", t0.AddSeconds(4), fp, 4, 60, out _, out var sup1, out _);
        if (!sup1) return (false, "should suppress at threshold");
        if (!c.IsQuarantined("x", t0.AddSeconds(5)))
            return (false, "should be quarantined inside cooldown");
        if (!c.IsQuarantined("x", t0.AddSeconds(30)))
            return (false, "still quarantined before 60s");
        var tAfter = t0.AddSeconds(70);
        if (c.IsQuarantined("x", tAfter))
            return (false, "cooldown should expire");
        c.RegisterEvaluation("x", tAfter, fp, 4, 60, out _, out _, out _);
        if (c.IsQuarantined("x", tAfter.AddSeconds(1)))
            return (false, "after cooldown episode should not start quarantined");
        return (true, null);
    }

    private static (bool, string?) TestEngineBaseSemanticEventType()
    {
        var utc = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var evt = RobotEvents.EngineBase(utc, tradingDate: "", eventType: "ADOPTION_SCAN_START", state: "ENGINE",
            new { execution_instrument_key = "MES", iea_instance_id = 1 });
        if (evt is not Dictionary<string, object?> d)
            return (false, "expected dictionary event");
        if (!string.Equals(d["event_type"]?.ToString(), "ADOPTION_SCAN_START", StringComparison.Ordinal))
            return (false, $"event_type was {d.GetValueOrDefault("event_type")}");
        if (!string.Equals(d["state"]?.ToString(), "ENGINE", StringComparison.Ordinal))
            return (false, "state should be ENGINE");
        return (true, null);
    }
}
