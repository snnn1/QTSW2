// Cross-chart flatten coordination + verify debounce unit tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test FLATTEN_COORDINATION_TRACKER

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class FlattenCoordinationTrackerTests
{
    public static (bool Pass, string? Error) RunFlattenCoordinationTrackerTests()
    {
        var t = FlattenCoordinationTracker.Shared;
        t.ConfigureForTests(
            staleTtl: TimeSpan.FromSeconds(60),
            nonzeroThreshold: 2,
            maxVerifyRetries: 4,
            persistentCooldown: TimeSpan.FromSeconds(60));
        t.ClearAllForTests();

        var t0 = new DateTimeOffset(2026, 3, 17, 12, 0, 0, TimeSpan.Zero);
        const string acct = "SIM101";
        const string inst = "MNQ 03-26";
        var owner1 = "eng:aaa";
        var owner2 = "eng:bbb";

        // A — single owner: second instance skips
        var g1 = t.TryRequestFlattenEnqueue(acct, inst, owner1, "HOST1", "c1", t0, false,
            out var ep1, out _, out var asg1, out _, out _);
        if (g1 != FlattenEnqueueGateOutcome.Proceed || !asg1 || string.IsNullOrEmpty(ep1))
            return (false, "A: first should Proceed with owner assigned");
        var g2 = t.TryRequestFlattenEnqueue(acct, inst, owner2, "HOST2", "c2", t0.AddSeconds(1), false,
            out _, out _, out _, out _, out _);
        if (g2 != FlattenEnqueueGateOutcome.SecondaryInstanceSkip)
            return (false, "A: second instance should skip");
        if (t.Metrics.FlattenSecondarySkippedTotal < 1)
            return (false, "A: secondary metric");

        // B — takeover after stale TTL
        t.NotifyFlattenSubmitted(acct, inst, owner1, t0.AddSeconds(2));
        var staleTime = t0.AddSeconds(2 + 61);
        var g3 = t.TryRequestFlattenEnqueue(acct, inst, owner2, "HOST2", "c3", staleTime, false,
            out var ep3, out var prev, out _, out _, out var elap);
        if (g3 != FlattenEnqueueGateOutcome.TakeoverProceed)
            return (false, "B: expected takeover");
        if (string.IsNullOrEmpty(prev))
            return (false, "B: previous owner");
        if (elap < 60)
            return (false, "B: elapsed sec");
        if (string.IsNullOrEmpty(ep3) || ep3 == ep1)
            return (false, "B: new episode id");
        if (t.Metrics.FlattenOwnerTakeoverTotal < 1)
            return (false, "B: takeover metric");

        // C — verify debounce: first non-zero does not enqueue retry (DebounceExtendWindow)
        t.ClearAllForTests();
        t.TryRequestFlattenEnqueue(acct, inst, owner1, "H", "x", t0, false, out _, out _, out _, out _, out _);
        t.NotifyFlattenSubmitted(acct, inst, owner1, t0.AddSeconds(1));
        var d1 = t.ProcessVerifyWindow(acct, inst, owner1, 3, t0.AddSeconds(10), out _, out _, out var w1);
        if (d1 != FlattenVerifyProcessOutcome.DebounceExtendWindow || w1)
            return (false, "C: first non-zero should debounce");
        if (t.Metrics.FlattenVerifyDebouncedTotal < 1)
            return (false, "C: debounced metric");

        // D — second consecutive non-zero schedules retry
        var d2 = t.ProcessVerifyWindow(acct, inst, owner1, 3, t0.AddSeconds(20), out var inc2, out _, out var w2);
        if (d2 != FlattenVerifyProcessOutcome.EnqueueRetryFlatten || !inc2 || w2)
            return (false, "D: second non-zero should enqueue retry");

        // E — worsening bypass: first verify 2, second verify 5 → immediate EnqueueRetryFlatten
        t.ClearAllForTests();
        t.TryRequestFlattenEnqueue(acct, inst, owner1, "H", "x", t0, false, out _, out _, out _, out _, out _);
        t.NotifyFlattenSubmitted(acct, inst, owner1, t0.AddSeconds(1));
        t.ProcessVerifyWindow(acct, inst, owner1, 2, t0.AddSeconds(5), out _, out _, out _);
        var e1 = t.ProcessVerifyWindow(acct, inst, owner1, 5, t0.AddSeconds(15), out _, out _, out var we);
        if (e1 != FlattenVerifyProcessOutcome.EnqueueRetryFlatten || !we)
            return (false, "E: worsening should bypass debounce");

        // F — persistent failure blocks same owner; secondary skip; still-open debounce on metric path
        t.ClearAllForTests();
        t.ConfigureForTests(maxVerifyRetries: 1);
        t.TryRequestFlattenEnqueue(acct, inst, owner1, "H", "x", t0, false, out _, out _, out _, out _, out _);
        t.NotifyFlattenSubmitted(acct, inst, owner1, t0.AddSeconds(1));
        // max=1: debounce pair → retry scheduled (count=1); debounce pair → FailedPersistent on next threshold
        t.ProcessVerifyWindow(acct, inst, owner1, 1, t0.AddSeconds(2), out _, out _, out _);
        t.ProcessVerifyWindow(acct, inst, owner1, 1, t0.AddSeconds(3), out _, out _, out _);
        t.ProcessVerifyWindow(acct, inst, owner1, 1, t0.AddSeconds(4), out _, out _, out _);
        var fLast = t.ProcessVerifyWindow(acct, inst, owner1, 1, t0.AddSeconds(5), out _, out _, out _);
        if (fLast != FlattenVerifyProcessOutcome.FailedPersistent)
            return (false, "F: expected FailedPersistent");
        t.MarkFailedPersistent(acct, inst, owner1, t0.AddSeconds(6));
        var fBlock = t.TryRequestFlattenEnqueue(acct, inst, owner1, "H", "y", t0.AddSeconds(7), false,
            out _, out _, out _, out var stillOpen, out _);
        if (fBlock != FlattenEnqueueGateOutcome.FailedPersistentBlocked || !stillOpen)
            return (false, "F: owner should get persistent blocked + still-open flag");
        var fSkip = t.TryRequestFlattenEnqueue(acct, inst, owner2, "H2", "z", t0.AddSeconds(8), false,
            out _, out _, out _, out _, out _);
        if (fSkip != FlattenEnqueueGateOutcome.SecondaryInstanceSkip)
            return (false, "F: secondary should skip while persistent active");

        t.ConfigureForTests(
            staleTtl: FlattenCoordinationTracker.DefaultStaleOwnerTtl,
            nonzeroThreshold: FlattenCoordinationTracker.DefaultVerifyNonzeroThreshold,
            maxVerifyRetries: FlattenCoordinationTracker.DefaultMaxVerifyRetries,
            persistentCooldown: FlattenCoordinationTracker.DefaultPersistentStillOpenLogCooldown);
        t.ClearAllForTests();
        return (true, null);
    }
}
